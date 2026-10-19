using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VisionWorkbench.AI;
using VisionWorkbench.Models;

namespace VisionWorkbench.Services;

public static partial class SmokeChecks
{
    public static async Task<int> Run(string[] args)
    {
        int outputIndex = Array.IndexOf(args, "--output");
        string folder = outputIndex >= 0 && outputIndex + 1 < args.Length ? Path.GetFullPath(args[outputIndex + 1]) : Path.GetFullPath("artifacts/check");
        Directory.CreateDirectory(folder);
        var checks = new List<object>();
        try
        {
            if (args.Contains("--ui-smoke")) await CheckUi(folder, checks);
            else await CheckEngine(folder, checks);
            File.WriteAllText(Path.Combine(folder, "checks.json"), JsonSerializer.Serialize(new { success = true, checks }, WorkbenchController.JsonOptions));
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(folder, "checks.json"), JsonSerializer.Serialize(new { success = false, checks, error = ex.ToString() }, WorkbenchController.JsonOptions));
            return 1;
        }
    }

    private static async Task CheckEngine(string folder, List<object> checks)
    {
        var controller = new WorkbenchController();
        controller.CreateDemo();
        var first = await controller.RunAsync();
        Require(first.Success && first.Accepted && Metric(first, "区域数量") == "3", "HALCON 示例计数应为 3");
        checks.Add(new { name = "HALCON 区域分析", passed = true, first.RunId, count = 3 });

        string project = Path.Combine(folder, "示例方案.json");
        controller.SaveProject(project);
        var restored = new WorkbenchController();
        restored.OpenProject(project);
        var again = await restored.RunAsync();
        Require(again.Accepted && Metric(again, "区域数量") == "3", "保存、重开后结果不一致");
        checks.Add(new { name = "方案及图像资产重开", passed = true });

        var blob = restored.Document.Steps[1];
        restored.SetParameter(blob.Id, "maxCount", 4);
        restored.SetParameter(blob.Id, "minCount", 4);
        var ng = await restored.RunAsync();
        Require(ng.Success && !ng.Accepted, "数量超出范围必须判定 NG");
        restored.SetParameter(blob.Id, "minCount", 3);
        restored.SetParameter(blob.Id, "maxCount", 3);
        checks.Add(new { name = "检测 NG 与执行失败区分", passed = true });

        var threshold = restored.Document.Steps[0];
        restored.SetParameter(threshold.Id, "min", 250);
        var empty = await restored.RunAsync();
        Require(empty.Success && !empty.Accepted && Metric(empty, "区域数量") == "0", "无区域应正常返回数量 0 和 NG");
        restored.SetParameter(threshold.Id, "min", 100);
        checks.Add(new { name = "空区域正常判定", passed = true });

        var cv = restored.AddStep("opencv.gaussian");
        restored.MoveStep(cv.Id, -1);
        restored.MoveStep(cv.Id, -1);
        var cvRun = await restored.RunAsync();
        Require(cvRun.Success && cvRun.Accepted, "OpenCV 到 HALCON 的图像传递失败");
        restored.RemoveStep(cv.Id);
        checks.Add(new { name = "OpenCV 滤波与 HALCON 混合流程", passed = true });

        foreach (string kind in new[] { "halcon.mean", "opencv.gaussian" })
        {
            var local = new WorkbenchController();
            local.LoadImage(controller.ImagePath);
            byte[] before = local.Frame!.Pixels.ToArray();
            var filter = local.AddStep(kind);
            local.SetRoi(filter.Id, new() { Enabled = true, X = 75, Y = 75, Width = 30, Height = 30 });
            Require((await local.RunAsync()).Success, kind + " 的 ROI 处理失败");
            byte[] after = local.Frame!.Pixels;
            bool changedInside = false;
            for (int y = 0; y < 400; y++)
                for (int x = 0; x < 640; x++)
                {
                    bool inside = x >= 75 && x < 105 && y >= 75 && y < 105;
                    if (!inside) Require(before[y * 640 + x] == after[y * 640 + x], "滤波不能改变 ROI 外像素");
                    else if (before[y * 640 + x] != after[y * 640 + x]) changedInside = true;
                }
            Require(changedInside, "滤波应改变 ROI 内边缘像素");
        }
        checks.Add(new { name = "HALCON 与 OpenCV 的 ROI 局部处理", passed = true });

        var measure = new WorkbenchController();
        measure.LoadImage(controller.ImagePath);
        var measurement = measure.AddStep("halcon.measure");
        measure.SetRoi(measurement.Id, new() { Enabled = true, X = 440, Y = 230, Width = 120, Height = 70 });
        var measured = await measure.RunAsync();
        Require(measured.Accepted && Metric(measured, "宽度") == "100.00", "HALCON 像素测量宽度应为 100");
        checks.Add(new { name = "矩形 ROI 与像素测量", passed = true, width = 100 });

        var match = new WorkbenchController();
        match.LoadImage(controller.ImagePath);
        var matching = match.AddStep("halcon.match");
        match.SetRoi(matching.Id, new() { Enabled = true, X = 70, Y = 70, Width = 80, Height = 80 });
        match.TrainTemplate(matching.Id);
        var matched = await match.RunAsync();
        Require(matched.Success && matched.Accepted && Metric(matched, "匹配数量") == "1", "模板训练与定位失败");
        string templateProject = Path.Combine(folder, "模板方案.json");
        match.SaveProject(templateProject);
        var reopenedMatch = new WorkbenchController();
        reopenedMatch.OpenProject(templateProject);
        Require((await reopenedMatch.RunAsync()).Accepted, "模板资产重开失败");
        checks.Add(new { name = "形状模板训练、匹配与重开", passed = true });

        await CheckExpandedEngine(folder, checks);

        var tools = new VisionTools(controller);
        Require(tools.Functions.Count == 7, "工具数量不正确");
        string state = await tools.GetWorkspaceState();
        Require(!state.Contains(controller.ImagePath), "工具状态不应包含本地路径");
        await ExpectFailure(() => tools.SetToolParameter(controller.Document.Steps[0].Id, "min", 999));
        await ExpectFailure(() => tools.AddVisionTool("unsupported"));
        await ExpectFailure(() => tools.SetRoi(controller.Document.Steps[0].Id, -1, 0, 30, 30));
        string reportJson = await tools.RunPipeline();
        Require(JsonDocument.Parse(reportJson).RootElement.GetProperty("Accepted").GetBoolean(), "工具必须返回真实运行结果");
        checks.Add(new { name = "工具注册、参数错误、ROI 边界与真实执行", passed = true });

        using (var server = new HarnessServer(tools))
        {
            server.Start();
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
            var unauthorized = await client.GetAsync(server.BaseUrl + "tools");
            Require(unauthorized.StatusCode == HttpStatusCode.Unauthorized, "无密钥请求应被拒绝");
            using var session = JsonDocument.Parse(File.ReadAllText(server.SessionFilePath));
            client.DefaultRequestHeaders.Authorization = new("Bearer", session.RootElement.GetProperty("token").GetString());
            var catalog = await client.GetStringAsync(server.BaseUrl + "tools");
            Require(JsonDocument.Parse(catalog).RootElement.GetArrayLength() == 7, "HTTP 工具发现失败");
            var response = await client.PostAsync(server.BaseUrl + "tools/call", new StringContent("{\"name\":\"RunPipeline\",\"arguments\":{}}", Encoding.UTF8, "application/json"));
            Require(response.IsSuccessStatusCode && JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("Accepted").GetBoolean(), "HTTP 工具执行失败");
            checks.Add(new { name = "HTTP Harness 认证、发现与流程调用", passed = true });
        }

        string batchFolder = Path.Combine(folder, "batch");
        Directory.CreateDirectory(batchFolder);
        File.Copy(controller.ImagePath, Path.Combine(batchFolder, "零件A.png"), true);
        File.Copy(controller.ImagePath, Path.Combine(batchFolder, "零件B.png"), true);
        string csv = Path.Combine(folder, "批量结果.csv");
        await controller.BatchAsync(batchFolder, csv);
        Require(File.ReadAllLines(csv).Length == 3, "批量 CSV 应包含两条图像结果");
        Require(controller.ImagePath == controller.Document.ImagePath && controller.Frame?.Width == 640 && controller.LastReport == null, "批量处理不能改变当前源图身份或留下错误报告");
        checks.Add(new { name = "文件夹批量检测及 CSV", passed = true });

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await controller.RunAsync(token: cancelled.Token); throw new InvalidOperationException("预先取消应终止运行"); }
        catch (OperationCanceledException) { Require(!controller.IsBusy, "取消后应恢复就绪"); }
        checks.Add(new { name = "取消后恢复", passed = true });
        // 本机协议桩验证真实聊天调用循环，不使用用户密钥或云模型。
        checks.Add(new { name = "聊天工具调用协议", passed = true, detail = await ChatProbe.RunAsync(controller) });
    }

    private static async Task CheckUi(string folder, List<object> checks)
    {
        var window = (MainWindow)Application.Current.MainWindow;
        var controller = window.Controller;
        controller.CreateDemo();
        var list = (ListBox)window.FindName("StepList");
        list.SelectedIndex = 1;
        await controller.RunAsync();
        await window.Dispatcher.InvokeAsync(() => { window.UpdateLayout(); }, DispatcherPriority.ApplicationIdle);
        CheckIndustrialNavigation(window, checks);
        var image = (Image)window.FindName("PreviewImage");
        Require(image.Source != null && list.Items.Count == 2, "窗口应显示图像和两项工具");
        Require(((DataGrid)window.FindName("ResultGrid")).Items.Count > 0, "窗口应显示检测结果");
        Capture((FrameworkElement)window.Content, Path.Combine(folder, "ui.png"));
        checks.Add(new { name = "真实 WPF 窗口、工具列表与检测结果", passed = true, screenshot = "ui.png" });

        var blob = controller.Document.Steps[1];
        controller.SetParameter(blob.Id, "maxCount", 4);
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        var field = Descendants<TextBox>((StackPanel)window.FindName("ParameterPanel")).First(x => (string?)x.Tag == "maxCount");
        Require(field.Text == "4", "工具接口修改参数后，界面应同步新值");
        Require(controller.LastReport == null && controller.Frame!.Overlays.Count == 0, "编辑后应清除旧检测结果");
        controller.SetParameter(blob.Id, "maxCount", 3);
        await controller.RunAsync();
        checks.Add(new { name = "参数绑定与结果失效", passed = true });

        var aiTab = Descendants<TabItem>(window).First(t => (string?)t.Header == "AI 助手");
        aiTab.IsSelected = true;
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        Require(((TextBox)window.FindName("ChatInput")).IsVisible, "AI 页应显示聊天输入框");
        Capture((FrameworkElement)window.Content, Path.Combine(folder, "ui-ai.png"));
        window.Width = window.MinWidth;
        window.Height = window.MinHeight;
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        Capture((FrameworkElement)window.Content, Path.Combine(folder, "ui-compact.png"));
        checks.Add(new { name = "中文 AI 页面和最小窗口布局", passed = true, screenshots = new[] { "ui-ai.png", "ui-compact.png" } });
        window.Width = 1440;
        window.Height = 920;
        await CheckMeasurementUi(folder, window, checks);
    }

    private static void Capture(FrameworkElement element, string path)
    {
        var target = new RenderTargetBitmap((int)element.ActualWidth + 36, (int)element.ActualHeight + 36, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(243, 245, 250)), null, new Rect(0, 0, target.PixelWidth, target.PixelHeight));
            context.DrawRectangle(new VisualBrush(element), null, new Rect(18, 18, element.ActualWidth, element.ActualHeight));
        }
        target.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
    private static string Metric(RunReport report, string name) => report.Steps.SelectMany(s => s.Metrics).First(m => m.Name == name).Value;
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task ExpectFailure(Func<Task<string>> operation)
    {
        try { await operation(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("非法工具输入应被拒绝");
    }
}
