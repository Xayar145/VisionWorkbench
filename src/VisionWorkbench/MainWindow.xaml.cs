using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

/// <summary>
/// 工业视觉工作台主窗口
/// </summary>
public partial class MainWindow : Window
{
    public WorkbenchController Controller { get; } = new();
    private readonly ChatService chat;
    private readonly HarnessServer harness;
    private AiSettings settings;
    private CancellationTokenSource? cancellation;
    private Point? roiStart;
    private Shape? roiOutline;
    private string currentRoiShape = "rectangle";
    private bool fitImage = true;
    private bool sending;
    private bool syncingRoiShape;
    private VisionStep? SelectedStep => StepList?.SelectedItem as VisionStep;

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

        RefreshOperatorTree();
        if (ToolPicker != null)
        {
            ToolPicker.ItemsSource = ToolCatalog.All;
            ToolPicker.SelectedIndex = 0;
        }

        Controller.StateChanged += () => Dispatcher.Invoke(RefreshWorkspace);
        Controller.Logged += message => Dispatcher.Invoke(() => AppendLog(message));
        RefreshWorkspace();
        InitializeDevices();
        chat.Tools.Devices = Devices;
        PreviewKeyDown += Workspace_KeyDown;

        try
        {
            harness.Start();
            HarnessAddress.Text = harness.BaseUrl;
            HarnessSession.Text = harness.SessionFilePath;
            AppendLog($"本机工具接口已启动：{harness.BaseUrl}");
        }
        catch (Exception ex)
        {
            HarnessAddress.Text = "未启动";
            AppendLog($"本机工具接口启动失败：{ex.Message}");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        cancellation?.Cancel();
        harness.Dispose();
        base.OnClosed(e);
    }

    #region 算子库树结构分类与过滤 (HALCON/OpenCV)

    public class ToolItem
    {
        public ToolDefinition Definition { get; }
        public string Name => Definition.Name;
        public string Kind => Definition.Kind;
        public string Backend => Definition.Backend;
        public string Description => Definition.Description;
        public ToolItem(ToolDefinition def) => Definition = def;
    }

    public class ToolCategoryGroup
    {
        public string CategoryName { get; set; } = "";
        public bool Expanded { get; set; }
        public List<ToolItem> Tools { get; set; } = new();
    }

    private string GetToolCategory(ToolDefinition def)
    {
        var kind = def.Kind.ToLowerInvariant();
        if (kind.StartsWith("opencv.") || kind.Contains("opencv"))
            return "OpenCV 图像处理";
        if (kind.Contains("mean") || kind.Contains("median") || kind.Contains("gaussian") || kind.Contains("invert") || kind.Contains("emphasize"))
            return "HALCON 预处理";
        if (kind.Contains("threshold") || kind.Contains("blob") || kind.Contains("open") || kind.Contains("close") || kind.Contains("fill"))
            return "区域与形态学";
        if (kind.Contains("match") || kind.Contains("ncc"))
            return "定位与对齐";
        if (kind.Contains("measure") || kind.Contains("metrology_line") || kind.Contains("metrology_circle") || kind.Contains("intensity"))
            return "几何测量与检测";
        if (kind.Contains("edges") || kind.Contains("edge"))
            return "边缘检测";
        return "其他通用工具";
    }

    private void RefreshOperatorTree(string? filter = null)
    {
        var all = ToolCatalog.All;
        var categories = new Dictionary<string, List<ToolItem>>
        {
            ["HALCON 预处理"] = new(),
            ["区域与形态学"] = new(),
            ["定位与对齐"] = new(),
            ["几何测量与检测"] = new(),
            ["边缘检测"] = new(),
            ["OpenCV 图像处理"] = new(),
            ["其他通用工具"] = new()
        };

        foreach (var tool in all)
        {
            var cat = GetToolCategory(tool);
            if (!categories.ContainsKey(cat)) categories[cat] = new();
            categories[cat].Add(new ToolItem(tool));
        }

        var result = new List<ToolCategoryGroup>();
        var q = filter?.Trim();
        bool hasFilter = !string.IsNullOrEmpty(q);

        foreach (var kvp in categories)
        {
            var list = kvp.Value;
            if (hasFilter)
            {
                list = list.Where(t =>
                    t.Name.Contains(q!, StringComparison.OrdinalIgnoreCase) ||
                    t.Kind.Contains(q!, StringComparison.OrdinalIgnoreCase) ||
                    (t.Description != null && t.Description.Contains(q!, StringComparison.OrdinalIgnoreCase))
                ).ToList();
            }

            if (list.Count > 0)
            {
                result.Add(new ToolCategoryGroup
                {
                    CategoryName = $"{kvp.Key} ({list.Count})",
                    Tools = list,
                    Expanded = hasFilter
                });
            }
        }

        OperatorTree.ItemsSource = result;
        OperatorCount.Text = $"算子库（{result.Sum(group => group.Tools.Count)}）";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshOperatorTree(SearchBox.Text);

    private void OperatorTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (OperatorTree.SelectedItem is ToolItem item)
            Perform(() => StepList.SelectedItem = Controller.AddStep(item.Kind));
    }

    #endregion

    #region 状态与工作区刷新

    private void RefreshWorkspace()
    {
        if (!ReferenceEquals(StepList.ItemsSource, Controller.Document.Steps))
            StepList.ItemsSource = Controller.Document.Steps;

        bool isIdle = !Controller.IsBusy && !sending;
        ProjectActions.IsEnabled = isIdle;
        if (AddButton != null) AddButton.IsEnabled = isIdle;
        StepActions.IsEnabled = isIdle;
        StepList.IsEnabled = isIdle;
        ParameterPanel.IsEnabled = isIdle;
        SyncRoiShape();
        DrawRoi.IsEnabled = isIdle && SelectedStep != null;
        StopButton.IsEnabled = Controller.IsBusy || sending;
        StatusText.Text = Controller.IsBusy ? "正在执行检测流程..." : sending ? "AI 助手正在处理..." : "就绪";
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
            ImageInfo.Text = $"{frame.Width} × {frame.Height} px · {ImageScale.ScaleX * 100:F0}%";
            DrawOverlays();
            if (fitImage) FitImage();
        }
        else
        {
            PreviewImage.Source = null;
            OverlayCanvas.Children.Clear();
            EmptyImage.Visibility = Visibility.Visible;
            ImageInfo.Text = "未加载图像";
        }

        if (Controller.LastReport is { } report)
        {
            var rows = new List<MetricRow>
            {
                new("整条流程综合判定", report.Success ? (report.Accepted ? "OK (合格)" : "NG (超差)") : "执行失败", $"{report.ElapsedMs:F1} ms")
            };
            foreach (var step in report.Steps)
            {
                rows.Add(new(step.Name, step.Message, $"{step.ElapsedMs:F1} ms"));
                rows.AddRange(step.Metrics.Select(m => new MetricRow($"    {m.Name}", m.Value, m.Unit)));
            }
            ResultGrid.ItemsSource = rows;
        }
        else
        {
            ResultGrid.ItemsSource = null;
        }
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

    #endregion

    #region 菜单与工具栏真实绑定事件

    private void NewProject_Click(object sender, RoutedEventArgs e) => Perform(() => Controller.NewProject());
    private void MetrologyDemo_Click(object sender, RoutedEventArgs e) => Perform(() =>
    {
        fitImage = true;
        Controller.CreateMetrologyDemo();
        StepList.SelectedIndex = 1;
    });

    private void Workspace_KeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool control = Keyboard.Modifiers == ModifierKeys.Control;
        if (e.Key == Key.F5 && Keyboard.Modifiers == ModifierKeys.Shift) { Stop_Click(sender, e); e.Handled = true; return; }
        if (Controller.IsBusy || sending) return;
        if (control && e.Key == Key.S) Save_Click(sender, e);
        else if (control && e.Key == Key.O) OpenProject_Click(sender, e);
        else if (control && e.Key == Key.N) NewProject_Click(sender, e);
        else if (control && e.Key == Key.D0) Fit_Click(sender, e);
        else if (control && e.Key == Key.D1) Actual_Click(sender, e);
        else if (e.Key == Key.F5) Run_Click(sender, e);
        else if (e.Key == Key.F6) RunToStep_Click(sender, e);
        else if (e.Key == Key.Delete && Keyboard.FocusedElement is not TextBox) Remove_Click(sender, e);
        else if (Keyboard.Modifiers == ModifierKeys.Alt && key == Key.Up) Up_Click(sender, e);
        else if (Keyboard.Modifiers == ModifierKeys.Alt && key == Key.Down) Down_Click(sender, e);
        else return;
        e.Handled = true;
    }
    private async void OpenImage_Click(object sender, RoutedEventArgs e)
    {
        await Devices.StopPreviewAsync();
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
    private async void Run_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async token => { await Devices.StopPreviewAsync(); await Controller.RunAsync(token: token); });
    private void Stop_Click(object sender, RoutedEventArgs e) => cancellation?.Cancel();
    private async void RunToStep_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedStep is { } step)
            await ExecuteAsync(async token => { await Devices.StopPreviewAsync(); await Controller.RunAsync(step.Id, token); });
    }
    private async void Batch_Click(object sender, RoutedEventArgs e)
    {
        var folder = new OpenFolderDialog { Title = "选择批量检测图像目录" };
        if (folder.ShowDialog(this) != true) return;
        var csv = new SaveFileDialog { Filter = "检测结果 CSV|*.csv", FileName = "检测结果.csv" };
        if (csv.ShowDialog(this) == true) await ExecuteAsync(token => Controller.BatchAsync(folder.FolderName, csv.FileName, token));
    }
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private void ShowSource_Click(object sender, RoutedEventArgs e) => Perform(() => Controller.ShowSource());
    private void ShowResult_Click(object sender, RoutedEventArgs e) => Perform(() => Controller.ShowResult());

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (OperatorTree.SelectedItem is ToolItem treeItem)
            Perform(() => StepList.SelectedItem = Controller.AddStep(treeItem.Kind));
        else if (ToolPicker?.SelectedItem is ToolDefinition definition)
            Perform(() => StepList.SelectedItem = Controller.AddStep(definition.Kind));
    }
    private void Up_Click(object sender, RoutedEventArgs e) { if (SelectedStep is { } step) Perform(() => Controller.MoveStep(step.Id, -1)); }
    private void Down_Click(object sender, RoutedEventArgs e) { if (SelectedStep is { } step) Perform(() => Controller.MoveStep(step.Id, 1)); }
    private void Remove_Click(object sender, RoutedEventArgs e) { if (SelectedStep is { } step) Perform(() => Controller.RemoveStep(step.Id)); }
    private void ClearSteps_Click(object sender, RoutedEventArgs e)
    {
        if (Controller.IsBusy || sending) return;
        if (Controller.Document.Steps.Count == 0) return;
        if (MessageBox.Show(this, "确定清空当前流程的所有算子吗？", "清空流程", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            Perform(() => { Controller.Document.Steps.Clear(); Controller.NotifyEdited(); });
    }
    private void Enabled_Changed(object sender, RoutedEventArgs e) { if (IsLoaded) Controller.NotifyEdited(); }
    private void StepList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SyncRoiShape();
        BuildParameters();
        DrawOverlays();
        DrawRoi.IsEnabled = !Controller.IsBusy && !sending && SelectedStep != null;
    }

    private void DrawRectRoi_Click(object sender, RoutedEventArgs e)
    {
        currentRoiShape = "rectangle";
        if (RoiShapeCombo != null) RoiShapeCombo.SelectedIndex = 0;
        DrawRoi.IsChecked = true;
    }
    private void DrawCircleRoi_Click(object sender, RoutedEventArgs e)
    {
        currentRoiShape = "circle";
        if (RoiShapeCombo != null) RoiShapeCombo.SelectedIndex = 1;
        DrawRoi.IsChecked = true;
    }
    private void ClearRoi_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedStep is { } step)
        {
            Perform(() => Controller.SetRoi(step.Id, new RoiSpec
            {
                Enabled = false,
                Shape = step.Roi.Shape,
                X = step.Roi.X,
                Y = step.Roi.Y,
                Width = step.Roi.Width,
                Height = step.Roi.Height
            }));
            BuildParameters();
            DrawOverlays();
        }
    }
    private void RoiShapeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || syncingRoiShape) return;
        if (RoiShapeCombo?.SelectedItem is ComboBoxItem item && item.Tag is string shape)
        {
            currentRoiShape = shape;
            if (SelectedStep is { } step && step.Roi.Enabled)
            {
                double w = step.Roi.Width;
                double h = step.Roi.Height;
                if (shape == "circle") { double s = Math.Max(w, h); w = s; h = s; }
                Perform(() => Controller.SetRoi(step.Id, new RoiSpec
                {
                    Enabled = true,
                    Shape = shape,
                    X = step.Roi.X,
                    Y = step.Roi.Y,
                    Width = w,
                    Height = h
                }));
                BuildParameters();
                DrawOverlays();
            }
        }
    }

    private void SyncRoiShape()
    {
        if (RoiShapeCombo == null) return;
        syncingRoiShape = true;
        currentRoiShape = SelectedStep?.Roi.Shape ?? "rectangle";
        RoiShapeCombo.SelectedIndex = currentRoiShape == "circle" ? 1 : 0;
        syncingRoiShape = false;
    }

    private void TrainTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedStep is { } step && (step.Kind == "halcon.match" || step.Kind == "halcon.ncc"))
        {
            Perform(() => Controller.TrainTemplate(step.Id));
            BuildParameters();
        }
        else
        {
            MessageBox.Show(this, "请在流程列表中选中定位工具 (halcon.match 或 halcon.ncc)。", "模型训练", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
    private void InsertMetrologyLine_Click(object sender, RoutedEventArgs e) => Perform(() => StepList.SelectedItem = Controller.AddStep("halcon.metrology_line"));
    private void InsertMetrologyCircle_Click(object sender, RoutedEventArgs e) => Perform(() => StepList.SelectedItem = Controller.AddStep("halcon.metrology_circle"));
    private void InsertNcc_Click(object sender, RoutedEventArgs e) => Perform(() => StepList.SelectedItem = Controller.AddStep("halcon.ncc"));

    private void HelpManual_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this, "【VisionWorkbench 算子与模型指南】\n1. 双击左侧算子库可快速向流程追加算子。\n2. 在视口工具栏勾选“绘制 ROI”或由菜单触发交互绘制，支持矩形与圆形外接框。\n3. 几何测量模型 (Line / Circle)：ROI 作为初始几何骨架，由法向辐射卡尺提取亚像素边缘点拟合几何体，参数在普通列表中动态调整。\n4. 定位模型 (Match / NCC)：配置 ROI 后点击“从当前 ROI 创建模板”即可完成建模。", "算子使用指南", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private void About_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this, "VisionWorkbench 工业视觉工作台\n集成 HALCON 核心算法库与 OpenCV 视觉处理引擎。\n遵循传统工业视觉工作台布局与操作规范。", "关于工作台", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    #endregion

    #region 属性参数面板动态构建

    private void BuildParameters()
    {
        ParameterPanel.Children.Clear();
        if (SelectedStep is not { } step)
        {
            ParameterPanel.Children.Add(new TextBlock { Text = "选择左侧工具查看参数", Foreground = Brushes.Gray, Margin = new Thickness(0, 10, 0, 0) });
            return;
        }

        // 算子头部摘要卡片
        var headerBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(248, 250, 252)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 10)
        };
        var headerStack = new StackPanel();
        headerStack.Children.Add(new TextBlock { Text = step.Name, FontSize = 14, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(30, 41, 59)) });
        headerStack.Children.Add(new TextBlock { Text = $"算法引擎：{step.Backend}", FontSize = 10.5, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 2, 0, 0) });
        headerBorder.Child = headerStack;
        ParameterPanel.Children.Add(headerBorder);

        if (step.Kind is "halcon.metrology_line" or "halcon.metrology_circle")
            ParameterPanel.Children.Add(new TextBlock
            {
                Text = step.Kind == "halcon.metrology_line"
                    ? "初始几何：ROI 水平中心线；输出为有效卡尺间的拟合线段。"
                    : "初始几何：ROI 内接圆；输出拟合圆心和直径。",
                Foreground = Brushes.SlateGray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });

        // 动态算子通用参数列表
        if (step.Parameters.Count > 0)
        {
            ParameterPanel.Children.Add(new TextBlock { Text = "算子参数", FontWeight = FontWeights.SemiBold, FontSize = 12.5, Margin = new Thickness(0, 0, 0, 6) });
            foreach (var parameter in step.Parameters)
            {
                var pGrid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                pGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
                pGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var lbl = new TextBlock { Text = parameter.Label, VerticalAlignment = VerticalAlignment.Center, FontSize = 11.5, Foreground = new SolidColorBrush(Color.FromRgb(55, 65, 81)) };
                Grid.SetColumn(lbl, 0); pGrid.Children.Add(lbl);

                var field = new TextBox { Padding = new Thickness(4, 2, 4, 2), Tag = parameter.Key, VerticalAlignment = VerticalAlignment.Center };
                field.SetBinding(TextBox.TextProperty, new Binding(nameof(ToolParameter.Value)) { Source = parameter, Mode = BindingMode.OneWay, ConverterCulture = CultureInfo.InvariantCulture });
                field.LostKeyboardFocus += (_, _) =>
                {
                    if (double.TryParse(field.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                        Perform(() => Controller.SetParameter(step.Id, parameter.Key, value));
                    field.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
                };
                field.KeyDown += (_, ke) =>
                {
                    if (ke.Key == Key.Enter)
                    {
                        if (double.TryParse(field.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                            Perform(() => Controller.SetParameter(step.Id, parameter.Key, value));
                        field.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
                    }
                };
                Grid.SetColumn(field, 1); pGrid.Children.Add(field);
                ParameterPanel.Children.Add(pGrid);
            }
        }

        // ROI 区域设置模块 (矩形 / 圆形 / 数值手动编辑)
        var roiExpander = new Expander { Header = "感兴趣区域 (ROI)", IsExpanded = true, Margin = new Thickness(0, 6, 0, 8) };
        var roiStack = new StackPanel { Margin = new Thickness(2, 6, 2, 4) };

        var roiCheck = new CheckBox { Content = "启用当前算子 ROI", Margin = new Thickness(0, 0, 0, 6) };
        roiCheck.SetBinding(CheckBox.IsCheckedProperty, new Binding("Roi.Enabled") { Source = step, Mode = BindingMode.OneWay });
        roiCheck.Click += (_, _) => Perform(() => Controller.SetRoi(step.Id, new RoiSpec
        {
            Enabled = roiCheck.IsChecked == true,
            Shape = step.Roi.Shape ?? "rectangle",
            X = step.Roi.X,
            Y = step.Roi.Y,
            Width = step.Roi.Width,
            Height = step.Roi.Height
        }));
        roiStack.Children.Add(roiCheck);

        var shapePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        shapePanel.Children.Add(new TextBlock { Text = "形状: ", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        var rRadio = new RadioButton { Content = "矩形", IsChecked = (step.Roi.Shape != "circle"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        var cRadio = new RadioButton { Content = "圆形", IsChecked = (step.Roi.Shape == "circle"), VerticalAlignment = VerticalAlignment.Center };
        rRadio.Checked += (_, _) =>
        {
            currentRoiShape = "rectangle";
            if (RoiShapeCombo != null) RoiShapeCombo.SelectedIndex = 0;
            Perform(() => Controller.SetRoi(step.Id, new RoiSpec { Enabled = step.Roi.Enabled, Shape = "rectangle", X = step.Roi.X, Y = step.Roi.Y, Width = step.Roi.Width, Height = step.Roi.Height }));
        };
        cRadio.Checked += (_, _) =>
        {
            currentRoiShape = "circle";
            if (RoiShapeCombo != null) RoiShapeCombo.SelectedIndex = 1;
            double s = Math.Max(step.Roi.Width, step.Roi.Height);
            Perform(() => Controller.SetRoi(step.Id, new RoiSpec { Enabled = step.Roi.Enabled, Shape = "circle", X = step.Roi.X, Y = step.Roi.Y, Width = s, Height = s }));
        };
        shapePanel.Children.Add(rRadio); shapePanel.Children.Add(cRadio);
        roiStack.Children.Add(shapePanel);

        var coordGrid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        coordGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        coordGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        coordGrid.RowDefinitions.Add(new RowDefinition());
        coordGrid.RowDefinitions.Add(new RowDefinition());

        var xP = new DockPanel { Margin = new Thickness(0, 2, 3, 2) };
        xP.Children.Add(new TextBlock { Text = "X:", Width = 18, VerticalAlignment = VerticalAlignment.Center });
        var xBox = new TextBox();
        xBox.SetBinding(TextBox.TextProperty, new Binding("Roi.X") { Source = step, Mode = BindingMode.OneWay, ConverterCulture = CultureInfo.InvariantCulture });
        xBox.LostFocus += (_, _) => { if (double.TryParse(xBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) Perform(() => Controller.SetRoi(step.Id, new RoiSpec { Enabled = step.Roi.Enabled, Shape = step.Roi.Shape, X = v, Y = step.Roi.Y, Width = step.Roi.Width, Height = step.Roi.Height })); };
        xP.Children.Add(xBox); Grid.SetRow(xP, 0); Grid.SetColumn(xP, 0); coordGrid.Children.Add(xP);

        var yP = new DockPanel { Margin = new Thickness(3, 2, 0, 2) };
        yP.Children.Add(new TextBlock { Text = "Y:", Width = 18, VerticalAlignment = VerticalAlignment.Center });
        var yBox = new TextBox();
        yBox.SetBinding(TextBox.TextProperty, new Binding("Roi.Y") { Source = step, Mode = BindingMode.OneWay, ConverterCulture = CultureInfo.InvariantCulture });
        yBox.LostFocus += (_, _) => { if (double.TryParse(yBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) Perform(() => Controller.SetRoi(step.Id, new RoiSpec { Enabled = step.Roi.Enabled, Shape = step.Roi.Shape, X = step.Roi.X, Y = v, Width = step.Roi.Width, Height = step.Roi.Height })); };
        yP.Children.Add(yBox); Grid.SetRow(yP, 0); Grid.SetColumn(yP, 1); coordGrid.Children.Add(yP);

        var wP = new DockPanel { Margin = new Thickness(0, 2, 3, 2) };
        wP.Children.Add(new TextBlock { Text = "W:", Width = 18, VerticalAlignment = VerticalAlignment.Center });
        var wBox = new TextBox();
        wBox.SetBinding(TextBox.TextProperty, new Binding("Roi.Width") { Source = step, Mode = BindingMode.OneWay, ConverterCulture = CultureInfo.InvariantCulture });
        wBox.LostFocus += (_, _) => { if (double.TryParse(wBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) { double h = (step.Roi.Shape == "circle") ? v : step.Roi.Height; Perform(() => Controller.SetRoi(step.Id, new RoiSpec { Enabled = step.Roi.Enabled, Shape = step.Roi.Shape, X = step.Roi.X, Y = step.Roi.Y, Width = v, Height = h })); } };
        wP.Children.Add(wBox); Grid.SetRow(wP, 1); Grid.SetColumn(wP, 0); coordGrid.Children.Add(wP);

        var hP = new DockPanel { Margin = new Thickness(3, 2, 0, 2) };
        hP.Children.Add(new TextBlock { Text = "H:", Width = 18, VerticalAlignment = VerticalAlignment.Center });
        var hBox = new TextBox();
        hBox.SetBinding(TextBox.TextProperty, new Binding("Roi.Height") { Source = step, Mode = BindingMode.OneWay, ConverterCulture = CultureInfo.InvariantCulture });
        hBox.LostFocus += (_, _) => { if (double.TryParse(hBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) { double w = (step.Roi.Shape == "circle") ? v : step.Roi.Width; Perform(() => Controller.SetRoi(step.Id, new RoiSpec { Enabled = step.Roi.Enabled, Shape = step.Roi.Shape, X = step.Roi.X, Y = step.Roi.Y, Width = w, Height = v })); } };
        hP.Children.Add(hBox); Grid.SetRow(hP, 1); Grid.SetColumn(hP, 1); coordGrid.Children.Add(hP);
        roiStack.Children.Add(coordGrid);

        var roiBtnStack = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
        var clrBtn = new Button { Content = "清除选区", Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 4, 0) };
        clrBtn.Click += (_, _) => ClearRoi_Click(clrBtn, new RoutedEventArgs());
        var dBtn = new Button { Content = "交互绘制", Padding = new Thickness(6, 2, 6, 2) };
        dBtn.Click += (_, _) => { DrawRoi.IsChecked = true; };
        roiBtnStack.Children.Add(clrBtn); roiBtnStack.Children.Add(dBtn);
        roiStack.Children.Add(roiBtnStack);

        roiExpander.Content = roiStack;
        ParameterPanel.Children.Add(roiExpander);

        // 模板匹配训练 (halcon.match 与 halcon.ncc)
        if (step.Kind == "halcon.match" || step.Kind == "halcon.ncc")
        {
            var tplExpander = new Expander { Header = "模板模型设置", IsExpanded = true, Margin = new Thickness(0, 0, 0, 10) };
            var tplStack = new StackPanel { Margin = new Thickness(2, 6, 2, 4) };
            var train = new Button { Content = "从当前 ROI 创建模板", Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 0, 4) };
            var templateStatus = new TextBlock
            {
                Text = string.IsNullOrEmpty(step.TemplatePath) ? "状态：尚未创建模板 (请在上方框选 ROI 后训练)" : "状态：模板已创建就绪",
                Foreground = string.IsNullOrEmpty(step.TemplatePath) ? Brushes.SlateGray : new SolidColorBrush(Color.FromRgb(16, 185, 129)),
                FontSize = 11
            };
            train.Click += (_, _) => Perform(() =>
            {
                Controller.TrainTemplate(step.Id);
                templateStatus.Text = "状态：模板已创建就绪";
                templateStatus.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            });
            tplStack.Children.Add(train); tplStack.Children.Add(templateStatus);
            tplExpander.Content = tplStack;
            ParameterPanel.Children.Add(tplExpander);
        }

        // 快捷执行至此算子按钮
        var run = new Button { Content = "▶ 运行到此算子", Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
        run.Click += async (_, _) => await ExecuteAsync(async token => { await Controller.RunAsync(step.Id, token); });
        ParameterPanel.Children.Add(run);
    }

    #endregion

    #region 图像视口与亚像素标注渲染 (OverlayBox Rectangle/Circle/Line)

    private void DrawOverlays()
    {
        if (OverlayCanvas == null) return;
        OverlayCanvas.Children.Clear();
        var scale = Math.Max(0.02, ImageScale.ScaleX);
        var strokeThickness = 2.0 / scale;

        if (Controller.Frame is { } frame)
        {
            foreach (var box in frame.Overlays)
            {
                var shape = (box.Shape ?? "rectangle").ToLowerInvariant();
                if (shape == "circle")
                {
                    var el = new Ellipse
                    {
                        Width = Math.Max(0, box.Width),
                        Height = Math.Max(0, box.Height),
                        Stroke = Brushes.LimeGreen,
                        StrokeThickness = strokeThickness
                    };
                    Canvas.SetLeft(el, box.X); Canvas.SetTop(el, box.Y);
                    OverlayCanvas.Children.Add(el);
                }
                else if (shape == "line")
                {
                    var ln = new Line
                    {
                        X1 = box.X, Y1 = box.Y,
                        X2 = box.X + box.Width, Y2 = box.Y + box.Height,
                        Stroke = Brushes.LimeGreen,
                        StrokeThickness = strokeThickness
                    };
                    OverlayCanvas.Children.Add(ln);
                }
                else
                {
                    var rect = new Rectangle
                    {
                        Width = Math.Max(0, box.Width),
                        Height = Math.Max(0, box.Height),
                        Stroke = Brushes.LimeGreen,
                        StrokeThickness = strokeThickness
                    };
                    Canvas.SetLeft(rect, box.X); Canvas.SetTop(rect, box.Y);
                    OverlayCanvas.Children.Add(rect);
                }

                if (!string.IsNullOrEmpty(box.Label))
                {
                    var label = new Border
                    {
                        Background = new SolidColorBrush(Color.FromArgb(200, 16, 185, 129)),
                        Padding = new Thickness(3, 1, 3, 1),
                        CornerRadius = new CornerRadius(2),
                        Child = new TextBlock
                        {
                            Text = box.Label,
                            Foreground = Brushes.White,
                            FontSize = Math.Max(10, 11 / scale),
                            FontWeight = FontWeights.SemiBold
                        }
                    };
                    Canvas.SetLeft(label, box.X);
                    Canvas.SetTop(label, Math.Max(0, box.Y - 18 / scale));
                    OverlayCanvas.Children.Add(label);
                }
            }
        }

        if (SelectedStep is { Roi.Enabled: true } s)
        {
            var rShape = (s.Roi.Shape ?? "rectangle").ToLowerInvariant();
            if (rShape == "circle")
            {
                var el = new Ellipse
                {
                    Width = Math.Max(0, s.Roi.Width),
                    Height = Math.Max(0, s.Roi.Height),
                    Stroke = Brushes.DeepSkyBlue,
                    StrokeThickness = strokeThickness,
                    StrokeDashArray = new DoubleCollection { 4, 2 }
                };
                Canvas.SetLeft(el, s.Roi.X); Canvas.SetTop(el, s.Roi.Y);
                OverlayCanvas.Children.Add(el);
            }
            else
            {
                var rect = new Rectangle
                {
                    Width = Math.Max(0, s.Roi.Width),
                    Height = Math.Max(0, s.Roi.Height),
                    Stroke = Brushes.DeepSkyBlue,
                    StrokeThickness = strokeThickness,
                    StrokeDashArray = new DoubleCollection { 4, 2 }
                };
                Canvas.SetLeft(rect, s.Roi.X); Canvas.SetTop(rect, s.Roi.Y);
                OverlayCanvas.Children.Add(rect);
            }

            var roiLabel = new TextBlock
            {
                Text = $"ROI [{(rShape == "circle" ? "圆" : "矩形")}] {s.Roi.Width:F0}×{s.Roi.Height:F0}",
                Foreground = Brushes.DeepSkyBlue,
                Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)),
                FontSize = Math.Max(10, 11 / scale),
                Padding = new Thickness(2)
            };
            Canvas.SetLeft(roiLabel, s.Roi.X);
            Canvas.SetTop(roiLabel, Math.Max(0, s.Roi.Y - 16 / scale));
            OverlayCanvas.Children.Add(roiLabel);
        }
    }

    private void Image_Down(object sender, MouseButtonEventArgs e)
    {
        if (DrawRoi.IsChecked != true || Controller.Frame == null || SelectedStep == null || Controller.IsBusy || sending) return;
        roiStart = e.GetPosition(ImageSurface);
        var scale = Math.Max(0.02, ImageScale.ScaleX);
        var strokeThickness = 2.0 / scale;

        if (currentRoiShape == "circle")
        {
            var el = new Ellipse
            {
                Width = 0, Height = 0,
                Stroke = Brushes.DeepSkyBlue, StrokeThickness = strokeThickness,
                StrokeDashArray = new DoubleCollection { 4, 2 }
            };
            Canvas.SetLeft(el, roiStart.Value.X); Canvas.SetTop(el, roiStart.Value.Y);
            OverlayCanvas.Children.Add(el);
            roiOutline = el;
        }
        else
        {
            var rect = new Rectangle
            {
                Width = 0, Height = 0,
                Stroke = Brushes.DeepSkyBlue, StrokeThickness = strokeThickness,
                StrokeDashArray = new DoubleCollection { 4, 2 }
            };
            Canvas.SetLeft(rect, roiStart.Value.X); Canvas.SetTop(rect, roiStart.Value.Y);
            OverlayCanvas.Children.Add(rect);
            roiOutline = rect;
        }
        ImageSurface.CaptureMouse();
        e.Handled = true;
    }

    private void Image_Move(object sender, MouseEventArgs e)
    {
        if (CursorPosText != null && Controller.Frame != null)
        {
            var pos = e.GetPosition(ImageSurface);
            if (pos.X >= 0 && pos.X < Controller.Frame.Width && pos.Y >= 0 && pos.Y < Controller.Frame.Height)
            {
                int px = (int)pos.X;
                int py = (int)pos.Y;
                int index = py * Controller.Frame.Width + px;
                byte gray = (index >= 0 && index < Controller.Frame.Pixels.Length) ? Controller.Frame.Pixels[index] : (byte)0;
                CursorPosText.Text = $"X: {px}, Y: {py} | 灰度: {gray}";
            }
        }

        if (roiStart is not { } start || roiOutline == null) return;
        var point = e.GetPosition(ImageSurface);
        point.X = Math.Clamp(point.X, 0, ImageSurface.Width);
        point.Y = Math.Clamp(point.Y, 0, ImageSurface.Height);

        if (currentRoiShape == "circle")
        {
            var size = Math.Min(Math.Min(ImageSurface.Width, ImageSurface.Height),
                Math.Max(Math.Abs(point.X - start.X), Math.Abs(point.Y - start.Y)));
            double left = point.X < start.X ? start.X - size : start.X;
            double top = point.Y < start.Y ? start.Y - size : start.Y;
            left = Math.Clamp(left, 0, Math.Max(0, ImageSurface.Width - size));
            top = Math.Clamp(top, 0, Math.Max(0, ImageSurface.Height - size));
            Canvas.SetLeft(roiOutline, left);
            Canvas.SetTop(roiOutline, top);
            roiOutline.Width = size;
            roiOutline.Height = size;
        }
        else
        {
            Canvas.SetLeft(roiOutline, Math.Min(start.X, point.X));
            Canvas.SetTop(roiOutline, Math.Min(start.Y, point.Y));
            roiOutline.Width = Math.Abs(point.X - start.X);
            roiOutline.Height = Math.Abs(point.Y - start.Y);
        }
    }

    private void Image_Up(object sender, MouseButtonEventArgs e)
    {
        if (roiStart == null || roiOutline == null || SelectedStep is not { } step) return;
        ImageSurface.ReleaseMouseCapture();
        if (roiOutline.Width >= 2 && roiOutline.Height >= 2)
        {
            var left = Math.Floor(Canvas.GetLeft(roiOutline));
            var top = Math.Floor(Canvas.GetTop(roiOutline));
            var w = Math.Floor(roiOutline.Width);
            var h = Math.Floor(roiOutline.Height);
            if (currentRoiShape == "circle")
            {
                var s = Math.Max(w, h);
                w = s; h = s;
            }
            Perform(() => Controller.SetRoi(step.Id, new RoiSpec
            {
                Enabled = true,
                Shape = currentRoiShape,
                X = left,
                Y = top,
                Width = w,
                Height = h
            }));
        }
        roiStart = null;
        roiOutline = null;
        DrawRoi.IsChecked = false;
        BuildParameters();
        DrawOverlays();
    }

    private void FitImage()
    {
        if (Controller.Frame is not { } frame || ImageScroller.ViewportWidth <= 0 || ImageScroller.ViewportHeight <= 0) return;
        var scale = Math.Min((ImageScroller.ViewportWidth - 20) / frame.Width, (ImageScroller.ViewportHeight - 20) / frame.Height);
        ImageScale.ScaleX = ImageScale.ScaleY = Math.Max(0.02, scale);
        if (ImageInfo != null) ImageInfo.Text = $"{frame.Width} × {frame.Height} px · {ImageScale.ScaleX * 100:F0}%";
    }
    private void Fit_Click(object sender, RoutedEventArgs e) { fitImage = true; FitImage(); DrawOverlays(); }
    private void Actual_Click(object sender, RoutedEventArgs e)
    {
        fitImage = false; ImageScale.ScaleX = ImageScale.ScaleY = 1.0;
        if (Controller.Frame is { } frame && ImageInfo != null)
            ImageInfo.Text = $"{frame.Width} × {frame.Height} px · 100%";
        DrawOverlays();
    }
    private void ImageScroller_SizeChanged(object sender, SizeChangedEventArgs e) { if (fitImage) FitImage(); }
    private void Image_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        fitImage = false;
        ImageScale.ScaleX = ImageScale.ScaleY = Math.Clamp(ImageScale.ScaleX * (e.Delta > 0 ? 1.15 : 1 / 1.15), 0.02, 16);
        if (Controller.Frame is { } frame && ImageInfo != null)
            ImageInfo.Text = $"{frame.Width} × {frame.Height} px · {ImageScale.ScaleX * 100:F0}%";
        e.Handled = true;
        DrawOverlays();
    }

    #endregion

    #region AI 助手与本机 Harness 交互

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AiSettingsWindow(settings) { Owner = this };
        if (dialog.ShowDialog() == true) Perform(() => { settings = dialog.Settings; SettingsStore.Save(settings); AppendLog("AI 连接设置已保存。"); });
    }
    private void ClearChat_Click(object sender, RoutedEventArgs e) { if (sending) return; chat.Clear(); ChatMessages.Children.Clear(); }
    private void AddChat(string role, string message)
    {
        ChatMessages.Children.Add(new TextBlock { Text = role, FontWeight = FontWeights.Bold, Foreground = role == "你" ? Brushes.RoyalBlue : Brushes.DarkSlateGray, Margin = new Thickness(0, 8, 0, 3) });
        ChatMessages.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkSlateGray, Margin = new Thickness(0, 0, 0, 6) });
        ChatScroller.ScrollToEnd();
    }
    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        var message = ChatInput.Text.Trim();
        if (sending || Controller.IsBusy || message.Length == 0) return;
        if (string.IsNullOrWhiteSpace(settings.Endpoint) || string.IsNullOrWhiteSpace(settings.Model) || string.IsNullOrWhiteSpace(settings.ApiKey))
        { AddChat("系统", "请先在“连接设置”中填写接口地址、模型和密钥。"); return; }
        sending = true; cancellation = new(); SendButton.IsEnabled = false; ChatInput.Clear(); AddChat("你", message); RefreshWorkspace();
        try { AddChat("AI", await chat.SendAsync(message, settings, cancellation.Token)); BuildParameters(); }
        catch (OperationCanceledException) { AddChat("系统", "已停止本次请求。已执行的操作保留在工作区。"); }
        catch (Exception ex) { AddChat("系统", ex.Message); AppendLog($"AI 请求失败：{ex.Message}"); }
        finally { sending = false; cancellation.Dispose(); cancellation = null; SendButton.IsEnabled = true; RefreshWorkspace(); }
    }

    #endregion
}
