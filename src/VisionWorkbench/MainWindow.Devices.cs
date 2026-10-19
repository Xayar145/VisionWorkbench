using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using VisionWorkbench.Infrastructure;
using VisionWorkbench.Models;
using VisionWorkbench.Services;
using VisionWorkbench.Views;

namespace VisionWorkbench;

public partial class MainWindow
{
    public AppBus Bus { get; } = new();
    public DeviceRuntime Devices { get; private set; } = null!;
    private readonly List<IDisposable> deviceSubscriptions = [];
    private DeviceWindow? deviceWindow;
    private HistoryWindow? historyWindow;
    private PreviewFrame? pendingLiveFrame;
    private int frameQueued;
    private bool closingDevices, devicesClosed;

    private void InitializeDevices()
    {
        try { Devices = new(Controller, Bus, Dispatcher); }
        catch (Exception ex)
        {
            Devices = new(Controller, Bus, Dispatcher, new DeviceConfiguration());
            AppendLog("设备配置读取失败，使用默认配置，原文件保留：" + ex.Message);
        }
        deviceSubscriptions.Add(Bus.Subscribe<DeviceStatusMessage>(message =>
            Dispatcher.InvokeAsync(() => { DeviceStatus.Text = Devices.Status; AppendLog(message.Text); })));
        deviceSubscriptions.Add(Bus.Subscribe<LiveFrameMessage>(message =>
        {
            if (!Devices.Camera.IsPreviewing || Devices.IsCapturing) return;
            Volatile.Write(ref pendingLiveFrame, message.Frame);
            if (Interlocked.Exchange(ref frameQueued, 1) != 0) return;
            Dispatcher.InvokeAsync(() =>
            {
                var frame = Interlocked.Exchange(ref pendingLiveFrame, null);
                Interlocked.Exchange(ref frameQueued, 0);
                if (frame == null || closingDevices || !Devices.Camera.IsPreviewing || Devices.IsCapturing || Controller.IsBusy) return;
                Controller.AcceptCapturedFrame(frame);
                DeviceStatus.Text = Devices.Status + $"　帧：{Devices.Camera.FrameCount}";
            }, DispatcherPriority.Background);
        }));
        DeviceStatus.Text = Devices.Status;
        Closing += CloseDevices;
    }

    private void Devices_Click(object sender, RoutedEventArgs e)
    {
        if (deviceWindow != null) { deviceWindow.Activate(); return; }
        deviceWindow = new(Devices, Bus) { Owner = this };
        deviceWindow.HistoryRequested += ShowHistory;
        deviceWindow.Closed += (_, _) => deviceWindow = null;
        deviceWindow.Show();
    }
    private void History_Click(object sender, RoutedEventArgs e) => ShowHistory();
    private void ShowHistory()
    {
        if (historyWindow != null) { historyWindow.Activate(); return; }
        historyWindow = new(Devices.Journal) { Owner = this };
        historyWindow.ImageSelected += async path =>
        {
            try { await Devices.StopPreviewAsync(); Perform(() => Controller.LoadImage(path)); }
            catch (Exception ex) { AppendLog("记录图像加载失败：" + ex.Message); }
        };
        historyWindow.Closed += (_, _) => historyWindow = null;
        historyWindow.Show();
    }
    private async void CaptureDevice_Click(object sender, RoutedEventArgs e)
    {
        try { await Devices.CaptureAsync(); }
        catch (Exception ex) { AppendLog("拍照失败：" + ex.Message); }
    }
    private async void InspectDevice_Click(object sender, RoutedEventArgs e)
    {
        try { await Devices.CaptureAsync(true); }
        catch (Exception ex) { AppendLog("拍照检测失败：" + ex.Message); }
    }
    private async void PreviewDevice_Click(object sender, RoutedEventArgs e)
    {
        try { await Devices.StartPreviewAsync(); }
        catch (Exception ex) { AppendLog("预览失败：" + ex.Message); }
    }
    private async void StopPreviewDevice_Click(object sender, RoutedEventArgs e)
    {
        try { await Devices.StopPreviewAsync(); }
        catch (Exception ex) { AppendLog("停止预览失败：" + ex.Message); }
    }

    private async void CloseDevices(object? sender, CancelEventArgs e)
    {
        if (devicesClosed) return;
        e.Cancel = true;
        if (closingDevices) return;
        closingDevices = true;
        IsEnabled = false;
        if (deviceWindow != null) deviceWindow.IsEnabled = false;
        cancellation?.Cancel();
        try
        {
            harness.Dispose();
            await Devices.DisposeAsync();
            await Controller.WaitForIdleAsync();
        }
        catch (Exception ex) { AppendLog("设备关闭异常：" + ex.Message); }
        foreach (var subscription in deviceSubscriptions) subscription.Dispose();
        devicesClosed = true;
        Close();
    }
}
