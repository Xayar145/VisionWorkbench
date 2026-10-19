using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenCvSharp;
using VisionWorkbench.Models;
using VisionWorkbench.Vision;

namespace VisionWorkbench.Services;

public sealed class WorkbenchController
{
    public static JsonSerializerOptions JsonOptions { get; } = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public ProjectDocument Document { get; private set; } = new();
    public PreviewFrame? Frame { get; private set; }
    public RunReport? LastReport { get; private set; }
    public bool IsBusy { get; private set; }
    public string ProjectPath { get; private set; } = "";
    public string ImagePath { get; private set; } = "";
    public event Action? StateChanged;
    public event Action<string>? Logged;
    private readonly PipelineRunner runner = new();
    private PreviewFrame? sourceFrame;
    private readonly string workFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VisionWorkbench", "Work");

    public WorkbenchController()
    {
        HalconRuntime.Configure();
        Directory.CreateDirectory(workFolder);
    }

    public void LoadImage(string path)
    {
        EnsureIdle();
        using var image = ImageBridge.ReadGray(path);
        Frame = sourceFrame = ImageBridge.ToFrame(image);
        ImagePath = Path.GetFullPath(path);
        Document.ImagePath = ImagePath;
        ResetResults();
        Log($"已加载 {Path.GetFileName(path)} · {Frame.Width} × {Frame.Height}");
        StateChanged?.Invoke();
    }

    public VisionStep AddStep(string kind)
    {
        EnsureIdle();
        var step = ToolCatalog.Create(kind);
        Document.Steps.Add(step);
        ResetResults();
        Log($"添加工具：{step.Name}");
        StateChanged?.Invoke();
        return step;
    }

    public void RemoveStep(string id)
    {
        EnsureIdle();
        Document.Steps.Remove(FindStep(id));
        ResetResults();
        StateChanged?.Invoke();
    }

    public void MoveStep(string id, int delta)
    {
        EnsureIdle();
        int index = Document.Steps.IndexOf(FindStep(id));
        int next = index + delta;
        if (next < 0 || next >= Document.Steps.Count) return;
        Document.Steps.Move(index, next);
        ResetResults();
        StateChanged?.Invoke();
    }

    public void SetParameter(string id, string key, double value)
    {
        EnsureIdle();
        var step = FindStep(id);
        var parameter = step.Parameters.FirstOrDefault(p => p.Key == key) ?? throw new ArgumentException($"参数不存在：{key}");
        if (!double.IsFinite(value) || value < parameter.Minimum || value > parameter.Maximum)
            throw new ArgumentException($"{parameter.Label} 必须在 {parameter.Minimum} 到 {parameter.Maximum} 之间");
        double previous = parameter.Value;
        parameter.Value = value;
        try { PipelineRunner.Validate(step); }
        catch { parameter.Value = previous; throw; }
        ResetResults();
        Log($"{step.Name} · {parameter.Label} = {value}");
        StateChanged?.Invoke();
    }

    public void SetRoi(string id, RoiSpec roi)
    {
        EnsureIdle();
        if (roi.Enabled)
        {
            if (Frame == null) throw new InvalidOperationException("请先加载图像");
            if (!double.IsFinite(roi.X + roi.Y + roi.Width + roi.Height) || roi.X < 0 || roi.Y < 0 || roi.Width < 1 || roi.Height < 1 || roi.X + roi.Width > Frame.Width || roi.Y + roi.Height > Frame.Height)
                throw new ArgumentException("ROI 必须位于图像范围内");
        }
        FindStep(id).Roi = roi;
        ResetResults();
        StateChanged?.Invoke();
    }

    public void TrainTemplate(string id)
    {
        EnsureIdle();
        var step = FindStep(id);
        if (step.Kind != "halcon.match") throw new InvalidOperationException("只有形状模板匹配工具支持创建模板");
        string path = Path.Combine(workFolder, $"template-{step.Id}.png");
        PipelineRunner.Train(ImagePath, step.Roi, path);
        step.TemplatePath = path;
        // 模板 ROI 用于建模，搜索默认在整幅图中进行。
        step.Roi.Enabled = false;
        ResetResults();
        Log("模板已创建，搜索范围切换为整幅图像");
        StateChanged?.Invoke();
    }

    public async Task<RunReport> RunAsync(string? throughStepId = null, CancellationToken token = default)
    {
        EnsureReady();
        var steps = Snapshot(throughStepId);
        IsBusy = true;
        ResetResults();
        StateChanged?.Invoke();
        try
        {
            var result = await Task.Run(() => runner.Run(ImagePath, steps, token), token);
            Apply(result.Report, result.Frame);
            return result.Report;
        }
        catch (OperationCanceledException) { Log("运行已取消"); throw; }
        finally { IsBusy = false; StateChanged?.Invoke(); }
    }

    public async Task BatchAsync(string folder, string csvPath, CancellationToken token = default)
    {
        EnsureReady();
        var files = Directory.EnumerateFiles(folder).Where(IsImage).OrderBy(p => p).ToArray();
        if (files.Length == 0) throw new InvalidOperationException("文件夹中没有支持的图像");
        var steps = Snapshot(null);
        IsBusy = true;
        ResetResults();
        StateChanged?.Invoke();
        try
        {
            using var writer = new StreamWriter(csvPath, false, new UTF8Encoding(true));
            await writer.WriteLineAsync("文件,运行编号,执行成功,判定,耗时ms,详情");
            foreach (string file in files)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var result = await Task.Run(() => runner.Run(file, steps, token), token);
                    await writer.WriteLineAsync(string.Join(",", Csv(Path.GetFileName(file)), result.Report.RunId, result.Report.Success,
                        result.Report.Accepted ? "OK" : "NG", result.Report.ElapsedMs.ToString("F2", CultureInfo.InvariantCulture), Csv(JsonSerializer.Serialize(result.Report.Steps))));
                    Log($"批量 {Path.GetFileName(file)} · {(result.Report.Accepted ? "OK" : "NG")} · {result.Report.ElapsedMs:F1} ms");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await writer.WriteLineAsync($"{Csv(Path.GetFileName(file))},,false,ERROR,,{Csv(ex.Message)}");
                    Log($"批量图像失败 {Path.GetFileName(file)}：{ex.Message}");
                }
            }
            Log($"批量完成 · {files.Length} 张 · {csvPath}");
        }
        finally { IsBusy = false; StateChanged?.Invoke(); }
    }

    public void SaveProject(string path)
    {
        EnsureIdle();
        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string assets = Path.Combine(folder, Path.GetFileNameWithoutExtension(path) + ".assets");
        Directory.CreateDirectory(assets);
        var saved = Clone(Document);
        if (File.Exists(ImagePath))
        {
            string target = Path.Combine(assets, "source" + Path.GetExtension(ImagePath));
            CopyAsset(ImagePath, target);
            saved.ImagePath = Path.GetRelativePath(folder, target);
        }
        foreach (var step in saved.Steps.Where(s => s.TemplatePath.Length > 0))
        {
            string target = Path.Combine(assets, $"template-{step.Id}.png");
            CopyAsset(step.TemplatePath, target);
            step.TemplatePath = Path.GetRelativePath(folder, target);
        }
        saved.Name = Path.GetFileNameWithoutExtension(path);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(saved, JsonOptions), Encoding.UTF8);
        File.Move(temp, path, true);
        Document.Name = saved.Name;
        ProjectPath = Path.GetFullPath(path);
        Log($"方案已保存：{Path.GetFileName(path)}");
        StateChanged?.Invoke();
    }

    public void OpenProject(string path)
    {
        EnsureIdle();
        var loaded = JsonSerializer.Deserialize<ProjectDocument>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException("方案文件为空");
        if (loaded.FormatVersion != 1) throw new InvalidDataException("不支持此方案版本");
        if (loaded.Steps.Select(s => s.Id).Distinct().Count() != loaded.Steps.Count) throw new InvalidDataException("工具编号重复");
        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        foreach (var step in loaded.Steps)
        {
            var expected = ToolCatalog.Create(step.Kind);
            if (!step.Parameters.Select(p => p.Key).Order().SequenceEqual(expected.Parameters.Select(p => p.Key).Order()))
                throw new InvalidDataException($"{step.Name} 的参数与工具定义不匹配");
            // 参数约束来自应用，不信任方案文件中提供的上下界。
            foreach (var p in step.Parameters)
            {
                var definition = expected.Parameters.First(x => x.Key == p.Key);
                p.Minimum = definition.Minimum; p.Maximum = definition.Maximum; p.Label = definition.Label;
            }
            PipelineRunner.Validate(step);
            if (step.TemplatePath.Length > 0) step.TemplatePath = Path.GetFullPath(step.TemplatePath, folder);
        }
        string imagePath = loaded.ImagePath.Length > 0 ? Path.GetFullPath(loaded.ImagePath, folder) : "";
        PreviewFrame? frame = null;
        if (imagePath.Length > 0)
        {
            using var image = ImageBridge.ReadGray(imagePath);
            frame = ImageBridge.ToFrame(image);
        }
        Document = loaded;
        Document.ImagePath = imagePath;
        ImagePath = imagePath;
        Frame = sourceFrame = frame;
        ProjectPath = Path.GetFullPath(path);
        LastReport = null;
        Log($"已打开方案：{loaded.Name}");
        StateChanged?.Invoke();
    }

    public void CreateDemo()
    {
        EnsureIdle();
        string imagePath = Path.Combine(workFolder, "demo-parts.png");
        using (var mat = new Mat(400, 640, MatType.CV_8UC1, new Scalar(24)))
        {
            Cv2.Rectangle(mat, new Rect(80, 80, 60, 60), new Scalar(205), -1);
            Cv2.Circle(mat, new Point(300, 130), 30, new Scalar(230), -1);
            Cv2.Rectangle(mat, new Rect(450, 240, 100, 50), new Scalar(185), -1);
            Cv2.ImWrite(imagePath, mat);
        }
        Document = new() { Name = "三零件计数" };
        ProjectPath = "";
        LoadImage(imagePath);
        AddStep("halcon.threshold");
        var blob = AddStep("halcon.blob");
        SetParameter(blob.Id, "minCount", 3);
        SetParameter(blob.Id, "maxCount", 3);
        Log("示例就绪：预期 3 个区域，判定 OK");
    }

    public VisionStep FindStep(string id) => Document.Steps.FirstOrDefault(s => s.Id == id) ?? throw new ArgumentException($"工具不存在：{id}");
    public void Log(string message) => Logged?.Invoke(message);
    public void NotifyEdited()
    {
        EnsureIdle();
        ResetResults();
        StateChanged?.Invoke();
    }

    private List<VisionStep> Snapshot(string? throughId)
    {
        var steps = Clone(Document).Steps.ToList();
        if (throughId != null)
        {
            int index = steps.FindIndex(s => s.Id == throughId);
            if (index < 0) throw new ArgumentException("选中的工具不存在");
            steps = steps.Take(index + 1).ToList();
        }
        return steps;
    }
    private void Apply(RunReport report, PreviewFrame frame)
    {
        LastReport = report;
        Frame = frame;
        foreach (var r in report.Steps)
        {
            var step = FindStep(r.StepId);
            step.Status = !r.Success ? "错误" : r.Accepted ? "OK" : "NG";
            step.ElapsedMs = r.ElapsedMs;
        }
        Log($"运行 {report.RunId} · {(report.Accepted ? "OK" : "NG")} · {report.ElapsedMs:F1} ms");
        StateChanged?.Invoke();
    }
    private void ResetResults()
    {
        LastReport = null;
        Frame = sourceFrame;
        foreach (var step in Document.Steps) { step.Status = "待运行"; step.ElapsedMs = 0; }
    }
    private void EnsureIdle() { if (IsBusy) throw new InvalidOperationException("流程正在运行，请先停止或等待完成"); }
    private void EnsureReady()
    {
        EnsureIdle();
        if (!File.Exists(ImagePath)) throw new InvalidOperationException("请先加载图像");
        if (!Document.Steps.Any(s => s.Enabled)) throw new InvalidOperationException("请添加并启用至少一个工具");
    }
    private static ProjectDocument Clone(ProjectDocument doc) => JsonSerializer.Deserialize<ProjectDocument>(JsonSerializer.Serialize(doc, JsonOptions), JsonOptions)!;
    private static bool IsImage(string path) => new[] { ".png", ".bmp", ".jpg", ".jpeg", ".tif", ".tiff" }.Contains(Path.GetExtension(path).ToLowerInvariant());
    private static string Csv(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";
    private static void CopyAsset(string source, string target) { if (!Path.GetFullPath(source).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) File.Copy(source, target, true); }
}
