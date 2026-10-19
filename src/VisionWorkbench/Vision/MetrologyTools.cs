using HalconDotNet;
using VisionWorkbench.Models;

namespace VisionWorkbench.Vision;

public static class MetrologyTools
{
    public static bool Run(HObject image, VisionStep step, List<OverlayBox> overlays, List<MetricRow> metrics)
    {
        if (!step.Roi.Enabled) throw new InvalidOperationException("请先启用 ROI，定义测量模型的初始位置");
        var roi = step.Roi;
        bool circle = step.Kind == "halcon.metrology_circle";
        if (roi.Width < 3 || roi.Height < 3) throw new ArgumentException("测量模型 ROI 宽高至少为 3 像素");
        double row = roi.Y + (roi.Height - 1) / 2, col = roi.X + (roi.Width - 1) / 2;
        using var geometry = circle
            ? new HTuple(new[] { row, col, (Math.Min(roi.Width, roi.Height) - 1) / 2 })
            : new HTuple(new[] { row, roi.X, row, roi.X + roi.Width - 1 });
        HOperatorSet.CreateMetrologyModel(out HTuple model);
        try
        {
            HOperatorSet.GetImageSize(image, out HTuple width, out HTuple height);
            using (width) using (height) HOperatorSet.SetMetrologyModelImageSize(model, width, height);
            using var names = new HTuple(new[] { "measure_transition", "measure_select", "min_score" });
            using var values = new HTuple();
            values[0] = "all"; values[1] = "all"; values[2] = step.Get("score");
            HOperatorSet.AddMetrologyObjectGeneric(model, circle ? "circle" : "line", geometry,
                step.Get("length1"), step.Get("length2"), step.Get("sigma"), step.Get("threshold"), names, values, out HTuple index);
            using (index)
            {
                // 使用完整图像，让初始轮廓两侧的卡尺都能取得灰度。
                HOperatorSet.ApplyMetrologyModel(image, model);
                HOperatorSet.GetMetrologyObjectResult(model, index, "all", "result_type", "all_param", out HTuple result);
                using (result)
                {
                    if (result.Length == 0) return false;
                    HOperatorSet.GetMetrologyObjectResult(model, index, "all", "result_type", "score", out HTuple scores);
                    using (scores) metrics.Add(new("拟合分数", scores[0].D.ToString("F3")));
                    double size;
                    if (circle)
                    {
                        double y = result[0].D, x = result[1].D, radius = result[2].D;
                        size = radius * 2;
                        Add(metrics, "圆心X", x); Add(metrics, "圆心Y", y); Add(metrics, "半径", radius); Add(metrics, "直径", size);
                        overlays.Add(new(x - radius, y - radius, size, size, $"直径 {size:F2} px", "circle"));
                    }
                    else
                    {
                        double y1 = result[0].D, x1 = result[1].D, y2 = result[2].D, x2 = result[3].D;
                        size = Math.Sqrt(Math.Pow(x2 - x1, 2) + Math.Pow(y2 - y1, 2));
                        Add(metrics, "起点X", x1); Add(metrics, "起点Y", y1); Add(metrics, "终点X", x2); Add(metrics, "终点Y", y2); Add(metrics, "长度", size);
                        metrics.Add(new("角度", (Math.Atan2(y2 - y1, x2 - x1) * 180 / Math.PI).ToString("F2"), "°"));
                        overlays.Add(new(x1, y1, x2 - x1, y2 - y1, $"长度 {size:F2} px", "line"));
                    }
                    return size >= step.Get("minSize") && size <= step.Get("maxSize");
                }
            }
        }
        finally { HOperatorSet.ClearMetrologyModel(model); model.Dispose(); }
    }

    private static void Add(List<MetricRow> metrics, string name, double value) => metrics.Add(new(name, value.ToString("F2"), "px"));
}
