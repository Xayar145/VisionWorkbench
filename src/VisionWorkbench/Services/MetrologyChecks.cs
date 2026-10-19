using OpenCvSharp;

namespace VisionWorkbench.Services;

public static partial class SmokeChecks
{
    private static async Task CheckMetrology(string folder, List<object> checks)
    {
        string linePath = WriteImage(folder, "metrology-line.png", image =>
            Cv2.Rectangle(image, new Rect(0, 80, 160, 80), Scalar.All(230), -1));
        var line = new WorkbenchController();
        line.LoadImage(linePath);
        var lineStep = line.AddStep("halcon.metrology_line");
        line.SetRoi(lineStep.Id, new() { Enabled = true, X = 25, Y = 65, Width = 110, Height = 30 });
        var lineReport = await line.RunAsync();
        Require(lineReport.Success && lineReport.Accepted, "HALCON测量模型应找到合成图的水平边缘：" + string.Join("；", lineReport.Steps.Select(s => s.Message)));
        Require(Math.Abs(NumberMetric(lineReport, "长度") - 99) < 3, "有效卡尺拟合线段应接近99像素，实际：" + System.Text.Json.JsonSerializer.Serialize(lineReport));
        Require(Math.Abs(NumberMetric(lineReport, "起点Y") - 79.5) < 2 && Math.Abs(NumberMetric(lineReport, "终点Y") - 79.5) < 2, "测量直线应落在真实跳变边缘上");
        Require(line.Frame!.Overlays.Any(o => o.Shape == "line"), "直线测量应生成线形叠加结果");
        checks.Add(new { name = "HALCON直线测量模型真实拟合", passed = true, length = NumberMetric(lineReport, "长度") });

        string circlePath = WriteImage(folder, "metrology-circle.png", image => Cv2.Circle(image, new Point(80, 80), 35, Scalar.All(230), -1));
        var circle = new WorkbenchController();
        circle.LoadImage(circlePath);
        var circleStep = circle.AddStep("halcon.metrology_circle");
        circle.SetRoi(circleStep.Id, new() { Enabled = true, Shape = "circle", X = 42, Y = 42, Width = 77, Height = 77 });
        var circleReport = await circle.RunAsync();
        Require(circleReport.Success && circleReport.Accepted, "HALCON测量模型应找到合成图的圆边缘：" + string.Join("；", circleReport.Steps.Select(s => s.Message)));
        Require(Math.Abs(NumberMetric(circleReport, "圆心X") - 80) < 1.5 && Math.Abs(NumberMetric(circleReport, "圆心Y") - 80) < 1.5, "测量圆心误差应小于1.5像素");
        Require(Math.Abs(NumberMetric(circleReport, "直径") - 70) < 2, "测量直径应接近70像素，并非直接返回77像素ROI宽度");
        Require(circle.Frame!.Overlays.Any(o => o.Shape == "circle"), "圆测量应生成圆形叠加结果");
        checks.Add(new { name = "HALCON圆测量模型中心与直径精度", passed = true, diameter = NumberMetric(circleReport, "直径") });

        var demo = new WorkbenchController();
        demo.CreateMetrologyDemo();
        var demoReport = await demo.RunAsync();
        Require(demoReport.Accepted && Math.Abs(NumberMetric(demoReport, "直径") - 140) < 2, "完整测量示例应拟合直径约140像素的圆");
        string demoProject = Path.Combine(folder, "测量模型方案.json");
        demo.SaveProject(demoProject);
        var demoRestored = new WorkbenchController();
        demoRestored.OpenProject(demoProject);
        var restoredCircle = demoRestored.Document.Steps.Single(s => s.Kind == "halcon.metrology_circle");
        Require(restoredCircle.Roi.Shape == "circle" && restoredCircle.Roi.Width == 151 && restoredCircle.Get("threshold") == 20, "测量模型重开应保留圆ROI及卡尺参数");
        var restoredReport = await demoRestored.RunAsync();
        Require(restoredReport.Accepted && Math.Abs(NumberMetric(restoredReport, "直径") - 140) < 2, "测量模型方案重开后拟合结果应一致");
        checks.Add(new { name = "完整测量示例及方案重开", passed = true, diameter = NumberMetric(restoredReport, "直径"), project = "测量模型方案.json" });

        string blankPath = WriteImage(folder, "metrology-blank.png", image => image.SetTo(Scalar.All(24)));
        foreach (string kind in new[] { "halcon.metrology_line", "halcon.metrology_circle" })
        {
            var blank = new WorkbenchController();
            blank.LoadImage(blankPath);
            var step = blank.AddStep(kind);
            blank.SetRoi(step.Id, new() { Enabled = true, Shape = kind.EndsWith("circle") ? "circle" : "rectangle", X = 42, Y = 42, Width = 77, Height = 77 });
            var report = await blank.RunAsync();
            Require(report.Success && !report.Accepted, kind + " 无边缘应正常NG，而非执行失败或虚构尺寸");
            Require(blank.Frame!.Overlays.Count == 0, "无边缘不应残留拟合图形");
        }
        checks.Add(new { name = "测量模型无边缘正常NG", passed = true });
    }
}
