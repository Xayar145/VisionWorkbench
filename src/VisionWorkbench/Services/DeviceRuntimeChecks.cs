using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NModbus;
using VisionWorkbench.Acquisition;
using VisionWorkbench.Communication;
using VisionWorkbench.Infrastructure;
using VisionWorkbench.Models;

namespace VisionWorkbench.Services;

public static partial class SmokeChecks
{
    private static async Task CheckDeviceRuntime(string folder, List<object> checks)
    {
        var bus = new AppBus();
        int messages = 0;
        var subscription = bus.Subscribe<DeviceStatusMessage>(_ => messages++);
        bus.Publish(new DeviceStatusMessage("test"));
        subscription.Dispose();
        bus.Publish(new DeviceStatusMessage("test"));
        Require(messages == 1, "数据总线退订后不能继续收到消息");
        checks.Add(new { name = "CommunityToolkit 数据总线发布与窗体退订", passed = true });

        var configuration = new DeviceConfiguration();
        configuration.Camera.Kind = "simulated";
        configuration.Communication.TcpPort = 0;
        configuration.Trace.Directory = Path.Combine(folder, "station-records-" + Guid.NewGuid().ToString("N")[..8]);
        configuration.Trace.SaveAllImages = true;
        var controller = new WorkbenchController();
        controller.CreateDemo();
        string configPath = Path.Combine(folder, "设备配置.json");
        await using var runtime = new DeviceRuntime(controller, bus, configuration: configuration, settingsPath: configPath);
        runtime.SaveConfiguration();
        Require(JsonSerializer.Deserialize<DeviceConfiguration>(File.ReadAllText(configPath))!.Communication.ResultAddress == 10, "设备配置应可保存重开");
        await runtime.ConnectCameraAsync();
        await runtime.StartPreviewAsync();
        await WaitFor(() => runtime.Camera.FrameCount >= 2);
        var direct = await runtime.CaptureAsync(true);
        Require(!runtime.Camera.IsPreviewing && direct is { Success: true, Accepted: true }, "拍照检测应停止预览并执行真实图像流程");
        var firstRecord = runtime.Journal.ReadRecent().Single();
        Require(firstRecord.Report.RunId == direct!.RunId && File.Exists(firstRecord.ImagePath), "追溯报告和存图必须属于本次运行");
        controller.SaveProject(Path.Combine(folder, "采集方案.json"));
        var restored = new WorkbenchController();
        restored.OpenProject(Path.Combine(folder, "采集方案.json"));
        Require((await restored.RunAsync()).Accepted, "内存采集图保存方案后应能重开检测");
        checks.Add(new { name = "实时预览到内存采图、HALCON检测、存图及方案重开", passed = true });

        await runtime.StartTcpAsync();
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, runtime.TcpServer.BoundPort);
            using var writer = new StreamWriter(tcp.GetStream(), new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(tcp.GetStream(), Encoding.UTF8, leaveOpen: true);
            var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            await writer.WriteLineAsync("{\"command\":\"TRIGGER\",\"requestId\":\"cycle-1\"}");
            var reply = JsonSerializer.Deserialize<DeviceReply>((await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!, jsonOptions)!;
            Require(reply is { Success: true, State: "OK", Report: not null }, "TCP请求应执行相机采图与HALCON检测并返回真实报告");
            long count = runtime.Camera.FrameCount;
            await writer.WriteLineAsync("{\"command\":\"TRIGGER\",\"requestId\":\"cycle-1\"}");
            var repeat = JsonSerializer.Deserialize<DeviceReply>((await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))!, jsonOptions)!;
            Require(repeat.Report!.RunId == reply.Report!.RunId && runtime.Camera.FrameCount == count, "已完成请求重发不能再次采图检测");
            var record = runtime.Journal.ReadRecent().First();
            Require(record.RequestId == "cycle-1" && record.Report.RunId == reply.Report.RunId, "通信回传与追溯记录应关联同一请求和运行编号");
        }
        await runtime.StopTcpAsync();
        runtime.Journal.ExportCsv(Path.Combine(folder, "检测追溯.csv"));
        Require(File.ReadAllText(Path.Combine(folder, "检测追溯.csv")).Contains("cycle-1"), "追溯CSV应包含原请求编号");
        checks.Add(new { name = "真实TCP触发：采图、算法、请求去重、报告回传与追溯CSV闭环", passed = true });

        FrameSources.Register("runtime-delayed", () => new DelayedFrameSource());
        configuration.Camera.Kind = "runtime-delayed";
        await runtime.ConnectCameraAsync();
        var repeats = await Task.WhenAll(runtime.HandleRequestAsync(new("TRIGGER", "in-flight")),
            runtime.HandleRequestAsync(new("TRIGGER", "in-flight")));
        Require(repeats.All(reply => reply.Report != null) && repeats[0].Report!.RunId == repeats[1].Report!.RunId
            && runtime.Camera.FrameCount == 1, "同编号执行中重试必须共享一次采图和同一份报告");
        configuration.Camera.Kind = "simulated";
        await runtime.ConnectCameraAsync();
        checks.Add(new { name = "执行中的同编号并发请求仅采图一次并共享结果", passed = true });

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var factory = new ModbusFactory();
        using var network = factory.CreateSlaveNetwork(listener);
        var slave = factory.CreateSlave(1);
        network.AddSlave(slave);
        using var stop = new CancellationTokenSource();
        var serving = network.ListenAsync(stop.Token);
        try
        {
            configuration.Communication.ModbusPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            configuration.Communication.PollIntervalMs = 30;
            await runtime.ConnectModbusAsync();
            await runtime.StartModbusPollingAsync();
            slave.DataStore.HoldingRegisters.WritePoints(0, [1]);
            await WaitFor(() => slave.DataStore.HoldingRegisters.ReadPoints(10, 4) is [1, 1, 1, _]);
            long afterFirst = runtime.Camera.FrameCount;
            await Task.Delay(150);
            Require(runtime.Camera.FrameCount == afterFirst, "PLC触发保持非零不能重复拍照");
            slave.DataStore.HoldingRegisters.WritePoints(0, [0]);
            await Task.Delay(120);
            var countStep = controller.Document.Steps.Single(step => step.Kind == "halcon.blob");
            controller.SetParameter(countStep.Id, "maxCount", 4);
            controller.SetParameter(countStep.Id, "minCount", 4);
            slave.DataStore.HoldingRegisters.WritePoints(0, [1]);
            await WaitFor(() => slave.DataStore.HoldingRegisters.ReadPoints(10, 4) is [1, 2, 2, _]);
            slave.DataStore.HoldingRegisters.WritePoints(0, [0]);
            await Task.Delay(120);
            await runtime.DisconnectCameraAsync();
            slave.DataStore.HoldingRegisters.WritePoints(0, [1]);
            await WaitFor(() => slave.DataStore.HoldingRegisters.ReadPoints(10, 4) is [1, 3, 3, _]);
            slave.DataStore.HoldingRegisters.WritePoints(0, [0]);
            await Task.Delay(120);
            configuration.Camera.Kind = "runtime-delayed";
            await runtime.ConnectCameraAsync();
            slave.DataStore.HoldingRegisters.WritePoints(0, [1]);
            await WaitFor(() => runtime.IsCapturing);
            await runtime.StopModbusPollingAsync();
            Require(!runtime.IsPolling, "停止后轮询状态应恢复");
            Require(slave.DataStore.HoldingRegisters.ReadPoints(10, 4) is [1, 3, 4, _], "停止采集周期时应向仍连接的PLC回写错误完成标记");
            var recent = runtime.Journal.ReadRecent();
            Require(recent.Count(r => r.Source == "Modbus") == 2, "PLC两次上升沿应产生两份真实追溯报告");
            checks.Add(new { name = "真实Modbus主从：OK、NG、采集错误回写及触发保持去重", passed = true });
        }
        finally
        {
            await runtime.DisconnectModbusAsync();
            stop.Cancel(); network.Dispose(); listener.Stop();
            try { await serving.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }
        await runtime.DisconnectCameraAsync();
        var failure = await runtime.HandleRequestAsync(new("TRIGGER", "disconnected"));
        Require(!failure.Success && failure.Report == null, "相机断开后不能重复返回上次图像的成功报告");
        checks.Add(new { name = "相机未连接时拒绝远程检测并保留错误状态", passed = true });

        var shutdownConfiguration = new DeviceConfiguration();
        shutdownConfiguration.Camera.Kind = "runtime-delayed";
        shutdownConfiguration.Trace.Enabled = false;
        await using var closing = new DeviceRuntime(controller, bus, configuration: shutdownConfiguration);
        await closing.ConnectCameraAsync();
        var pending = closing.HandleRequestAsync(new("TRIGGER", "shutdown"));
        await WaitFor(() => closing.IsCapturing);
        await closing.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        await pending;
        Require(!closing.Camera.IsConnected && !closing.IsCapturing, "关闭应等待执行中的采集归还设备，不残留后台任务");
        checks.Add(new { name = "执行中关闭：取消采集、归还互斥锁并释放设备", passed = true });
    }

    private static async Task WaitFor(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!predicate()) await Task.Delay(25, timeout.Token);
    }

    private sealed class DelayedFrameSource : IFrameSource
    {
        private readonly IFrameSource inner = FrameSources.Create("simulated");
        public void Open(CameraSettings settings) => inner.Open(settings);
        public PreviewFrame Grab(CancellationToken token)
        {
            token.WaitHandle.WaitOne(180);
            token.ThrowIfCancellationRequested();
            return inner.Grab(token);
        }
        public void ApplyParameters(double? exposure, double? gain) => inner.ApplyParameters(exposure, gain);
        public void Dispose() => inner.Dispose();
    }
}
