using System.ComponentModel;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Extensions.AI;
using VisionWorkbench.Models;
using VisionWorkbench.Services;
using VisionWorkbench.Vision;

namespace VisionWorkbench.AI;

public class VisionTools
{
    private readonly WorkbenchController controller;
    private readonly Dispatcher dispatcher;
    private readonly SemaphoreSlim gate = new(1, 1);
    public event Action<string>? Logged;
    public IList<AITool> Functions { get; }
    public static IReadOnlyList<string> Names { get; } = ["ListAvailableTools", "GetWorkspaceState",
        "AddVisionTool", "SetToolParameter", "SetRoi", "RunPipeline", "GetRunResults"];

    public VisionTools(WorkbenchController controller, Dispatcher? dispatcher = null)
    {
        this.controller = controller;
        this.dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        Functions = [AIFunctionFactory.Create(ListAvailableTools), AIFunctionFactory.Create(GetWorkspaceState),
            AIFunctionFactory.Create(AddVisionTool), AIFunctionFactory.Create(SetToolParameter),
            AIFunctionFactory.Create(SetRoi), AIFunctionFactory.Create(RunPipeline), AIFunctionFactory.Create(GetRunResults)];
    }

    [Description("列出支持的 HALCON、OpenCV 工具及其类型标识和参数范围。")]
    public Task<string> ListAvailableTools(CancellationToken token = default) => Call("ListAvailableTools", new { }, token);
    [Description("读取当前方案、图像尺寸、步骤、参数、ROI、运行状态和最近运行编号，不包含图像内容和本地路径。")]
    public Task<string> GetWorkspaceState(CancellationToken token = default) => Call("GetWorkspaceState", new { }, token);
    [Description("添加一个支持的视觉工具，kind 必须使用 ListAvailableTools 返回的类型标识。")]
    public Task<string> AddVisionTool(string kind, CancellationToken token = default) => Call("AddVisionTool", new { kind }, token);
    [Description("修改已有步骤的数值参数，先通过 GetWorkspaceState 确认参数键名和范围。")]
    public Task<string> SetToolParameter(string stepId, string key, double value, CancellationToken token = default) =>
        Call("SetToolParameter", new { stepId, key, value }, token);
    [Description("设置矩形或圆形 ROI，坐标为原图像素的外接框，原点在左上角。shape 为 rectangle 或 circle，圆形宽高必须相等。")]
    public Task<string> SetRoi(string stepId, double x, double y, double width, double height, bool enabled = true,
        string shape = "rectangle", CancellationToken token = default) => Call("SetRoi", new { stepId, x, y, width, height, enabled, shape }, token);
    [Description("执行当前流程并等待实际结果，可选参数 throughStepId 表示运行到指定步骤。")]
    public Task<string> RunPipeline(string? throughStepId = null, CancellationToken token = default) =>
        Call("RunPipeline", new { throughStepId }, token);
    [Description("读取最近一次已完成的流程报告，报告仅反映该次运行时的状态。")]
    public Task<string> GetRunResults(CancellationToken token = default) => Call("GetRunResults", new { }, token);

    private Task<string> Call(string name, object arguments, CancellationToken token) =>
        InvokeAsync(name, JsonSerializer.SerializeToElement(arguments), token);

    public async Task<string> InvokeAsync(string name, JsonElement arguments, CancellationToken token = default)
    {
        if (!Names.Contains(name)) throw new ArgumentException("未知工具：" + name);
        await gate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            await dispatcher.InvokeAsync(() => Logged?.Invoke("工具开始：" + name));
            var result = await dispatcher.InvokeAsync(() => ExecuteAsync(name, arguments, token)).Task.Unwrap();
            await dispatcher.InvokeAsync(() => Logged?.Invoke("工具完成：" + name));
            return JsonSerializer.Serialize(result);
        }
        catch (Exception ex)
        {
            await dispatcher.InvokeAsync(() => Logged?.Invoke("工具失败：" + name + " · " + ex.Message));
            throw;
        }
        finally { gate.Release(); }
    }

    private async Task<object> ExecuteAsync(string name, JsonElement args, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string Text(string key) => args.GetProperty(key).GetString() ?? throw new ArgumentException(key + " 不能为空");
        double Number(string key)
        {
            var number = args.GetProperty(key).GetDouble();
            if (!double.IsFinite(number)) throw new ArgumentException(key + " 必须是有限数字");
            return number;
        }
        VisionStep Step() => controller.Document.Steps.FirstOrDefault(s => s.Id == Text("stepId"))
            ?? throw new ArgumentException("步骤不存在");
        switch (name)
        {
            case "ListAvailableTools": return ToolCatalog.All.Select(t => new
            {
                t.Kind, t.Name, t.Backend, t.Description, Parameters = ToolCatalog.Create(t.Kind).Parameters
            }).ToArray();
            case "GetWorkspaceState": return new
            {
                controller.Document.Name, controller.IsBusy, HasImage = controller.Frame != null,
                Width = controller.Frame?.Width, Height = controller.Frame?.Height,
                Steps = controller.Document.Steps.Select(s => new
                {
                    s.Id, s.Kind, s.Name, s.Backend, s.Enabled, s.Parameters, s.Roi,
                    HasTemplate = !string.IsNullOrEmpty(s.TemplatePath), s.Status, s.ElapsedMs
                }).ToArray(), LastRunId = controller.LastReport?.RunId
            };
            case "GetRunResults": return (object?)controller.LastReport ?? new { Message = "还没有运行结果" };
            case "RunPipeline":
                var through = args.TryGetProperty("throughStepId", out var value) ? value.GetString() : null;
                if (!string.IsNullOrEmpty(through) && !controller.Document.Steps.Any(s => s.Id == through))
                    throw new ArgumentException("指定的运行终点不存在");
                if (controller.IsBusy) throw new InvalidOperationException("流程正在运行");
                return await controller.RunAsync(through, token);
        }
        if (controller.IsBusy) throw new InvalidOperationException("运行期间不能修改流程");
        switch (name)
        {
            case "AddVisionTool":
                var kind = Text("kind");
                if (!ToolCatalog.All.Any(t => t.Kind == kind)) throw new ArgumentException("不支持的工具类型");
                return controller.AddStep(kind);
            case "SetToolParameter":
                var step = Step();
                var key = Text("key");
                var parameter = step.Parameters.FirstOrDefault(p => p.Key == key) ?? throw new ArgumentException("参数不存在");
                var number = Number("value");
                if (number < parameter.Minimum || number > parameter.Maximum) throw new ArgumentOutOfRangeException(key,
                    $"范围为 {parameter.Minimum} 至 {parameter.Maximum}");
                if (key == "size" && (number % 2 != 1)) throw new ArgumentException("核大小必须是奇数整数");
                controller.SetParameter(step.Id, key, number);
                return new { step.Id, Key = key, Value = number };
            case "SetRoi":
                var roiStep = Step();
                var frame = controller.Frame ?? throw new InvalidOperationException("请先载入图像");
                var roi = new RoiSpec { Shape = args.TryGetProperty("shape", out var shape) ? shape.GetString() ?? "rectangle" : roiStep.Roi.Shape,
                    X = Number("x"), Y = Number("y"), Width = Number("width"), Height = Number("height"),
                    Enabled = !args.TryGetProperty("enabled", out var enable) || enable.GetBoolean() };
                if (roi.X < 0 || roi.Y < 0 || roi.Width < 1 || roi.Height < 1 ||
                    roi.X + roi.Width > frame.Width || roi.Y + roi.Height > frame.Height)
                    throw new ArgumentException("ROI 必须位于图像范围内且宽高至少为 1");
                controller.SetRoi(roiStep.Id, roi);
                return new { roiStep.Id, Roi = roi };
            default: throw new ArgumentException("未知工具");
        }
    }
}
