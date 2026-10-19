using System.Globalization;
using HalconDotNet;
using OpenCvSharp;
using VisionWorkbench.Models;
using VisionWorkbench.Vision;

namespace VisionWorkbench.Services;

public static partial class SmokeChecks
{
    private static async Task CheckExpandedEngine(string folder, List<object> checks)
    {
        string whitePath = WriteImage(folder, "roi-white.png", image => image.SetTo(Scalar.All(200)));
        var circular = new WorkbenchController();
        circular.LoadImage(whitePath);
        var threshold = circular.AddStep("halcon.threshold");
        circular.SetRoi(threshold.Id, new() { Enabled = true, Shape = "circle", X = 40, Y = 40, Width = 80, Height = 80 });
        Require((await circular.RunAsync()).Success, "圆形 ROI 阈值分割应成功");
        Require(Pixel(circular, 40, 40) == 0 && Pixel(circular, 80, 80) == 255, "圆形 ROI 必须排除外接框四角，保留圆心");
        string roiProject = Path.Combine(folder, "圆形ROI方案.json");
        circular.SaveProject(roiProject);
        var reopened = new WorkbenchController();
        reopened.OpenProject(roiProject);
        Require(reopened.Document.Steps.Single().Roi.Shape == "circle", "方案重开应保留圆形 ROI");
        Require((await reopened.RunAsync()).Success && Pixel(reopened, 40, 40) == 0, "重开后仍应按圆形域处理");
        checks.Add(new { name = "圆形 ROI 真正裁域及方案持久化", passed = true });

        circular.AddStep("halcon.blob");
        var circleBlob = await circular.RunAsync();
        Require(circleBlob.Accepted && Metric(circleBlob, "区域数量") == "1", "圆形ROI二值化后区域分析只能得到圆内目标");
        Require(NumberMetric(circleBlob, "总面积") > 4500 && NumberMetric(circleBlob, "总面积") < 5300, "圆形ROI检测链不得把ROI外原图误作亮区域");
        checks.Add(new { name = "圆形ROI阈值到区域分析数据流", passed = true, area = NumberMetric(circleBlob, "总面积") });

        string patternPath = WriteImage(folder, "operator-pattern.png", image =>
        {
            image.SetTo(Scalar.All(0));
            Cv2.Rectangle(image, new Rect(40, 40, 80, 80), Scalar.All(210), -1);
            Cv2.Circle(image, new Point(80, 80), 12, Scalar.All(0), -1);
            image.Set(20, 20, (byte)255);
            image.Set(50, 50, (byte)0);
        });
        foreach (string kind in new[] { "halcon.median", "halcon.gaussian", "halcon.invert", "halcon.emphasize", "halcon.open", "halcon.close", "halcon.fill", "halcon.edges", "opencv.canny", "opencv.median" })
        {
            var test = new WorkbenchController();
            test.LoadImage(patternPath);
            byte[] before = test.Frame!.Pixels.ToArray();
            test.AddStep(kind);
            var report = await test.RunAsync();
            Require(report.Success, kind + " 执行失败：" + string.Join("；", report.Steps.Select(s => s.Message)));
            Require(test.Frame!.Width == 160 && test.Frame.Height == 160, kind + " 不应改变图像尺寸");
            Require(!before.SequenceEqual(test.Frame.Pixels), kind + " 应对测试图产生实际处理结果");
            if (kind is "halcon.median" or "halcon.open" or "opencv.median")
                Require(Pixel(test, 20, 20) < 100, kind + " 应去除孤立亮点");
            if (kind == "halcon.close") Require(Pixel(test, 50, 50) == 255 && Pixel(test, 80, 80) == 0, "闭运算应填平单像素孔洞、保留大孔洞");
            if (kind == "halcon.fill") Require(Pixel(test, 80, 80) == 255, "填孔应填实闭合区域中心");
            if (kind == "halcon.invert") Require(Pixel(test, 0, 0) == 255 && Pixel(test, 55, 55) == 45, "反色应输出255减原灰度");
            if (kind is "halcon.edges" or "opencv.canny") Require(Pixel(test, 0, 0) == 0, "边缘图的平坦背景应为零");
            checks.Add(new { name = "真实算子输出 " + kind, passed = true });
        }

        string noisePath = WriteImage(folder, "roi-noise.png", image =>
        {
            image.SetTo(Scalar.All(24));
            image.Set(80, 80, (byte)255);
            image.Set(41, 41, (byte)255);
        });
        foreach (string kind in new[] { "halcon.median", "opencv.median" })
        {
            var test = new WorkbenchController();
            test.LoadImage(noisePath);
            var step = test.AddStep(kind);
            test.SetRoi(step.Id, new() { Enabled = true, Shape = "circle", X = 40, Y = 40, Width = 80, Height = 80 });
            Require((await test.RunAsync()).Success, "圆 ROI 局部滤波失败：" + kind);
            Require(Pixel(test, 41, 41) == 255 && Pixel(test, 80, 80) == 24, "圆 ROI 滤波应保留框角孤点、移除圆内孤点：" + kind);
        }
        checks.Add(new { name = "HALCON/OpenCV 圆 ROI 局部滤波", passed = true });

        foreach (string kind in new[] { "halcon.threshold", "halcon.open", "halcon.close", "halcon.fill", "halcon.edges", "opencv.canny" })
        {
            var masked = new WorkbenchController();
            masked.LoadImage(patternPath);
            var step = masked.AddStep(kind);
            masked.SetRoi(step.Id, new() { Enabled = true, Shape = "circle", X = 40, Y = 40, Width = 80, Height = 80 });
            Require((await masked.RunAsync()).Success, kind + "圆ROI区域输出失败");
            for (int y = 0; y < 160; y++)
                for (int x = 0; x < 160; x++)
                    if (x < 40 || x >= 120 || y < 40 || y >= 120 || (x == 40 && y == 40))
                        Require(Pixel(masked, x, y) == 0, kind + "区域/边缘输出在ROI外必须为零");
        }
        checks.Add(new { name = "区域及边缘算子ROI外清零", passed = true });

        var intensity = new WorkbenchController();
        intensity.LoadImage(whitePath);
        intensity.AddStep("halcon.intensity");
        var statistics = await intensity.RunAsync();
        Require(statistics.Success && statistics.Steps.Single().Metrics.Count >= 2, "灰度统计应输出实际统计值");
        Require(statistics.Steps.Single().Metrics.Any(m => double.TryParse(m.Value, out var value) && Math.Abs(value - 200) < 0.01), "均匀图的灰度均值应为200");
        checks.Add(new { name = "灰度统计已知图像", passed = true });

        var ncc = new WorkbenchController();
        ncc.LoadImage(patternPath);
        var nccStep = ncc.AddStep("halcon.ncc");
        ncc.SetRoi(nccStep.Id, new() { Enabled = true, X = 30, Y = 30, Width = 100, Height = 100 });
        ncc.TrainTemplate(nccStep.Id);
        var nccReport = await ncc.RunAsync();
        Require(nccReport.Accepted && Metric(nccReport, "匹配数量") == "1", "NCC应在原图上找到训练目标");
        string nccPath = Path.Combine(folder, "NCC方案.json");
        ncc.SaveProject(nccPath);
        var nccRestored = new WorkbenchController();
        nccRestored.OpenProject(nccPath);
        Require((await nccRestored.RunAsync()).Accepted, "NCC模板资产保存重开失败");
        checks.Add(new { name = "NCC真实训练、定位与重开", passed = true });

        var roundTemplate = new WorkbenchController();
        roundTemplate.LoadImage(patternPath);
        var roundStep = roundTemplate.AddStep("halcon.ncc");
        roundTemplate.SetRoi(roundStep.Id, new() { Enabled = true, Shape = "circle", X = 30, Y = 30, Width = 100, Height = 100 });
        roundTemplate.TrainTemplate(roundStep.Id);
        using (var template = ImageBridge.ReadGray(roundStep.TemplatePath))
        {
            HOperatorSet.GetDomain(template, out HObject domain);
            using (domain)
            {
                HOperatorSet.AreaCenter(domain, out HTuple area, out HTuple row, out HTuple column);
                using (area) using (row) using (column)
                    Require(area.D > 7000 && area.D < 8500, "圆模板PNG重开后应保留圆形域，而非完整外接矩形");
            }
        }
        checks.Add(new { name = "圆形模板PNG域保留", passed = true });

        await CheckMetrology(folder, checks);

        var views = new WorkbenchController();
        views.LoadImage(patternPath);
        byte[] source = views.Frame!.Pixels.ToArray();
        views.AddStep("halcon.invert");
        await views.RunAsync();
        byte[] result = views.Frame!.Pixels.ToArray();
        views.ShowSource();
        Require(views.Frame!.Pixels.SequenceEqual(source), "原图视图应回到未处理图像");
        views.ShowResult();
        Require(views.Frame!.Pixels.SequenceEqual(result), "结果视图应恢复本次处理图像");
        views.NewProject();
        Require(views.Document.Steps.Count == 0 && views.Frame == null && views.ImagePath == "" && views.ProjectPath == "", "新建方案应清空当前流程与图像");
        checks.Add(new { name = "原图结果切换与新建方案", passed = true });
    }

    private static string WriteImage(string folder, string name, Action<Mat> paint)
    {
        string path = Path.Combine(folder, name);
        using var image = new Mat(160, 160, MatType.CV_8UC1, Scalar.All(0));
        paint(image);
        Cv2.ImWrite(path, image);
        return path;
    }

    private static byte Pixel(WorkbenchController controller, int x, int y) => controller.Frame!.Pixels[y * controller.Frame.Width + x];
    private static double NumberMetric(RunReport report, string name) => double.Parse(Metric(report, name), CultureInfo.CurrentCulture);
}
