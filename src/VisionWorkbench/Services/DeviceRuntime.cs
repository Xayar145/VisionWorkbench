using System.IO.Ports;
using System.Text.Json;
using System.Windows.Threading;
using VisionWorkbench.Acquisition;
using VisionWorkbench.Communication;
using VisionWorkbench.Infrastructure;
using VisionWorkbench.Models;

namespace VisionWorkbench.Services;

public sealed class DeviceConfiguration
{
    public CameraSettings Camera { get; set; } = new();
    public CommunicationSettings Communication { get; set; } = new();
    public TraceSettings Trace { get; set; } = new();
}

public record DeviceStatusMessage(string Text);
public record LiveFrameMessage(PreviewFrame Frame);
public record InspectionCompletedMessage(InspectionRecord Record);

// 窗体、TCP、串口与 PLC 共用这一条拍照检测链路，设备线程不直接访问界面。
public sealed class DeviceRuntime : IAsyncDisposable
{
    public DeviceConfiguration Configuration { get; }
    public CameraSession Camera { get; } = new();
    public TcpInspectionServer TcpServer { get; } = new();
    public ModbusPlcClient Modbus { get; } = new();
    public SerialInspectionChannel Serial { get; } = new();
    public InspectionJournal Journal { get; }
    public string Status => $"相机：{(Camera.IsConnected ? Camera.IsPreviewing ? "实时预览" : "已连接" : "未连接")}　TCP：{(TcpServer.IsRunning ? TcpServer.BoundPort.ToString() : "停止")}　串口：{(Serial.IsOpen ? "已打开" : "关闭")}　Modbus：{(IsPolling ? "轮询" : Modbus.IsConnected ? "已连接" : "未连接")}";
    public bool IsPolling { get; private set; }
    public bool IsCapturing { get; private set; }
    private readonly WorkbenchController controller;
    private readonly Dispatcher dispatcher;
    private readonly AppBus bus;
    private readonly SemaphoreSlim inspectionGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, Task<DeviceReply>> requests = new();
    private readonly string settingsPath;
    private CancellationTokenSource? pollingStop;
    private Task? pollingTask;
    private string source = "手动/AI";
    private string requestId = "";
    private bool disposed;

    public DeviceRuntime(WorkbenchController controller, AppBus bus, Dispatcher? dispatcher = null,
        DeviceConfiguration? configuration = null, string? settingsPath = null)
    {
        this.controller = controller;
        this.bus = bus;
        this.dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        this.settingsPath = settingsPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VisionWorkbench", "devices.json");
        Configuration = configuration ?? Load(this.settingsPath);
        Journal = new(Configuration.Trace);
        Camera.FrameReceived += frame => bus.Publish(new LiveFrameMessage(frame));
        Camera.StateChanged += PublishStatus;
        Camera.Logged += Log;
        TcpServer.Logged += Log;
        Serial.Logged += Log;
        controller.RunCompleted += OnRunCompleted;
    }

    public void SaveConfiguration()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        string temp = settingsPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(Configuration, WorkbenchController.JsonOptions));
        File.Move(temp, settingsPath, true);
        Log("设备配置已保存；连接参数在下次连接时生效。");
    }
    private static DeviceConfiguration Load(string path) => File.Exists(path)
        ? JsonSerializer.Deserialize<DeviceConfiguration>(File.ReadAllText(path), WorkbenchController.JsonOptions) ?? new()
        : new();

    public async Task ConnectCameraAsync() { await Camera.ConnectAsync(Configuration.Camera); PublishStatus(); }
    public async Task DisconnectCameraAsync() { await Camera.DisconnectAsync(); PublishStatus(); }
    public async Task StartPreviewAsync() { await Camera.StartPreviewAsync(); PublishStatus(); }
    public async Task StopPreviewAsync() { await Camera.StopPreviewAsync(); PublishStatus(); }
    public Task<RunReport?> CaptureAsync(bool inspect = false, CancellationToken token = default) => CaptureCore(inspect, "采集窗口", "", token);

    private async Task<RunReport?> CaptureCore(bool inspect, string triggerSource, string id, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        token = linked.Token;
        if (!await inspectionGate.WaitAsync(0, token)) throw new InvalidOperationException("当前正在拍照检测，请等待完成。");
        try
        {
            if (await dispatcher.InvokeAsync(() => controller.IsBusy)) throw new InvalidOperationException("视觉流程正在运行。");
            IsCapturing = true;
            await Camera.StopPreviewAsync();
            var frame = await Camera.SnapAsync(token);
            await dispatcher.InvokeAsync(() => controller.AcceptCapturedFrame(frame));
            if (!inspect) return null;
            source = triggerSource; requestId = id;
            return await dispatcher.InvokeAsync(() => controller.RunAsync(token: token)).Task.Unwrap();
        }
        finally
        {
            source = "手动/AI"; requestId = ""; IsCapturing = false;
            inspectionGate.Release(); PublishStatus();
        }
    }

    public async Task<DeviceReply> HandleRequestAsync(DeviceRequest request, CancellationToken token = default)
    {
        if (request.Command.Equals("STATUS", StringComparison.OrdinalIgnoreCase))
            return new(request.RequestId, true, IsCapturing || await dispatcher.InvokeAsync(() => controller.IsBusy) ? "BUSY" : Camera.IsConnected ? "READY" : "DISCONNECTED");
        if (!request.Command.Equals("TRIGGER", StringComparison.OrdinalIgnoreCase)) return new(request.RequestId, false, "ERROR", Error: "支持的命令为 STATUS、TRIGGER");
        string key = request.RequestId ?? "";
        if (key.Length == 0) return await ExecuteTriggerAsync(key, token);
        Task<DeviceReply> operation;
        TaskCompletionSource<DeviceReply>? owner = null;
        lock (requests)
        {
            if (!requests.TryGetValue(key, out operation!))
            {
                if (requests.Count >= 256)
                {
                    string? oldest = requests.FirstOrDefault(pair => pair.Value.IsCompleted).Key;
                    if (oldest != null) requests.Remove(oldest);
                }
                owner = new(TaskCreationOptions.RunContinuationsAsynchronously);
                operation = owner.Task;
                requests.Add(key, operation);
            }
        }
        if (owner != null) _ = CompleteRequestAsync(key, owner, token);
        // 同一编号的执行中重试也等待原任务，停止服务仍等待任务实际归还设备。
        return await operation;
    }

    private async Task CompleteRequestAsync(string key, TaskCompletionSource<DeviceReply> completion, CancellationToken token)
    {
        try
        {
            var reply = await ExecuteTriggerAsync(key, token);
            if (reply.Report == null) lock (requests) requests.Remove(key);
            completion.TrySetResult(reply);
        }
        catch (OperationCanceledException)
        {
            lock (requests) requests.Remove(key);
            completion.TrySetCanceled(token);
        }
        catch (Exception ex)
        {
            lock (requests) requests.Remove(key);
            completion.TrySetException(ex);
        }
    }

    private async Task<DeviceReply> ExecuteTriggerAsync(string key, CancellationToken token)
    {
        try
        {
            var report = await CaptureCore(true, "TCP/串口", key, token);
            return new DeviceReply(key, report!.Success, report.Success ? report.Accepted ? "OK" : "NG" : "ERROR", report);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new(key, false, "ERROR", Error: ex.Message); }
    }

    public async Task StartTcpAsync()
    {
        var config = Configuration.Communication;
        await TcpServer.StartAsync(config.ListenAddress, config.TcpPort, HandleRequestAsync);
        PublishStatus();
    }
    public async Task StopTcpAsync() { await TcpServer.StopAsync(); PublishStatus(); }
    public async Task OpenSerialAsync()
    {
        var c = Configuration.Communication;
        await Serial.OpenAsync(c.SerialPortName, c.SerialBaudRate, HandleRequestAsync,
            Enum.Parse<Parity>(c.SerialParity), c.SerialDataBits, Enum.Parse<StopBits>(c.SerialStopBits));
        PublishStatus();
    }
    public async Task CloseSerialAsync() { await Serial.CloseAsync(); PublishStatus(); }
    public async Task ConnectModbusAsync()
    {
        if (IsPolling) throw new InvalidOperationException("请先停止轮询。");
        var c = Configuration.Communication;
        if (c.ModbusTransport == "rtu")
            await Modbus.ConnectRtuAsync(c.ModbusSerialPortName, c.ModbusBaudRate, c.Unit,
                Enum.Parse<Parity>(c.ModbusParity), c.ModbusDataBits, Enum.Parse<StopBits>(c.ModbusStopBits));
        else await Modbus.ConnectAsync(c.ModbusHost, c.ModbusPort, c.Unit);
        PublishStatus();
    }
    public async Task DisconnectModbusAsync() { await StopModbusPollingAsync(); await Modbus.DisconnectAsync(); PublishStatus(); }
    public Task StartModbusPollingAsync()
    {
        if (IsPolling) return Task.CompletedTask;
        if (!Modbus.IsConnected || !Camera.IsConnected) throw new InvalidOperationException("请先连接相机与 Modbus。");
        var c = JsonSerializer.Deserialize<CommunicationSettings>(JsonSerializer.Serialize(Configuration.Communication))!;
        if (c.PollIntervalMs < 20 || c.ResultAddress > 65532 || c.TriggerAddress >= c.ResultAddress && c.TriggerAddress <= c.ResultAddress + 3)
            throw new ArgumentException("轮询周期至少20ms，四个结果寄存器必须有效且不能覆盖触发地址。");
        pollingStop?.Dispose();
        pollingStop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        IsPolling = true;
        pollingTask = PollAsync(c, pollingStop.Token);
        PublishStatus();
        return Task.CompletedTask;
    }
    private async Task PollAsync(CommunicationSettings c, CancellationToken token)
    {
        ushort previous = 0, sequence = 0;
        bool cyclePending = false;
        try
        {
            while (!token.IsCancellationRequested)
            {
                ushort trigger = (await Modbus.ReadRegistersAsync(c.TriggerAddress, 1, token))[0];
                if (trigger != 0 && previous == 0)
                {
                    sequence++;
                    await Modbus.WriteRegistersAsync(c.ResultAddress, [0, 0, sequence, 0], token);
                    cyclePending = true;
                    RunReport? report = null;
                    try { report = await CaptureCore(true, "Modbus", sequence.ToString(), token); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { Log("PLC 触发失败：" + ex.Message); }
                    ushort code = report is { Success: true } ? report.Accepted ? (ushort)1 : (ushort)2 : (ushort)3;
                    await Modbus.WriteRegistersAsync(c.ResultAddress, [1, code, sequence, (ushort)Math.Clamp(report?.ElapsedMs ?? 0, 0, ushort.MaxValue)], token);
                    cyclePending = false;
                }
                previous = trigger;
                await Task.Delay(c.PollIntervalMs, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 若 PLC 连接仍有效，结束被操作员停止的周期；断线时由 PLC 的握手超时处理。
            if (cyclePending && Modbus.IsConnected)
                try { await Modbus.WriteRegistersAsync(c.ResultAddress, [1, 3, sequence, 0]); }
                catch (Exception ex) { Log("停止周期的错误标记未能回写：" + ex.Message); }
        }
        catch (Exception ex) { Log("Modbus 轮询已停止：" + ex.Message); }
        finally { IsPolling = false; PublishStatus(); }
    }
    public async Task StopModbusPollingAsync()
    {
        pollingStop?.Cancel();
        if (pollingTask != null) await pollingTask;
        pollingTask = null;
        pollingStop?.Dispose(); pollingStop = null;
        PublishStatus();
    }

    private void OnRunCompleted(RunReport report, PreviewFrame input)
    {
        try
        {
            var record = Journal.Append(source, requestId, report, input);
            if (record != null) bus.Publish(new InspectionCompletedMessage(record));
        }
        catch (Exception ex) { Log("检测结果已生成，但追溯保存失败：" + ex.Message); }
    }
    private void Log(string message) => bus.Publish(new DeviceStatusMessage(message));
    private void PublishStatus() => bus.Publish(new DeviceStatusMessage(Status));
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        await StopModbusPollingAsync();
        await TcpServer.DisposeAsync();
        await Serial.DisposeAsync();
        await inspectionGate.WaitAsync();
        try { await Camera.DisposeAsync(); await Modbus.DisposeAsync(); }
        finally { inspectionGate.Release(); }
        controller.RunCompleted -= OnRunCompleted;
        lifetime.Dispose();
    }
}
