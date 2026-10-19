using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VisionWorkbench.AI;
using VisionWorkbench.Views;

namespace VisionWorkbench.Services;

public static partial class SmokeChecks
{
    private static async Task CheckDeviceUi(string folder, MainWindow window, List<object> checks)
    {
        var devices = window.Devices;
        devices.Configuration.Camera.Kind = "simulated";
        devices.Configuration.Camera.Exposure = null;
        devices.Configuration.Camera.Gain = null;
        devices.Configuration.Trace.Directory = Path.Combine(folder, "ui-station-records");
        devices.Configuration.Trace.Enabled = true;
        devices.Configuration.Trace.SaveAllImages = true;
        window.Controller.CreateDemo();
        await devices.ConnectCameraAsync();
        await devices.StartPreviewAsync();
        await WaitFor(() => devices.Camera.FrameCount >= 3);
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        Require(window.Controller.Frame is { Width: 640, Height: 400 }, "实时预览应通过数据总线更新主图像");
        Capture((FrameworkElement)window.Content, Path.Combine(folder, "ui-live.png"));

        var tools = new VisionTools(window.Controller) { Devices = devices };
        var captured = await tools.CaptureFrame(true);
        Require(captured.Contains("RunId") && window.Controller.LastReport is { Accepted: true }, "AI/Harness采图工具应执行真实流程并返回报告");
        Require(!devices.Camera.IsPreviewing, "AI拍照检测后保持结果，不自动恢复预览覆盖结果");
        var settings = new DeviceWindow(devices, window.Bus) { Owner = window };
        settings.Show();
        await window.Dispatcher.InvokeAsync(() => settings.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        Capture((FrameworkElement)settings.Content, Path.Combine(folder, "ui-devices.png"));
        var tabs = Descendants<TabControl>(settings).First();
        tabs.SelectedIndex = 1;
        await window.Dispatcher.InvokeAsync(() => settings.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        Require(Descendants<TextBox>(settings).Any(box => box.Text == "8500") || devices.Configuration.Communication.TcpPort != 8500, "通信页应展示端口配置");
        Capture((FrameworkElement)settings.Content, Path.Combine(folder, "ui-communication.png"));
        settings.Close();
        Require(devices.Camera.IsConnected, "设置窗口关闭不能擅自断开站点设备");

        var history = new HistoryWindow(devices.Journal) { Owner = window };
        history.Show();
        await window.Dispatcher.InvokeAsync(() => history.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        Require(Descendants<DataGrid>(history).Any(grid => grid.Items.Count > 0), "记录窗口应显示本次真实检测记录");
        Capture((FrameworkElement)history.Content, Path.Combine(folder, "ui-history.png"));
        history.Close();
        await devices.DisconnectCameraAsync();
        devices.Configuration.Trace.Enabled = false;
        checks.Add(new { name = "设备窗、数据总线实时预览、AI采图、通信配置与追溯记录界面", passed = true,
            screenshots = new[] { "ui-live.png", "ui-devices.png", "ui-communication.png", "ui-history.png" } });
    }
}
