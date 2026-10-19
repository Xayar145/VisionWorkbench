using System.Diagnostics;
using HalconDotNet;
using OpenCvSharp;
using VisionWorkbench.Models;

namespace VisionWorkbench.Vision;

public sealed class PipelineRunner
{
    public (RunReport Report, PreviewFrame Frame) Run(string imagePath, IReadOnlyList<VisionStep> steps, CancellationToken token)
    {
        var started = DateTime.Now;
        var watch = Stopwatch.StartNew();
        var results = new List<StepResult>();
        var overlays = new List<OverlayBox>();
        using HObject original = ImageBridge.ReadGray(imagePath);
        HOperatorSet.CopyImage(original, out HObject current);
        try
        {
            foreach (var step in steps.Where(s => s.Enabled))
            {
                token.ThrowIfCancellationRequested();
                var timer = Stopwatch.StartNew();
                try
                {
                    Validate(step);
                    using HObject input = GetInput(current, step.Roi);
                    var metrics = new List<MetricRow>();
                    bool accepted = true;
                    string message = "完成";
                    HObject? next = null;
                    overlays.Clear();
                    switch (step.Kind)
                    {
                        case "halcon.threshold":
                            HOperatorSet.Threshold(input, out HObject region, step.Get("min"), step.Get("max"));
                            using (region)
                            {
                                HOperatorSet.GetImageSize(current, out HTuple w, out HTuple h);
                                using (w) using (h) HOperatorSet.RegionToBin(region, out next, 255, 0, w, h);
                            }
                            break;
                        case "halcon.blob":
                            HOperatorSet.Threshold(input, out HObject bright, 1, 255);
                            using (bright)
                            {
                                HOperatorSet.Connection(bright, out HObject connected);
                                using (connected)
                                {
                                    HOperatorSet.SelectShape(connected, out HObject selected, "area", "and", step.Get("minArea"), step.Get("maxArea"));
                                    using (selected)
                                    {
                                        HOperatorSet.CountObj(selected, out HTuple count);
                                        using (count)
                                        {
                                            accepted = count.I >= step.Get("minCount") && count.I <= step.Get("maxCount");
                                            metrics.Add(new("区域数量", count.I.ToString(), "个"));
                                            AddBoxes(selected, overlays);
                                            HOperatorSet.AreaCenter(selected, out HTuple areas, out HTuple rows, out HTuple cols);
                                            using (areas) using (rows) using (cols)
                                                metrics.Add(new("总面积", areas.ToDArr().Sum().ToString("F0"), "px²"));
                                        }
                                    }
                                }
                            }
                            break;
                        case "halcon.mean":
                            int size = (int)step.Get("size");
                            HOperatorSet.MeanImage(current, out next, size, size);
                            if (step.Roi.Enabled)
                            {
                                using HObject filtered = next;
                                next = ImageBridge.CopyRoi(current, filtered, step.Roi);
                            }
                            break;
                        case "halcon.measure":
                            HOperatorSet.Threshold(input, out HObject measured, step.Get("min"), 255);
                            using (measured)
                            {
                                HOperatorSet.AreaCenter(measured, out HTuple area, out HTuple row, out HTuple col);
                                using (area) using (row) using (col)
                                {
                                    if (area.D == 0) { accepted = false; message = "未找到测量区域"; break; }
                                }
                                HOperatorSet.SmallestRectangle1(measured, out HTuple r1, out HTuple c1, out HTuple r2, out HTuple c2);
                                using (r1) using (c1) using (r2) using (c2)
                                {
                                    double width = c2.D - c1.D + 1, height = r2.D - r1.D + 1;
                                    accepted = width >= step.Get("minWidth") && width <= step.Get("maxWidth");
                                    metrics.Add(new("宽度", width.ToString("F2"), "px"));
                                    metrics.Add(new("高度", height.ToString("F2"), "px"));
                                    overlays.Add(new(c1.D, r1.D, width, height, $"{width:F0} × {height:F0} px"));
                                }
                            }
                            break;
                        case "halcon.match":
                            accepted = Match(input, step, overlays, metrics);
                            message = accepted ? "定位成功" : "未找到匹配";
                            break;
                        case "opencv.gray":
                            using (var mat = ImageBridge.ToMat(input))
                            using (var bgr = new Mat())
                            using (var gray = new Mat())
                            {
                                Cv2.CvtColor(mat, bgr, ColorConversionCodes.GRAY2BGR);
                                Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
                                next = ImageBridge.ToHalcon(gray);
                            }
                            break;
                        case "opencv.gaussian":
                            using (var mat = ImageBridge.ToMat(input))
                            using (var filtered = new Mat())
                            {
                                int kernel = (int)step.Get("size");
                                if (step.Roi.Enabled)
                                {
                                    // OpenCV 不识别 HALCON 的缩小域，显式限制处理矩形。
                                    var roi = step.Roi;
                                    var rect = new Rect((int)roi.X, (int)roi.Y, (int)roi.Width, (int)roi.Height);
                                    using var target = new Mat(mat, rect);
                                    Cv2.GaussianBlur(target, filtered, new Size(kernel, kernel), 0);
                                    filtered.CopyTo(target);
                                    next = ImageBridge.ToHalcon(mat);
                                }
                                else
                                {
                                    Cv2.GaussianBlur(mat, filtered, new Size(kernel, kernel), 0);
                                    next = ImageBridge.ToHalcon(filtered);
                                }
                            }
                            break;
                        default: throw new InvalidOperationException($"未知工具：{step.Kind}");
                    }
                    if (next != null) { current.Dispose(); current = next; }
                    results.Add(new(step.Id, step.Name, true, accepted, timer.Elapsed.TotalMilliseconds, message, metrics));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    results.Add(new(step.Id, step.Name, false, false, timer.Elapsed.TotalMilliseconds, ex.Message, []));
                    break;
                }
            }
            token.ThrowIfCancellationRequested();
            var report = new RunReport(Guid.NewGuid().ToString("N")[..12], started,
                results.All(r => r.Success), results.Count > 0 && results.All(r => r.Success && r.Accepted), watch.Elapsed.TotalMilliseconds, results);
            return (report, ImageBridge.ToFrame(current, overlays));
        }
        finally { current.Dispose(); }
    }

    public static void Validate(VisionStep step)
    {
        foreach (var p in step.Parameters)
            if (!double.IsFinite(p.Value) || p.Value < p.Minimum || p.Value > p.Maximum)
                throw new ArgumentException($"{p.Label} 必须在 {p.Minimum} 到 {p.Maximum} 之间");
        if (step.Kind is "halcon.mean" or "opencv.gaussian")
        {
            double size = step.Get("size");
            if (size != Math.Truncate(size) || (int)size % 2 == 0) throw new ArgumentException("滤波核大小必须是正奇数");
        }
        if (step.Kind == "halcon.threshold" && step.Get("min") > step.Get("max")) throw new ArgumentException("最小灰度不能大于最大灰度");
        if (step.Kind == "halcon.blob" && (step.Get("minArea") > step.Get("maxArea") || step.Get("minCount") > step.Get("maxCount")))
            throw new ArgumentException("最小面积/数量不能大于最大值");
        if (step.Kind == "halcon.measure" && step.Get("minWidth") > step.Get("maxWidth")) throw new ArgumentException("最小宽度不能大于最大宽度");
    }

    private static HObject GetInput(HObject image, RoiSpec roi)
    {
        if (!roi.Enabled) { HOperatorSet.CopyImage(image, out HObject copy); return copy; }
        HOperatorSet.GetImageSize(image, out HTuple width, out HTuple height);
        using (width) using (height)
        {
            if (!double.IsFinite(roi.X + roi.Y + roi.Width + roi.Height) || roi.X < 0 || roi.Y < 0 || roi.Width < 1 || roi.Height < 1 || roi.X + roi.Width > width.I || roi.Y + roi.Height > height.I)
                throw new ArgumentException("ROI 必须位于图像内，且宽高至少为 1 像素");
        }
        HOperatorSet.GenRectangle1(out HObject rectangle, roi.Y, roi.X, roi.Y + roi.Height - 1, roi.X + roi.Width - 1);
        using (rectangle) { HOperatorSet.ReduceDomain(image, rectangle, out HObject reduced); return reduced; }
    }

    private static void AddBoxes(HObject regions, List<OverlayBox> overlays)
    {
        HOperatorSet.SmallestRectangle1(regions, out HTuple r1, out HTuple c1, out HTuple r2, out HTuple c2);
        using (r1) using (c1) using (r2) using (c2)
            for (int i = 0; i < r1.Length; i++) overlays.Add(new(c1[i].D, r1[i].D, c2[i].D - c1[i].D + 1, r2[i].D - r1[i].D + 1, $"#{i + 1}"));
    }

    private static bool Match(HObject input, VisionStep step, List<OverlayBox> overlays, List<MetricRow> metrics)
    {
        if (!File.Exists(step.TemplatePath)) throw new InvalidOperationException("请先绘制 ROI 并创建模板");
        using HObject template = ImageBridge.ReadGray(step.TemplatePath);
        double angle = step.Get("angle") * Math.PI / 180;
        HOperatorSet.CreateShapeModel(template, "auto", -angle, angle * 2, "auto", "auto", "use_polarity", "auto", "auto", out HTuple model);
        try
        {
            HOperatorSet.FindShapeModel(input, model, -angle, angle * 2, step.Get("score"), (int)step.Get("count"), 0.5, "least_squares", 0, 0.9,
                out HTuple rows, out HTuple cols, out HTuple angles, out HTuple scores);
            using (rows) using (cols) using (angles) using (scores)
            {
                metrics.Add(new("匹配数量", scores.Length.ToString(), "个"));
                HOperatorSet.GetImageSize(template, out HTuple width, out HTuple height);
                using (width) using (height)
                    for (int i = 0; i < scores.Length; i++)
                    {
                        metrics.Add(new($"匹配 {i + 1}", $"X={cols[i].D:F2}, Y={rows[i].D:F2}, θ={angles[i].D * 180 / Math.PI:F2}°, 分数={scores[i].D:F3}"));
                        overlays.Add(new(cols[i].D - width.I / 2.0, rows[i].D - height.I / 2.0, width.I, height.I, $"{scores[i].D:F2}"));
                    }
                return scores.Length > 0;
            }
        }
        finally { HOperatorSet.ClearShapeModel(model); model.Dispose(); }
    }

    public static void Train(string imagePath, RoiSpec roi, string destination)
    {
        if (!roi.Enabled) throw new InvalidOperationException("请先启用并绘制模板 ROI");
        using HObject image = ImageBridge.ReadGray(imagePath);
        using HObject reduced = GetInput(image, roi);
        HOperatorSet.CropDomain(reduced, out HObject cropped);
        using (cropped) HOperatorSet.WriteImage(cropped, "png", 0, destination);
    }
}
