using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using VisionWorkbench.Services;

namespace VisionWorkbench.Views;

public sealed class HistoryWindow : Window
{
    private readonly InspectionJournal journal;
    private readonly DataGrid records = new() { IsReadOnly = true, AutoGenerateColumns = false, SelectionMode = DataGridSelectionMode.Single };
    private readonly TextBlock status = new() { Margin = new(12, 8, 12, 8), Foreground = Brushes.DimGray };
    public event Action<string>? ImageSelected;

    public HistoryWindow(InspectionJournal journal)
    {
        this.journal = journal;
        Title = "检测记录"; Width = 1050; Height = 620; MinWidth = 740; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.WhiteSmoke; FontFamily = new("Microsoft YaHei UI"); FontSize = 13;
        var root = new DockPanel(); Content = root;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(10) };
        DockPanel.SetDock(actions, Dock.Top); root.Children.Add(actions);
        AddButton(actions, "刷新记录", Refresh);
        AddButton(actions, "导出 CSV", Export);
        AddButton(actions, "查看记录图像", OpenImage);
        DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status);
        var details = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Height = 125, Margin = new(10, 8, 10, 0), Padding = new(8) };
        DockPanel.SetDock(details, Dock.Bottom); root.Children.Add(details);
        records.SelectionChanged += (_, _) =>
        {
            if (records.SelectedItem is not InspectionRecord record) { details.Clear(); return; }
            details.Text = string.Join(Environment.NewLine, record.Report.Steps.Select(step =>
                $"{step.Name}  {(step.Success ? step.Accepted ? "OK" : "NG" : "执行失败")}  {step.ElapsedMs:F2} ms  {step.Message}" +
                Environment.NewLine + string.Join("；", step.Metrics.Select(metric => $"{metric.Name}：{metric.Value} {metric.Unit}"))));
        };
        records.Margin = new(10, 0, 10, 0);
        AddColumn("检测时间", "Timestamp", 170, "yyyy-MM-dd HH:mm:ss.fff");
        AddColumn("来源", "Source", 115);
        AddColumn("请求编号", "RequestId", 125);
        AddColumn("运行编号", "Report.RunId", 125);
        records.Columns.Add(new DataGridCheckBoxColumn { Header = "执行成功", Width = 85, Binding = new Binding("Report.Success") });
        records.Columns.Add(new DataGridCheckBoxColumn { Header = "检测合格", Width = 85, Binding = new Binding("Report.Accepted") });
        AddColumn("耗时 (ms)", "Report.ElapsedMs", 100, "F2");
        AddColumn("图像路径", "ImagePath", 260);
        root.Children.Add(records);
        Refresh();
    }

    private static void AddButton(Panel panel, string label, Action action)
    {
        var button = new Button { Content = label, Margin = new(0, 0, 8, 0), Padding = new(12, 5, 12, 5) };
        button.Click += (_, _) => action(); panel.Children.Add(button);
    }

    private void AddColumn(string title, string path, double width, string? format = null)
        => records.Columns.Add(new DataGridTextColumn { Header = title, Width = width, Binding = new Binding(path) { StringFormat = format } });

    private void Refresh()
    {
        try
        {
            var recent = journal.ReadRecent(200);
            records.ItemsSource = recent;
            if (recent.Count > 0) records.SelectedIndex = 0;
            status.Text = $"显示最近 {recent.Count} 条检测记录。";
        }
        catch (Exception ex) { status.Text = "读取失败：" + ex.Message; }
    }

    private void Export()
    {
        var dialog = new SaveFileDialog { Title = "导出检测记录", Filter = "CSV 文件|*.csv", FileName = "检测记录.csv" };
        if (dialog.ShowDialog(this) != true) return;
        try { journal.ExportCsv(dialog.FileName); status.Text = "已导出：" + dialog.FileName; }
        catch (Exception ex) { status.Text = "导出失败：" + ex.Message; }
    }

    private void OpenImage()
    {
        if (records.SelectedItem is not InspectionRecord record) { status.Text = "请先选择一条记录。"; return; }
        if (string.IsNullOrWhiteSpace(record.ImagePath) || !File.Exists(record.ImagePath))
        { status.Text = "该记录没有保留图像，或图像已被移动。"; return; }
        try { ImageSelected?.Invoke(record.ImagePath); status.Text = "已打开记录图像。"; }
        catch (Exception ex) { status.Text = "打开失败：" + ex.Message; }
    }
}
