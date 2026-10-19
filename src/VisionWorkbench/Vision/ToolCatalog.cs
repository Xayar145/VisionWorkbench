using VisionWorkbench.Models;

namespace VisionWorkbench.Vision;

public static class ToolCatalog
{
    public static IReadOnlyList<ToolDefinition> All { get; } = [
        new("halcon.threshold", "阈值分割", "HALCON", "提取指定灰度范围，输出二值图"),
        new("halcon.blob", "区域分析", "HALCON", "连接区域、筛选面积，按数量判断 OK/NG"),
        new("halcon.mean", "均值滤波", "HALCON", "平滑当前图像"),
        new("halcon.median", "中值滤波", "HALCON", "抑制椒盐噪声"),
        new("halcon.gaussian", "高斯滤波", "HALCON", "高斯平滑图像"),
        new("halcon.invert", "灰度反转", "HALCON", "反转图像灰度"),
        new("halcon.emphasize", "图像锐化", "HALCON", "增强局部灰度对比"),
        new("halcon.open", "区域开运算", "HALCON", "去除亮区域中的细小结构"),
        new("halcon.close", "区域闭运算", "HALCON", "连接亮区域的小间隙"),
        new("halcon.fill", "填充孔洞", "HALCON", "填充亮区域内部孔洞"),
        new("halcon.edges", "边缘幅度", "HALCON", "Canny 滤波后的边缘幅度图"),
        new("halcon.intensity", "灰度统计", "HALCON", "统计 ROI 的平均灰度与标准差"),
        new("halcon.match", "形状模板匹配", "HALCON", "从 ROI 创建模板，定位并返回角度、分数"),
        new("halcon.ncc", "NCC 模板匹配", "HALCON", "基于灰度相关性定位模板"),
        new("halcon.measure", "区域尺寸测量", "HALCON", "测量亮区域外接矩形的像素尺寸"),
        new("halcon.metrology_line", "直线测量模型", "HALCON", "沿 ROI 中心水平线布置卡尺；长度为有效卡尺间拟合线段长度"),
        new("halcon.metrology_circle", "圆测量模型", "HALCON", "沿 ROI 内接圆布置卡尺，拟合亚像素圆"),
        new("opencv.gray", "灰度转换", "OpenCV", "练习 OpenCvSharp 的图像转换"),
        new("opencv.gaussian", "高斯滤波", "OpenCV", "练习 OpenCvSharp 的高斯滤波"),
        new("opencv.median", "中值滤波", "OpenCV", "练习 OpenCvSharp 的中值滤波"),
        new("opencv.canny", "Canny 边缘", "OpenCV", "练习双阈值边缘检测")
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
            case "halcon.gaussian":
            case "halcon.emphasize":
            case "opencv.gaussian":
            case "opencv.median":
                Add(step, "size", "核大小（奇数）", 5, kind == "halcon.gaussian" || kind == "opencv.median" ? 3 : 1, kind == "halcon.gaussian" ? 11 : 99);
                if (kind == "halcon.emphasize") Add(step, "factor", "增强系数", 1, 0, 10);
                break;
            case "halcon.median":
            case "halcon.open":
            case "halcon.close":
                Add(step, "radius", "半径 px", 3, 1, 99);
                break;
            case "halcon.edges":
                Add(step, "sigma", "高斯平滑 Sigma", 1, 0.1, 10);
                break;
            case "opencv.canny":
                Add(step, "low", "低阈值", 50, 0, 255);
                Add(step, "high", "高阈值", 150, 0, 255);
                break;
            case "halcon.intensity":
                Add(step, "minMean", "最小平均灰度", 0, 0, 255);
                Add(step, "maxMean", "最大平均灰度", 255, 0, 255);
                break;
            case "halcon.match":
            case "halcon.ncc":
                Add(step, "score", "最低匹配分数", 0.6, 0, 1);
                Add(step, "angle", "角度搜索范围 ±°", 30, 0, 180);
                Add(step, "count", "最大匹配数量", 1, 1, 100);
                break;
            case "halcon.metrology_line":
            case "halcon.metrology_circle":
                Add(step, "length1", "卡尺法向半长 px", 20, 1, 500);
                Add(step, "length2", "卡尺切向半宽 px", 5, 1, 100);
                Add(step, "sigma", "平滑 Sigma", 1, 0.4, 10);
                Add(step, "threshold", "边缘幅度阈值", 20, 1, 255);
                Add(step, "score", "最小有效卡尺比例", 0.5, 0.1, 1);
                Add(step, "minSize", kind == "halcon.metrology_line" ? "最小长度 px" : "最小直径 px", 0, 0, 100000);
                Add(step, "maxSize", kind == "halcon.metrology_line" ? "最大长度 px" : "最大直径 px", 100000, 0, 100000);
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
