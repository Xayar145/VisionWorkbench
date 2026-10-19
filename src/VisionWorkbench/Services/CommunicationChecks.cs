using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NModbus;
using VisionWorkbench.Communication;

namespace VisionWorkbench.Services;

public static partial class SmokeChecks
{
    private static async Task CheckCommunication(string folder, List<object> checks)
    {
        await using var server = new TcpInspectionServer();
        int triggers = 0;
        await server.StartAsync("127.0.0.1", 0, (request, token) => request.Command switch
        {
            "STATUS" => Task.FromResult(new DeviceReply(request.RequestId, true, "IDLE")),
            "TRIGGER" => Task.FromResult(new DeviceReply(request.RequestId, true, $"DONE-{Interlocked.Increment(ref triggers)}")),
            "THROW" => throw new InvalidOperationException("故障注入"),
            _ => Task.FromResult(new DeviceReply(request.RequestId, false, "ERROR", Error: "未知命令"))
        });
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, server.BoundPort);
            var stream = tcp.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            await stream.WriteAsync(Encoding.UTF8.GetBytes("{\"command\":\"STA"));
            await stream.WriteAsync(Encoding.UTF8.GetBytes("TUS\",\"requestId\":\"a\"}\n{\"command\":\"TRIGGER\",\"requestId\":\"b\"}\n"));
            var first = await ReadDeviceReply(reader);
            var second = await ReadDeviceReply(reader);
            Require(first.Success && first.RequestId == "a" && second.State == "DONE-1" && second.RequestId == "b", "TCP 分帧、粘包或响应顺序错误");
            await stream.WriteAsync(Encoding.UTF8.GetBytes("invalid\n{\"command\":\"THROW\",\"requestId\":\"err\"}\n{\"command\":\"STATUS\"}\n"));
            Require(!(await ReadDeviceReply(reader)).Success, "无效 JSON 应返回失败");
            Require((await ReadDeviceReply(reader)) is { Success: false, RequestId: "err" }, "业务异常应保留请求编号");
            Require((await ReadDeviceReply(reader)).Success, "无效请求或处理器异常不应中断连接");
        }
        checks.Add(new { name = "TCP 真连接：分帧、粘包、多请求、异常隔离", passed = true });

        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, server.BoundPort);
            await tcp.GetStream().WriteAsync(Enumerable.Repeat((byte)'x', TcpInspectionServer.MaximumLineBytes + 1).ToArray());
            using var reader = new StreamReader(tcp.GetStream());
            Require(!(await ReadDeviceReply(reader)).Success, "超长请求应拒绝");
        }
        await server.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Require(!server.IsRunning, "TCP 停止状态错误");
        await server.StartAsync("127.0.0.1", 0, (request, token) => Task.FromResult(new DeviceReply(request.RequestId, true, "IDLE")));
        using (var idle = new TcpClient())
        {
            await idle.ConnectAsync(IPAddress.Loopback, server.BoundPort);
            await server.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
        }
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await server.StartAsync("127.0.0.1", 0, async (request, token) =>
        {
            handlerStarted.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new(request.RequestId, true, "IDLE");
        });
        using (var busy = new TcpClient())
        {
            await busy.ConnectAsync(IPAddress.Loopback, server.BoundPort);
            await busy.GetStream().WriteAsync(Encoding.UTF8.GetBytes("{\"command\":\"TRIGGER\"}\n"));
            await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await server.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
        }
        checks.Add(new { name = "TCP 请求上限、停止、重启与空闲连接取消", passed = true });

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var factory = new ModbusFactory();
        using var network = factory.CreateSlaveNetwork(listener);
        var slave = factory.CreateSlave(1);
        slave.DataStore.HoldingRegisters.WritePoints(0, new ushort[] { 5, 6 });
        network.AddSlave(slave);
        using var listening = new CancellationTokenSource();
        var slaveTask = network.ListenAsync(listening.Token);
        await using var plc = new ModbusPlcClient { TimeoutMs = 500 };
        try
        {
            await plc.ConnectAsync("127.0.0.1", port, 1);
            Require(plc.IsConnected, "Modbus 主站连接失败");
            Require((await plc.ReadRegistersAsync(0, 2)).SequenceEqual(new ushort[] { 5, 6 }), "FC03 真实从站读取失败");
            await plc.WriteRegistersAsync(10, new ushort[] { 1, 2, 65535 });
            Require(slave.DataStore.HoldingRegisters.ReadPoints(10, 3).SequenceEqual(new ushort[] { 1, 2, 65535 }), "FC16 真实从站写入失败");
            var concurrent = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => plc.ReadRegistersAsync(10, 3)));
            Require(concurrent.All(x => x[2] == 65535), "Modbus 并发调用应串行执行并保留响应");
            checks.Add(new { name = "NModbus TCP：真实主从 FC03、FC16 与并发串行化", passed = true });
        }
        finally
        {
            listening.Cancel();
            network.Dispose();
            listener.Stop();
            try { await slaveTask.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }
        bool disconnected = false;
        try { await plc.ReadRegistersAsync(0, 1); }
        catch { disconnected = true; }
        Require(disconnected && !plc.IsConnected, "Modbus 断线后不能报告连接正常");

        var silentListener = new TcpListener(IPAddress.Loopback, 0);
        silentListener.Start();
        try
        {
            var accepted = silentListener.AcceptTcpClientAsync();
            await plc.ConnectAsync("127.0.0.1", ((IPEndPoint)silentListener.LocalEndpoint).Port, 1);
            using var silent = await accepted;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            bool timedOut = false;
            try { await plc.ReadRegistersAsync(0, 1); }
            catch { timedOut = true; }
            Require(timedOut && !plc.IsConnected && clock.Elapsed < TimeSpan.FromSeconds(3), "无响应 PLC 应超时并断开");
            var nextAccepted = silentListener.AcceptTcpClientAsync();
            await plc.ConnectAsync("127.0.0.1", ((IPEndPoint)silentListener.LocalEndpoint).Port, 1);
            using var cancelledConnection = await nextAccepted;
            using var cancel = new CancellationTokenSource(50);
            bool cancelled = false;
            try { await plc.ReadRegistersAsync(0, 1, cancel.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Require(cancelled && !plc.IsConnected, "主动取消 Modbus 读取应关闭实际连接");
        }
        finally { silentListener.Stop(); }
        checks.Add(new { name = "Modbus 真实断线与无响应超时", passed = true });

        await using var serial = new SerialInspectionChannel();
        string[] ports = SerialInspectionChannel.GetPortNames();
        await serial.CloseAsync();
        Require(!serial.IsOpen, "未打开的串口关闭后状态错误");
        File.WriteAllText(Path.Combine(folder, "serial-ports.json"), JsonSerializer.Serialize(new { ports, physicalLinkVerified = false }));
        checks.Add(new { name = "串口枚举与关闭生命周期（物理串口及 RTU 待设备验证）", passed = true, availablePorts = ports });
    }

    private static async Task<DeviceReply> ReadDeviceReply(StreamReader reader)
    {
        string? line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3));
        return JsonSerializer.Deserialize<DeviceReply>(line ?? throw new IOException("TCP 提前关闭"), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }
}
