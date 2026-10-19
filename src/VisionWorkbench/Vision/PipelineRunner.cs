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
                HObject? next = null;
                try
                {
                    Validate(step);
                    using HObject input = GetInput(current, step.Roi);
                    var metrics = new List<MetricRow>();
                    bool accepted = true;
                    string message = "完成";
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
                            HOperatorSet.MeanImage(current, out next, (int)step.Get("size"), (int)step.Get("size"));
                            break;
                        case "halcon.median":
                            HOperatorSet.MedianImage(current, out next, "circle", (int)step.Get("radius"), "mirrored");
                            break;
                        case "halcon.gaussian":
                            HOperatorSet.GaussFilter(current, out next, (int)step.Get("size"));
                            break;
                        case "halcon.invert":
                            HOperatorSet.InvertImage(current, out next);
                            break;
                        case "halcon.emphasize":
                            HOperatorSet.Emphasize(current, out next, (int)step.Get("size"), (int)step.Get("size"), step.Get("factor"));
                            break;
                        case "halcon.open":
                        case "halcon.close":
                        case "halcon.fill":
                            next = Morphology(input, current, step);
                            break;
                        case "halcon.edges":
                            HOperatorSet.EdgesImage(current, out HObject amplitude, out HObject direction, "canny", step.Get("sigma"), "nms", -1, -1);
                            using (amplitude) using (direction) HOperatorSet.ConvertImageType(amplitude, out next, "byte");
                            break;
                        case "halcon.intensity":
                            HOperatorSet.GetDomain(input, out HObject domain);
                            using (domain)
                            {
                                HOperatorSet.Intensity(domain, input, out HTuple mean, out HTuple deviation);
                                using (mean) using (deviation)
                                {
                                    metrics.Add(new("平均灰度", mean.D.ToString("F2")));
                                    metrics.Add(new("灰度标准差", deviation.D.ToString("F2")));
                                    accepted = mean.D >= step.Get("minMean") && mean.D <= step.Get("maxMean");
                                }
                            }
                            break;
                        case "halcon.metrology_line":
                        case "halcon.metrology_circle":
                            accepted = MetrologyTools.Run(current, step, overlays, metrics);
                            message = accepted ? "测量合格" : "未找到有效边缘或尺寸不合格";
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
                        case "halcon.ncc":
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
                        case "opencv.median":
                        case "opencv.canny":
                            using (var mat = ImageBridge.ToMat(current))
                            using (var filtered = new Mat())
                            {
                                if (step.Kind == "opencv.gaussian")
                                {
                                    int kernel = (int)step.Get("size");
                                    Cv2.GaussianBlur(mat, filtered, new Size(kernel, kernel), 0);
                                }
                                else if (step.Kind == "opencv.median") Cv2.MedianBlur(mat, filtered, (int)step.Get("size"));
                                else Cv2.Canny(mat, filtered, step.Get("low"), step.Get("high"));
                                next = ImageBridge.ToHalcon(filtered);
                            }
                            break;
                        default: throw new InvalidOperationException($"未知工具：{step.Kind}");
                    }
                    if (next != null)
                    {
                        if (step.Roi.Enabled)
                        {
                            using HObject processed = next;
                            // 区域和边缘图供后续分析使用，ROI 外不能混入原图灰度。
                            bool clearOutside = step.Kind is "halcon.threshold" or "halcon.open" or "halcon.close" or "halcon.fill" or "halcon.edges" or "opencv.canny";
                            next = ImageBridge.CopyRoi(current, processed, step.Roi, clearOutside);
                        }
                        current.Dispose(); current = next; next = null;
                    }
                    results.Add(new(step.Id, step.Name, true, accepted, timer.Elapsed.TotalMilliseconds, message, metrics));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    results.Add(new(step.Id, step.Name, false, false, timer.Elapsed.TotalMilliseconds, ex.Message, []));
                    break;
                }
                finally { next?.Dispose(); }
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
        if (step.Kind is "halcon.mean" or "halcon.gaussian" or "halcon.emphasize" or "opencv.gaussian" or "opencv.median")
        {
            double size = step.Get("size");
            if (size != Math.Truncate(size) || (int)size % 2 == 0) throw new ArgumentException("滤波核大小必须是正奇数");
        }
        if (step.Kind == "halcon.median" && step.Get("radius") != Math.Truncate(step.Get("radius"))) throw new ArgumentException("中值半径必须为整数");
        if (step.Kind == "opencv.median" && step.Get("size") < 3) throw new ArgumentException("中值滤波核至少为 3");
        if (step.Kind == "opencv.canny" && step.Get("low") > step.Get("high")) throw new ArgumentException("低阈值不能大于高阈值");
        if (step.Kind == "halcon.intensity" && step.Get("minMean") > step.Get("maxMean")) throw new ArgumentException("最小平均灰度不能大于最大值");
        if (step.Kind.StartsWith("halcon.metrology_") && step.Get("minSize") > step.Get("maxSize")) throw new ArgumentException("最小尺寸不能大于最大值");
        if (step.Kind == "halcon.threshold" && step.Get("min") > step.Get("max")) throw new ArgumentException("最小灰度不能大于最大灰度");
        if (step.Kind == "halcon.blob" && (step.Get("minArea") > step.Get("maxArea") || step.Get("minCount") > step.Get("maxCount")))
            throw new ArgumentException("最小面积/数量不能大于最大值");
        if (step.Kind == "halcon.measure" && step.Get("minWidth") > step.Get("maxWidth")) throw new ArgumentException("最小宽度不能大于最大宽度");
    }

    private static HObject GetInput(HObject image, RoiSpec roi)
    {
        if (!roi.Enabled) { HOperatorSet.CopyImage(image, out HObject copy); return copy; }
        if (roi.Shape is not ("rectangle" or "circle")) throw new ArgumentException("ROI 形状应为矩形或圆形");
        if (roi.Shape == "circle" && Math.Abs(roi.Width - roi.Height) > 0.01) throw new ArgumentException("圆形 ROI 的宽高必须相等");
        HOperatorSet.GetImageSize(image, out HTuple width, out HTuple height);
        using (width) using (height)
        {
            if (!double.IsFinite(roi.X + roi.Y + roi.Width + roi.Height) || roi.X < 0 || roi.Y < 0 || roi.Width < 1 || roi.Height < 1 || roi.X + roi.Width > width.I || roi.Y + roi.Height > height.I)
                throw new ArgumentException("ROI 必须位于图像内，且宽高至少为 1 像素");
        }
        using HObject rectangle = ImageBridge.CreateRoi(roi);
        HOperatorSet.ReduceDomain(image, rectangle, out HObject reduced); return reduced;
    }

    private static HObject Morphology(HObject input, HObject current, VisionStep step)
    {
        HOperatorSet.Threshold(input, out HObject region, 1, 255);
        using (region)
        {
            HObject processed;
            if (step.Kind == "halcon.open") HOperatorSet.OpeningCircle(region, out processed, step.Get("radius"));
            else if (step.Kind == "halcon.close") HOperatorSet.ClosingCircle(region, out processed, step.Get("radius"));
            else HOperatorSet.FillUp(region, out processed);
            using (processed)
            {
                HOperatorSet.GetImageSize(current, out HTuple width, out HTuple height);
                using (width) using (height)
                {
                    HOperatorSet.RegionToBin(processed, out HObject result, 255, 0, width, height);
                    return result;
                }
            }
        }
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
        HTuple model;
        if (step.Kind == "halcon.ncc") HOperatorSet.CreateNccModel(template, "auto", -angle, angle * 2, "auto", "use_polarity", out model);
        else HOperatorSet.CreateShapeModel(template, "auto", -angle, angle * 2, "auto", "auto", "use_polarity", "auto", "auto", out model);
        try
        {
            HTuple rows, cols, angles, scores;
            if (step.Kind == "halcon.ncc")
                HOperatorSet.FindNccModel(input, model, -angle, angle * 2, step.Get("score"), (int)step.Get("count"), 0.5, "true", 0,
                    out rows, out cols, out angles, out scores);
            else HOperatorSet.FindShapeModel(input, model, -angle, angle * 2, step.Get("score"), (int)step.Get("count"), 0.5, "least_squares", 0, 0.9,
                out rows, out cols, out angles, out scores);
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
        finally
        {
            if (step.Kind == "halcon.ncc") HOperatorSet.ClearNccModel(model);
            else HOperatorSet.ClearShapeModel(model);
            model.Dispose();
        }
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
