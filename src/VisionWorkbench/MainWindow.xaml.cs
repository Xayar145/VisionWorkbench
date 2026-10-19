using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using VisionWorkbench.AI;
using VisionWorkbench.Models;
using VisionWorkbench.Services;
using VisionWorkbench.Vision;
using VisionWorkbench.Views;

namespace VisionWorkbench;

public partial class MainWindow : Window
{
    public WorkbenchController Controller { get; } = new();
    private readonly ChatService chat;
    private readonly HarnessServer harness;
    private AiSettings settings;
    private CancellationTokenSource? cancellation;
    private Point? roiStart;
    private Rectangle? roiOutline;
    private bool fitImage = true;
    private bool sending;
    private VisionStep? SelectedStep => StepList.SelectedItem as VisionStep;

    public MainWindow()
    {
        InitializeComponent();
        chat = new ChatService(Controller);
        chat.Logged += message => Dispatcher.Invoke(() => AppendLog(message));
        harness = new HarnessServer(chat.Tools);
        try { settings = SettingsStore.Load(); }
        catch (Exception ex)
        {
            settings = new AiSettings();
            AppendLog($"AI 配置读取失败，已使用默认连接设置；原配置文件已保留。{ex.Message}");
        }
        ToolPicker.ItemsSource = ToolCatalog.All;
        ToolPicker.SelectedIndex = 0;
        Controller.StateChanged += () => Dispatcher.Invoke(RefreshWorkspace);
        Controller.Logged += message => Dispatcher.Invoke(() => AppendLog(message));
        RefreshWorkspace();
        try
        {
            harness.Start();
            HarnessAddress.Text = harness.BaseUrl;
            HarnessSession.Text = harness.SessionFilePath;
            AppendLog($"本机工具接口已启动：{harness.BaseUrl}");
        }
        catch (Exception ex) { HarnessAddress.Text = "未启动"; AppendLog($"本机工具接口启动失败：{ex.Message}"); }
    }

    protected override void OnClosed(EventArgs e)
    {
        cancellation?.Cancel();
        harness.Dispose();
        base.OnClosed(e);
    }

    private void RefreshWorkspace()
    {
        if (!ReferenceEquals(StepList.ItemsSource, Controller.Document.Steps))
            StepList.ItemsSource = Controller.Document.Steps;
        ProjectActions.IsEnabled = !Controller.IsBusy && !sending;
        AddButton.IsEnabled = !Controller.IsBusy && !sending;
        StepActions.IsEnabled = !Controller.IsBusy && !sending;
        StepList.IsEnabled = !Controller.IsBusy && !sending;
        ParameterPanel.IsEnabled = !Controller.IsBusy && !sending;
        DrawRoi.IsEnabled = !Controller.IsBusy && !sending && SelectedStep != null;
        StopButton.IsEnabled = Controller.IsBusy || sending;
        StatusText.Text = Controller.IsBusy ? "正在检测…" : sending ? "AI 正在处理…" : "就绪";
        ProjectInfo.Text = string.IsNullOrEmpty(Controller.ProjectPath) ? Controller.Document.Name : Controller.ProjectPath;
        var frame = Controller.Frame;
        if (frame != null)
        {
            PreviewImage.Source = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Gray8, null, frame.Pixels, frame.Width);
            ImageSurface.Width = frame.Width;
            ImageSurface.Height = frame.Height;
            OverlayCanvas.Width = frame.Width;
            OverlayCanvas.Height = frame.Height;
            EmptyImage.Visibility = Visibility.Collapsed;
            ImageInfo.Text = $"{frame.Width} × {frame.Height} px · 滚轮缩放";
            DrawOverlays();
            if (fitImage) FitImage();
        }
        else
        {
            PreviewImage.Source = null;
            OverlayCanvas.Children.Clear();
            EmptyImage.Visibility = Visibility.Visible;
        }
        if (Controller.LastReport is { } report)
        {
            var rows = new List<MetricRow>();
            rows.Add(new("整条流程", report.Success ? (report.Accepted ? "OK" : "NG") : "执行失败", $"{report.ElapsedMs:F1} ms"));
            foreach (var step in report.Steps)
            {
                rows.Add(new(step.Name, step.Message, $"{step.ElapsedMs:F1} ms"));
                rows.AddRange(step.Metrics.Select(m => new MetricRow($"    {m.Name}", m.Value, m.Unit)));
            }
            ResultGrid.ItemsSource = rows;
        }
        else ResultGrid.ItemsSource = null;
    }

    private void AppendLog(string message)
    {
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
        if (LogBox.Text.Length > 60000) LogBox.Text = LogBox.Text[^30000..];
        LogBox.ScrollToEnd();
    }

    private void Perform(Action action)
    {
        try { action(); RefreshWorkspace(); }
        catch (Exception ex) { AppendLog(ex.Message); MessageBox.Show(this, ex.Message, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async Task ExecuteAsync(Func<CancellationToken, Task> action)
    {
        if (Controller.IsBusy || sending) return;
        cancellation = new();
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { AppendLog("操作已取消。"); }
        catch (Exception ex) { AppendLog(ex.Message); MessageBox.Show(this, ex.Message, "运行未完成", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { cancellation.Dispose(); cancellation = null; RefreshWorkspace(); }
    }

    private void OpenImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "图像|*.png;*.bmp;*.jpg;*.jpeg;*.tif;*.tiff|所有文件|*.*" };
        if (dialog.ShowDialog(this) == true) Perform(() => { fitImage = true; Controller.LoadImage(dialog.FileName); });
    }
    private void Demo_Click(object sender, RoutedEventArgs e) => Perform(() => { fitImage = true; Controller.CreateDemo(); StepList.SelectedIndex = 0; });
    private void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "视觉方案|*.json" };
        if (dialog.ShowDialog(this) == true) Perform(() => { fitImage = true; Controller.OpenProject(dialog.FileName); StepList.SelectedIndex = 0; });
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "视觉方案|*.json", FileName = string.IsNullOrEmpty(Controller.ProjectPath) ? "检测方案.json" : Controller.ProjectPath };
        if (dialog.ShowDialog(this) == true) Perform(() => Controller.SaveProject(dialog.FileName));
    }
    private async void Run_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async token => { await Controller.RunAsync(token: token); });
    private void Stop_Click(object sender, RoutedEventArgs e) => cancellation?.Cancel();
    private async void Batch_Click(object sender, RoutedEventArgs e)
    {
        var folder = new OpenFolderDialog { Title = "选择批量检测图像目录" };
        if (folder.ShowDialog(this) != true) return;
        var csv = new SaveFileDialog { Filter = "检测结果 CSV|*.csv", FileName = "检测结果.csv" };
        if (csv.ShowDialog(this) == true) await ExecuteAsync(token => Controller.BatchAsync(folder.FolderName, csv.FileName, token));
    }
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (ToolPicker.SelectedItem is ToolDefinition definition) Perform(() => StepList.SelectedItem = Controller.AddStep(definition.Kind));
    }
    private void Up_Click(object sender, RoutedEventArgs e) { if (SelectedStep is { } step) Perform(() => Controller.MoveStep(step.Id, -1)); }
    private void Down_Click(object sender, RoutedEventArgs e) { if (SelectedStep is { } step) Perform(() => Controller.MoveStep(step.Id, 1)); }
    private void Remove_Click(object sender, RoutedEventArgs e) { if (SelectedStep is { } step) Perform(() => Controller.RemoveStep(step.Id)); }
    private void Enabled_Changed(object sender, RoutedEventArgs e) { if (IsLoaded) Controller.NotifyEdited(); }
    private void StepList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        BuildParameters(); DrawOverlays();
        DrawRoi.IsEnabled = !Controller.IsBusy && !sending && SelectedStep != null;
    }

    private void BuildParameters()
    {
        ParameterPanel.Children.Clear();
        if (SelectedStep is not { } step) { ParameterPanel.Children.Add(new TextBlock { Text = "选择左侧工具查看参数", Foreground = Brushes.Gray }); return; }
        ParameterPanel.Children.Add(new TextBlock { Text = step.Name, FontSize = 18, FontWeight = FontWeights.Bold });
        ParameterPanel.Children.Add(new TextBlock { Text = $"{step.Backend} · {step.Id}", Foreground = Brushes.SlateGray, Margin = new(0, 5, 0, 20) });
        foreach (var parameter in step.Parameters)
        {
            ParameterPanel.Children.Add(new TextBlock { Text = parameter.Label, Margin = new(0, 0, 0, 5) });
            var field = new TextBox { Margin = new(0, 0, 0, 15), Tag = parameter.Key };
            field.SetBinding(TextBox.TextProperty, new Binding(nameof(ToolParameter.Value)) { Source = parameter, Mode = BindingMode.OneWay, ConverterCulture = CultureInfo.InvariantCulture });
            field.LostKeyboardFocus += (_, _) =>
            {
                if (double.TryParse(field.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) Perform(() => Controller.SetParameter(step.Id, parameter.Key, value));
                field.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
            };
            ParameterPanel.Children.Add(field);
        }
        var roi = new CheckBox { Content = "使用矩形 ROI", Margin = new(0, 3, 0, 8) };
        roi.SetBinding(CheckBox.IsCheckedProperty, new Binding("Roi.Enabled") { Source = step, Mode = BindingMode.OneWay });
        roi.Click += (_, _) => Perform(() => Controller.SetRoi(step.Id, new RoiSpec { Enabled = roi.IsChecked == true, X = step.Roi.X, Y = step.Roi.Y, Width = step.Roi.Width, Height = step.Roi.Height }));
        ParameterPanel.Children.Add(roi);
        ParameterPanel.Children.Add(new TextBlock { Text = "勾选图像上方“绘制 ROI”，拖动选择区域。坐标使用原图像素。", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new(0, 0, 0, 12) });
        var roiText = new TextBlock { Foreground = Brushes.SlateGray };
        var roiBinding = new MultiBinding { StringFormat = "ROI  {0:F0}, {1:F0} / {2:F0} × {3:F0}" };
        foreach (var name in new[] { "X", "Y", "Width", "Height" }) roiBinding.Bindings.Add(new Binding($"Roi.{name}") { Source = step });
        roiText.SetBinding(TextBlock.TextProperty, roiBinding);
        ParameterPanel.Children.Add(roiText);
        if (step.Kind == "halcon.match")
        {
            var train = new Button { Content = "从当前 ROI 创建模板", Margin = new(0, 18, 0, 8) };
            var templateStatus = new TextBlock { Text = string.IsNullOrEmpty(step.TemplatePath) ? "尚未创建模板" : "已创建模板", Foreground = Brushes.SlateGray };
            train.Click += (_, _) => Perform(() =>
            {
                Controller.TrainTemplate(step.Id);
                templateStatus.Text = "已创建模板";
            });
            ParameterPanel.Children.Add(train);
            ParameterPanel.Children.Add(templateStatus);
        }
        var run = new Button { Content = "运行到此工具", Margin = new(0, 20, 0, 0) };
        run.Click += async (_, _) => await ExecuteAsync(async token => { await Controller.RunAsync(step.Id, token); });
        ParameterPanel.Children.Add(run);
    }

    private void DrawOverlays()
    {
        if (OverlayCanvas == null) return;
        OverlayCanvas.Children.Clear();
        if (Controller.Frame is { } frame)
            foreach (var box in frame.Overlays)
            {
                AddBox(box.X, box.Y, box.Width, box.Height, Brushes.LimeGreen);
                var label = new TextBlock { Text = box.Label, Foreground = Brushes.Black, Background = Brushes.LimeGreen, FontSize = 12 };
                Canvas.SetLeft(label, box.X); Canvas.SetTop(label, Math.Max(0, box.Y - 19)); OverlayCanvas.Children.Add(label);
            }
        if (SelectedStep is { Roi.Enabled: true } step) AddBox(step.Roi.X, step.Roi.Y, step.Roi.Width, step.Roi.Height, Brushes.DeepSkyBlue);
    }
    private Rectangle AddBox(double x, double y, double width, double height, Brush color)
    {
        var rectangle = new Rectangle { Width = Math.Max(0, width), Height = Math.Max(0, height), Stroke = color, StrokeThickness = 2 / Math.Max(0.1, ImageScale.ScaleX) };
        Canvas.SetLeft(rectangle, x); Canvas.SetTop(rectangle, y); OverlayCanvas.Children.Add(rectangle); return rectangle;
    }
    private void Image_Down(object sender, MouseButtonEventArgs e)
    {
        if (DrawRoi.IsChecked != true || Controller.Frame == null || SelectedStep == null || Controller.IsBusy || sending) return;
        roiStart = e.GetPosition(ImageSurface); roiOutline = AddBox(roiStart.Value.X, roiStart.Value.Y, 0, 0, Brushes.DeepSkyBlue);
        ImageSurface.CaptureMouse(); e.Handled = true;
    }
    private void Image_Move(object sender, MouseEventArgs e)
    {
        if (roiStart is not { } start || roiOutline == null) return;
        var point = e.GetPosition(ImageSurface);
        point.X = Math.Clamp(point.X, 0, ImageSurface.Width); point.Y = Math.Clamp(point.Y, 0, ImageSurface.Height);
        Canvas.SetLeft(roiOutline, Math.Min(start.X, point.X)); Canvas.SetTop(roiOutline, Math.Min(start.Y, point.Y));
        roiOutline.Width = Math.Abs(point.X - start.X); roiOutline.Height = Math.Abs(point.Y - start.Y);
    }
    private void Image_Up(object sender, MouseButtonEventArgs e)
    {
        if (roiStart == null || roiOutline == null || SelectedStep is not { } step) return;
        ImageSurface.ReleaseMouseCapture();
        if (roiOutline.Width >= 2 && roiOutline.Height >= 2)
            Perform(() => Controller.SetRoi(step.Id, new RoiSpec { Enabled = true, X = Math.Floor(Canvas.GetLeft(roiOutline)), Y = Math.Floor(Canvas.GetTop(roiOutline)), Width = Math.Floor(roiOutline.Width), Height = Math.Floor(roiOutline.Height) }));
        roiStart = null; roiOutline = null; DrawRoi.IsChecked = false; BuildParameters(); DrawOverlays();
    }
    private void FitImage()
    {
        if (Controller.Frame is not { } frame || ImageScroller.ViewportWidth <= 0) return;
        var scale = Math.Min((ImageScroller.ViewportWidth - 20) / frame.Width, (ImageScroller.ViewportHeight - 20) / frame.Height);
        ImageScale.ScaleX = ImageScale.ScaleY = Math.Max(0.02, scale);
    }
    private void Fit_Click(object sender, RoutedEventArgs e) { fitImage = true; FitImage(); DrawOverlays(); }
    private void Actual_Click(object sender, RoutedEventArgs e) { fitImage = false; ImageScale.ScaleX = ImageScale.ScaleY = 1; DrawOverlays(); }
    private void ImageScroller_SizeChanged(object sender, SizeChangedEventArgs e) { if (fitImage) FitImage(); }
    private void Image_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        fitImage = false; ImageScale.ScaleX = ImageScale.ScaleY = Math.Clamp(ImageScale.ScaleX * (e.Delta > 0 ? 1.15 : 1 / 1.15), 0.02, 16); e.Handled = true; DrawOverlays();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AiSettingsWindow(settings) { Owner = this };
        if (dialog.ShowDialog() == true) Perform(() => { settings = dialog.Settings; SettingsStore.Save(settings); AppendLog("AI 连接设置已保存。"); });
    }
    private void ClearChat_Click(object sender, RoutedEventArgs e) { if (sending) return; chat.Clear(); ChatMessages.Children.Clear(); }
    private void AddChat(string role, string message)
    {
        ChatMessages.Children.Add(new TextBlock { Text = role, FontWeight = FontWeights.Bold, Foreground = role == "你" ? Brushes.RoyalBlue : Brushes.DarkSlateGray, Margin = new(0, 12, 0, 5) });
        ChatMessages.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkSlateGray, Margin = new(0, 0, 0, 8) });
        ChatScroller.ScrollToEnd();
    }
    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        var message = ChatInput.Text.Trim();
        if (sending || Controller.IsBusy || message.Length == 0) return;
        if (string.IsNullOrWhiteSpace(settings.Endpoint) || string.IsNullOrWhiteSpace(settings.Model) || string.IsNullOrWhiteSpace(settings.ApiKey))
        { AddChat("系统", "请先在“连接设置”中填写 接口地址、模型和密钥。"); return; }
        sending = true; cancellation = new(); SendButton.IsEnabled = false; ChatInput.Clear(); AddChat("你", message); RefreshWorkspace();
        try { AddChat("AI", await chat.SendAsync(message, settings, cancellation.Token)); BuildParameters(); }
        catch (OperationCanceledException) { AddChat("系统", "已停止本次请求。已执行的操作保留在工作区。"); }
        catch (Exception ex) { AddChat("系统", ex.Message); AppendLog($"AI 请求失败：{ex.Message}"); }
        finally { sending = false; cancellation.Dispose(); cancellation = null; SendButton.IsEnabled = true; RefreshWorkspace(); }
    }
}
