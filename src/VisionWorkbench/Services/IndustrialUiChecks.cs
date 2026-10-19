using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VisionWorkbench.Models;
using VisionWorkbench.Vision;

namespace VisionWorkbench.Services;

public static partial class SmokeChecks
{
    private static void CheckIndustrialNavigation(MainWindow window, List<object> checks)
    {
        var menus = Descendants<Menu>(window).ToArray();
        Require(menus.Length > 0, "主窗口应提供传统菜单栏");
        var rootItems = menus.SelectMany(m => m.Items.OfType<MenuItem>()).ToArray();
        Require(rootItems.Length >= 4, "菜单栏应包含文件、运行、视图等常用菜单");
        Require(rootItems.All(m => Convert.ToString(m.Header)!.Any(c => c >= '\u4e00' && c <= '\u9fff')), "主菜单标题应为中文");
        var tree = Descendants<TreeView>(window).Single();
        var categories = tree.Items.OfType<MainWindow.ToolCategoryGroup>().ToArray();
        var tools = categories.SelectMany(c => c.Tools).Select(t => t.Definition).ToArray();
        Require(tools.Length == ToolCatalog.All.Count && tools.Select(t => t.Kind).Distinct().Count() == tools.Length, "分类目录应完整展示每个视觉算子且不重复");
        Require(categories.Length >= 3, "算子目录应按至少三个功能分类");
        var search = (TextBox)window.FindName("SearchBox");
        search.Text = "圆测量";
        var matches = tree.Items.OfType<MainWindow.ToolCategoryGroup>().ToArray();
        Require(matches.Length > 0 && matches.All(group => group.Expanded)
            && matches.SelectMany(group => group.Tools).Any(tool => tool.Kind == "halcon.metrology_circle"), "搜索应展开匹配分类并显示圆测量模型");
        search.Clear();
        Require(tree.Items.OfType<MainWindow.ToolCategoryGroup>().Sum(group => group.Tools.Count) == tools.Length, "清空搜索后应恢复全部算子");
        checks.Add(new { name = "中文传统菜单栏与完整分类算子目录", passed = true, visualToolCount = tools.Length });
    }

    private static IEnumerable<TreeViewItem> EnumerateTreeItems(ItemsControl parent)
    {
        foreach (var item in parent.Items)
        {
            var container = item as TreeViewItem ?? parent.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem;
            if (container == null) continue;
            yield return container;
            foreach (var child in EnumerateTreeItems(container)) yield return child;
        }
    }

    private static async Task CheckMeasurementUi(string folder, MainWindow window, List<object> checks)
    {
        var controller = window.Controller;
        controller.CreateMetrologyDemo();
        var step = controller.Document.Steps.Single(s => s.Kind == "halcon.metrology_circle");
        ((ListBox)window.FindName("StepList")).SelectedItem = step;
        foreach (var tab in Descendants<TabItem>(window))
            if (Convert.ToString(tab.Header) is "工具参数" or "属性与参数") tab.IsSelected = true;
        Require((await controller.RunAsync()).Accepted, "界面测量场景应真实拟合示例圆");
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        var fields = Descendants<TextBox>((StackPanel)window.FindName("ParameterPanel")).ToArray();
        Require(fields.Any(f => (string?)f.Tag == "length1") && fields.Any(f => (string?)f.Tag == "threshold"), "参数面板应显示测量模型的卡尺与边缘参数");
        Require(((DataGrid)window.FindName("ResultGrid")).Items.Count > 0 && controller.Frame!.Overlays.Any(o => o.Shape == "circle"), "界面应显示真实圆测量结果与圆形叠加");
        Capture((FrameworkElement)window.Content, Path.Combine(folder, "ui-measurement-roi.png"));
        checks.Add(new { name = "圆ROI、测量模型参数及拟合结果界面", passed = true, screenshot = "ui-measurement-roi.png" });
    }
}
