using System.IO.Ports;
using System.Text;
using System.Text.Json;

namespace VisionWorkbench.Communication;

public sealed class SerialInspectionChannel : IAsyncDisposable
{
    private SerialPort? port;
    private CancellationTokenSource? cancellation;
    private Task? readTask;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public bool IsOpen => port?.IsOpen == true;
    public event Action<string>? Logged;
    public static string[] GetPortNames() => SerialPort.GetPortNames().Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();

    public Task OpenAsync(string portName, int baudRate, Func<DeviceRequest, CancellationToken, Task<DeviceReply>> handler,
        Parity parity = Parity.None, int dataBits = 8, StopBits stopBits = StopBits.One)
    {
        if (IsOpen) throw new InvalidOperationException("串口已经打开。");
        var serial = new SerialPort(portName, baudRate, parity, dataBits, stopBits)
        { Encoding = new UTF8Encoding(false), NewLine = "\n", ReadTimeout = 200, WriteTimeout = 1500 };
        try { serial.Open(); }
        catch { serial.Dispose(); throw; }
        port = serial;
        cancellation = new();
        readTask = Task.Run(() => ReadAsync(serial, handler, cancellation.Token));
        Log($"串口已打开：{portName}，{baudRate}，{dataBits}/{parity}/{stopBits}");
        return Task.CompletedTask;
    }

    private async Task ReadAsync(SerialPort serial, Func<DeviceRequest, CancellationToken, Task<DeviceReply>> handler, CancellationToken token)
    {
        // SerialPort 的超时读取可被定期取消，避免 BaseStream 在某些串口驱动上无法取消。
        using var line = new MemoryStream();
        try
        {
            while (!token.IsCancellationRequested)
            {
                int value;
                try { value = serial.ReadByte(); }
                catch (TimeoutException) { continue; }
                if (value < 0) break;
                if (value != '\n')
                {
                    if (line.Length >= TcpInspectionServer.MaximumLineBytes) throw new IOException("串口请求行超过 16 KiB，连接已关闭。");
                    line.WriteByte((byte)value);
                    continue;
                }
                DeviceReply reply;
                string requestId = "";
                try
                {
                    var request = JsonSerializer.Deserialize<DeviceRequest>(line.GetBuffer().AsSpan(0, (int)line.Length), JsonOptions);
                    if (request is null || string.IsNullOrWhiteSpace(request.Command)) throw new JsonException("缺少 command。");
                    requestId = request.RequestId ?? "";
                    reply = await handler(request, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) { reply = new(requestId, false, "ERROR", Error: ex.Message); }
                line.SetLength(0);
                line.Position = 0;
                serial.WriteLine(JsonSerializer.Serialize(reply, JsonOptions));
            }
        }
        catch (Exception ex) { if (!token.IsCancellationRequested) Log($"串口退出：{ex.Message}"); }
        finally { serial.Close(); }
    }

    public async Task CloseAsync()
    {
        if (port is null) return;
        cancellation!.Cancel();
        port.Close();
        if (readTask is not null) await readTask;
        port.Dispose();
        port = null;
        cancellation.Dispose();
        cancellation = null;
        Log("串口已关闭");
    }

    private void Log(string text) { try { Logged?.Invoke(text); } catch { /* 日志订阅者不影响通信。 */ } }
    public async ValueTask DisposeAsync() => await CloseAsync();
}
