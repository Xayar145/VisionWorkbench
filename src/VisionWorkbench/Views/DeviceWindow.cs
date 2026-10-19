using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VisionWorkbench.Acquisition;
using VisionWorkbench.Communication;
using VisionWorkbench.Infrastructure;
using VisionWorkbench.Services;

namespace VisionWorkbench.Views;

public sealed class DeviceWindow : Window
{
    private readonly DeviceRuntime runtime;
    private readonly IDisposable subscription;
    private readonly Dictionary<string, TextBox> fields = [];
    private readonly Dictionary<string, ComboBox> choices = [];
    private readonly List<Button> actions = [];
    private readonly TextBlock status = new() { Margin = new(12, 8, 12, 8), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock states = new() { Margin = new(12, 10, 12, 10), Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap };
    private bool closed;
    public event Action? HistoryRequested;

    public DeviceWindow(DeviceRuntime runtime, AppBus bus)
    {
        this.runtime = runtime;
        Title = "设备与通信"; Width = 790; Height = 760; MinWidth = 650; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.WhiteSmoke; FontFamily = new("Microsoft YaHei UI"); FontSize = 13;
        var root = new DockPanel(); Content = root;
        DockPanel.SetDock(states, Dock.Top); root.Children.Add(states);
        DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status);
        var tabs = new TabControl { Margin = new(10, 0, 10, 0) }; root.Children.Add(tabs);
        BuildCamera(tabs); BuildCommunication(tabs); BuildRecords(tabs);
        subscription = bus.Subscribe<DeviceStatusMessage>(message =>
        {
            if (closed) return;
            Dispatcher.InvokeAsync(() => { if (!closed) { status.Text = message.Text; UpdateStates(); } });
        });
        Closed += (_, _) => { closed = true; subscription.Dispose(); };
        UpdateStates();
    }

    private void BuildCamera(TabControl tabs)
    {
        var panel = Page(tabs, "相机采集"); var config = runtime.Configuration.Camera;
        Heading(panel, "采集源");
        var driver = Choice(panel, "camera.kind", "驱动类型", config.Kind,
            ("simulated", "模拟相机"), ("hikrobot", "海康机器人 MVS SDK"), ("halcon", "HALCON 通用工业相机"), ("usb", "USB 相机 / OpenCV"), ("replay", "文件回放"));
        driver.IsEditable = true;
        if (driver.SelectedItem == null) driver.Text = config.Kind;
        var device = Choice(panel, "camera.device", "设备 ID / 相机序号 / 回放路径", config.Device);
        device.IsEditable = true;
        Field(panel, "camera.sdk", "海康托管 SDK DLL 路径", config.SdkPath);
        Field(panel, "camera.interface", "HALCON 采集接口", config.InterfaceName);
        Note(panel, "海康填写 MvCameraControl.Net.dll 的完整路径，并安装对应 MVS 运行环境。HALCON 可填写 GigEVision2、GenICamTL 等已安装接口。回放源在设备栏填写目录、图像或视频路径。");
        var enumerate = Buttons(panel);
        Action(enumerate, "枚举相机", async () =>
        {
            var settings = CameraValues();
            var found = await CameraDevices.EnumerateAsync(settings);
            var oldText = device.Text;
            device.Items.Clear();
            foreach (var item in found) device.Items.Add(new ComboBoxItem { Content = item.Name, Tag = item.Id });
            if (found.Count > 0) device.SelectedIndex = 0; else device.Text = oldText;
            status.Text = $"发现 {found.Count} 个设备。";
        });
        Heading(panel, "采集参数");
        Field(panel, "camera.exposure", "曝光（海康 µs；USB 为驱动单位）", Format(config.Exposure));
        Field(panel, "camera.gain", "增益（空白为设备默认）", Format(config.Gain));
        Field(panel, "camera.interval", "预览帧间隔（毫秒）", config.FrameIntervalMs);
        Note(panel, "连接后按“开始预览”可在主窗口连续显示图像。单次拍照与检测会停止预览。设备窗口关闭后，已连接设备继续工作。");
        var row = Buttons(panel);
        Action(row, "连接相机", async () => { ApplyCamera(); await runtime.ConnectCameraAsync(); });
        Action(row, "断开相机", runtime.DisconnectCameraAsync);
        Action(row, "单次拍照", async () => { await runtime.CaptureAsync(); });
        Action(row, "拍照检测", async () => { await runtime.CaptureAsync(true); });
        row = Buttons(panel);
        Action(row, "开始预览", runtime.StartPreviewAsync);
        Action(row, "停止预览", runtime.StopPreviewAsync);
        Action(row, "应用曝光与增益", async () => { var value = CameraValues(); await runtime.Camera.ApplyParametersAsync(value.Exposure, value.Gain); ApplyCamera(); });
        Action(row, "保存采集设置", () => { ApplyCamera(); return Task.CompletedTask; });
    }

    private void BuildCommunication(TabControl tabs)
    {
        var panel = Page(tabs, "通信连接"); var config = runtime.Configuration.Communication;
        Heading(panel, "TCP/IP 检测服务");
        Field(panel, "tcp.address", "监听 IP 地址", config.ListenAddress);
        Field(panel, "tcp.port", "监听端口", config.TcpPort);
        Note(panel, "按行收发 UTF-8 JSON。支持 STATUS 查询和 TRIGGER 拍照检测；设备协议与 AI Harness 使用不同端口。");
        var row = Buttons(panel);
        Action(row, "启动 TCP 服务", async () => { ApplyTcp(); await runtime.StartTcpAsync(); });
        Action(row, "停止 TCP 服务", runtime.StopTcpAsync);

        Heading(panel, "串口检测服务");
        Field(panel, "serial.port", "串口名", config.SerialPortName);
        Field(panel, "serial.baud", "波特率", config.SerialBaudRate);
        Choice(panel, "serial.parity", "校验", config.SerialParity, ("None", "无校验"), ("Odd", "奇校验"), ("Even", "偶校验"));
        Field(panel, "serial.bits", "数据位", config.SerialDataBits);
        Choice(panel, "serial.stop", "停止位", config.SerialStopBits, ("One", "1"), ("Two", "2"), ("OnePointFive", "1.5"));
        Note(panel, "串口采用与 TCP 相同的 JSON 行协议。串口检测服务与 Modbus RTU 不能同时占用同一串口。");
        row = Buttons(panel);
        Action(row, "查看可用串口", () => { status.Text = "可用串口：" + string.Join("、", SerialInspectionChannel.GetPortNames()); return Task.CompletedTask; });
        Action(row, "打开串口", async () => { ApplySerial(); await runtime.OpenSerialAsync(); });
        Action(row, "关闭串口", runtime.CloseSerialAsync);

        Heading(panel, "Modbus PLC");
        Choice(panel, "modbus.transport", "连接方式", config.ModbusTransport, ("tcp", "Modbus TCP"), ("rtu", "Modbus RTU"));
        Field(panel, "modbus.host", "PLC IP 地址（TCP）", config.ModbusHost);
        Field(panel, "modbus.port", "PLC 端口（TCP）", config.ModbusPort);
        Field(panel, "modbus.serial", "串口名（RTU）", config.ModbusSerialPortName);
        Field(panel, "modbus.baud", "波特率（RTU）", config.ModbusBaudRate);
        Choice(panel, "modbus.parity", "校验（RTU）", config.ModbusParity, ("None", "无校验"), ("Odd", "奇校验"), ("Even", "偶校验"));
        Field(panel, "modbus.bits", "数据位（RTU）", config.ModbusDataBits);
        Choice(panel, "modbus.stop", "停止位（RTU）", config.ModbusStopBits, ("One", "1"), ("Two", "2"), ("OnePointFive", "1.5"));
        Field(panel, "modbus.unit", "从站地址", config.Unit);
        Field(panel, "modbus.trigger", "触发保持寄存器地址（从 0 开始）", config.TriggerAddress);
        Field(panel, "modbus.result", "结果保持寄存器起始地址（从 0 开始）", config.ResultAddress);
        Field(panel, "modbus.interval", "轮询间隔（毫秒）", config.PollIntervalMs);
        Note(panel, "触发寄存器从 0 变为非 0 时拍照检测。结果连续占用 4 个保持寄存器：完成标记、判定（1=OK，2=NG，3=错误）、序号、耗时毫秒。触发地址不能覆盖结果区域。");
        row = Buttons(panel);
        Action(row, "连接 PLC", async () => { ApplyModbus(); await runtime.ConnectModbusAsync(); });
        Action(row, "断开 PLC", runtime.DisconnectModbusAsync);
        Action(row, "启动触发轮询", runtime.StartModbusPollingAsync);
        Action(row, "停止轮询", runtime.StopModbusPollingAsync);
        row = Buttons(panel);
        Action(row, "保存通信设置", () => { ApplyTcp(); ApplySerial(); ApplyModbus(); return Task.CompletedTask; });
    }

    private void BuildRecords(TabControl tabs)
    {
        var panel = Page(tabs, "检测记录");
        var config = runtime.Configuration.Trace;
        Heading(panel, "检测追溯");
        Note(panel, "检测执行后记录来源、请求编号、结果、耗时与各步骤指标。可在检测记录窗口刷新、导出 CSV，并打开已保存的图像。");
        var enabled = new CheckBox { Content = "启用检测记录", IsChecked = config.Enabled, Margin = new(0, 0, 0, 12) };
        var ng = new CheckBox { Content = "保存 NG 图像", IsChecked = config.SaveNgImages, Margin = new(0, 0, 0, 12) };
        var all = new CheckBox { Content = "保存全部检测图像", IsChecked = config.SaveAllImages, Margin = new(0, 0, 0, 12) };
        panel.Children.Add(enabled); panel.Children.Add(ng); panel.Children.Add(all);
        Field(panel, "trace.directory", "记录保存目录", config.Directory);
        Note(panel, "图像按日期分目录保存。保存全部图像时会占用较多磁盘空间，请定期归档。");
        Action(Buttons(panel), "保存记录设置", () =>
        {
            config.Enabled = enabled.IsChecked == true; config.SaveNgImages = ng.IsChecked == true;
            config.SaveAllImages = all.IsChecked == true; config.Directory = Value("trace.directory");
            runtime.SaveConfiguration(); return Task.CompletedTask;
        });
        Action(Buttons(panel), "打开检测记录", () => { HistoryRequested?.Invoke(); return Task.CompletedTask; });
    }

    private CameraSettings CameraValues() => new()
    {
        Kind = Value("camera.kind"), Device = Value("camera.device"), SdkPath = Value("camera.sdk"), InterfaceName = Value("camera.interface"),
        Exposure = OptionalNumber("camera.exposure"), Gain = OptionalNumber("camera.gain"), FrameIntervalMs = Number("camera.interval")
    };

    private void ApplyCamera()
    {
        var value = CameraValues(); var target = runtime.Configuration.Camera;
        target.Kind = value.Kind; target.Device = value.Device; target.SdkPath = value.SdkPath; target.InterfaceName = value.InterfaceName;
        target.Exposure = value.Exposure; target.Gain = value.Gain; target.FrameIntervalMs = value.FrameIntervalMs;
        runtime.SaveConfiguration();
    }

    private void ApplyTcp()
    {
        var address = Value("tcp.address"); var port = Number("tcp.port");
        var target = runtime.Configuration.Communication;
        target.ListenAddress = address; target.TcpPort = port; runtime.SaveConfiguration();
    }

    private void ApplySerial()
    {
        var port = Value("serial.port"); var baud = Number("serial.baud"); var bits = Number("serial.bits");
        var target = runtime.Configuration.Communication;
        target.SerialPortName = port; target.SerialBaudRate = baud; target.SerialDataBits = bits;
        target.SerialParity = Value("serial.parity"); target.SerialStopBits = Value("serial.stop"); runtime.SaveConfiguration();
    }

    private void ApplyModbus()
    {
        var port = Number("modbus.port"); var baud = Number("modbus.baud"); var bits = Number("modbus.bits");
        var unit = byte.Parse(Value("modbus.unit"), CultureInfo.InvariantCulture);
        var trigger = ushort.Parse(Value("modbus.trigger"), CultureInfo.InvariantCulture);
        var result = ushort.Parse(Value("modbus.result"), CultureInfo.InvariantCulture);
        var interval = Number("modbus.interval"); var target = runtime.Configuration.Communication;
        target.ModbusTransport = Value("modbus.transport"); target.ModbusHost = Value("modbus.host"); target.ModbusPort = port;
        target.ModbusSerialPortName = Value("modbus.serial"); target.ModbusBaudRate = baud; target.ModbusDataBits = bits;
        target.ModbusParity = Value("modbus.parity"); target.ModbusStopBits = Value("modbus.stop");
        target.Unit = unit; target.TriggerAddress = trigger; target.ResultAddress = result; target.PollIntervalMs = interval;
        runtime.SaveConfiguration();
    }

    private void UpdateStates()
    {
        states.Text = $"相机：{(runtime.Camera.IsConnected ? "已连接" : "未连接")}　预览：{(runtime.Camera.IsPreviewing ? "运行中" : "已停止")}　" +
            $"TCP：{(runtime.TcpServer.IsRunning ? "监听中" : "已停止")}　串口：{(runtime.Serial.IsOpen ? "已打开" : "已关闭")}　" +
            $"PLC：{(runtime.Modbus.IsConnected ? "已连接" : "未连接")}　轮询：{(runtime.IsPolling ? "运行中" : "已停止")}";
    }

    private void Action(Panel panel, string label, Func<Task> action)
    {
        var button = new Button { Content = label, Margin = new(0, 0, 8, 7), Padding = new(10, 5, 10, 5) };
        actions.Add(button); panel.Children.Add(button);
        button.Click += async (_, _) =>
        {
            foreach (var item in actions) item.IsEnabled = false;
            status.Text = "正在执行：" + label;
            try { await action(); if (status.Text == "正在执行：" + label) status.Text = "操作完成。"; }
            catch (Exception ex) { status.Text = "操作失败：" + ex.Message; }
            finally { foreach (var item in actions) item.IsEnabled = true; UpdateStates(); }
        };
    }

    private static StackPanel Page(TabControl tabs, string header)
    {
        var panel = new StackPanel { Margin = new(16) };
        tabs.Items.Add(new TabItem { Header = header, Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel } });
        return panel;
    }
    private static void Heading(Panel panel, string text)
        => panel.Children.Add(new TextBlock { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new(0, 7, 0, 12) });
    private static void Note(Panel panel, string text)
        => panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new(0, 0, 0, 12), LineHeight = 21 });
    private static WrapPanel Buttons(Panel panel)
    { var row = new WrapPanel { Margin = new(0, 3, 0, 12) }; panel.Children.Add(row); return row; }
    private static Grid Row(Panel panel, string label)
    {
        var row = new Grid { Margin = new(0, 0, 0, 9) };
        row.ColumnDefinitions.Add(new() { Width = new(265) }); row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 12, 0) });
        panel.Children.Add(row); return row;
    }
    private void Field(Panel panel, string key, string label, object value)
    {
        var row = Row(panel, label); var box = new TextBox { Text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "", Padding = new(5), MinHeight = 29 };
        fields.Add(key, box); Grid.SetColumn(box, 1); row.Children.Add(box);
    }
    private ComboBox Choice(Panel panel, string key, string label, string value, params (string Value, string Label)[] options)
    {
        var row = Row(panel, label); var box = new ComboBox { MinHeight = 29, Padding = new(4) };
        foreach (var option in options)
        {
            var item = new ComboBoxItem { Content = option.Label, Tag = option.Value }; box.Items.Add(item);
            if (option.Value == value) box.SelectedItem = item;
        }
        if (options.Length == 0) { box.IsEditable = true; box.Text = value; }
        choices.Add(key, box); Grid.SetColumn(box, 1); row.Children.Add(box); return box;
    }
    private string Value(string key)
    {
        if (fields.TryGetValue(key, out var field)) return field.Text.Trim();
        var choice = choices[key];
        if (choice.IsEditable && choice.SelectedItem is ComboBoxItem selected && choice.Text == Convert.ToString(selected.Content))
            return Convert.ToString(selected.Tag) ?? "";
        return choice.IsEditable ? choice.Text.Trim() : Convert.ToString((choice.SelectedItem as ComboBoxItem)?.Tag) ?? "";
    }
    private int Number(string key) => int.Parse(Value(key), CultureInfo.InvariantCulture);
    private double? OptionalNumber(string key) => string.IsNullOrWhiteSpace(Value(key)) ? null : double.Parse(Value(key), CultureInfo.InvariantCulture);
    private static string Format(double? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";
}
