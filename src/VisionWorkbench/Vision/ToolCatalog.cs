using VisionWorkbench.Models;

namespace VisionWorkbench.Vision;

public static class ToolCatalog
{
    public static IReadOnlyList<ToolDefinition> All { get; } = [
        new("halcon.threshold", "阈值分割", "HALCON", "提取指定灰度范围，输出二值图"),
        new("halcon.blob", "区域分析", "HALCON", "连接区域、筛选面积，按数量判断 OK/NG"),
        new("halcon.mean", "均值滤波", "HALCON", "平滑当前图像"),
        new("halcon.match", "形状模板匹配", "HALCON", "从 ROI 创建模板，定位并返回角度、分数"),
        new("halcon.measure", "区域尺寸测量", "HALCON", "提取亮区域，测量外接矩形，输出像素尺寸"),
        new("opencv.gray", "灰度转换", "OpenCV", "练习 OpenCvSharp 的图像转换"),
        new("opencv.gaussian", "高斯滤波", "OpenCV", "练习 OpenCvSharp 的滤波与 Mat 生命周期")
    ];

    public static VisionStep Create(string kind)
    {
        var definition = All.First(t => t.Kind == kind);
        var step = new VisionStep { Kind = kind, Name = definition.Name, Backend = definition.Backend };
        switch (kind)
        {
            case "halcon.threshold":
                Add(step, "min", "最小灰度", 100, 0, 255);
                Add(step, "max", "最大灰度", 255, 0, 255);
                break;
            case "halcon.blob":
                Add(step, "minArea", "最小面积", 100, 0, 100000000);
                Add(step, "maxArea", "最大面积", 100000000, 0, 100000000);
                Add(step, "minCount", "最少数量", 1, 0, 10000);
                Add(step, "maxCount", "最多数量", 10, 0, 10000);
                break;
            case "halcon.mean":
            case "opencv.gaussian":
                Add(step, "size", "核大小（奇数）", 5, 1, 99);
                break;
            case "halcon.match":
                Add(step, "score", "最低匹配分数", 0.6, 0, 1);
                Add(step, "angle", "角度搜索范围 ±°", 30, 0, 180);
                Add(step, "count", "最大匹配数量", 1, 1, 100);
                break;
            case "halcon.measure":
                Add(step, "min", "最小灰度", 100, 0, 255);
                Add(step, "minWidth", "最小宽度 px", 0, 0, 100000);
                Add(step, "maxWidth", "最大宽度 px", 100000, 0, 100000);
                break;
        }
        return step;
    }

    private static void Add(VisionStep step, string key, string label, double value, double min, double max) =>
        step.Parameters.Add(new() { Key = key, Label = label, Value = value, Minimum = min, Maximum = max });
}
