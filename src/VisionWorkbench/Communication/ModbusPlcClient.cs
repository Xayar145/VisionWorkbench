using System.Net.Sockets;
using System.IO.Ports;
using NModbus;
using NModbus.Serial;

namespace VisionWorkbench.Communication;

public sealed class ModbusPlcClient : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private TcpClient? client;
    private IModbusMaster? master;
    private SerialPort? serial;
    private byte unit;
    public int TimeoutMs { get; set; } = 1500;
    public bool IsConnected
    {
        get
        {
            if (serial is not null) return serial.IsOpen && master is not null;
            try { return client is { Connected: true } socket && !(socket.Client.Poll(0, SelectMode.SelectRead) && socket.Available == 0); }
            catch (ObjectDisposedException) { return false; }
            catch (SocketException) { return false; }
        }
    }

    public async Task ConnectRtuAsync(string portName, int baudRate, byte unit, Parity parity = Parity.None,
        int dataBits = 8, StopBits stopBits = StopBits.One)
    {
        await gate.WaitAsync();
        try
        {
            Close();
            serial = new SerialPort(portName, baudRate, parity, dataBits, stopBits)
            { ReadTimeout = TimeoutMs, WriteTimeout = TimeoutMs };
            try
            {
                serial.Open();
                master = new ModbusFactory().CreateRtuMaster(new SerialPortAdapter(serial));
                master.Transport.ReadTimeout = TimeoutMs;
                master.Transport.WriteTimeout = TimeoutMs;
                master.Transport.Retries = 0;
                this.unit = unit;
            }
            catch { Close(); throw; }
        }
        finally { gate.Release(); }
    }

    public async Task ConnectAsync(string host, int port, byte unit)
    {
        await gate.WaitAsync();
        try
        {
            Close();
            var socket = new TcpClient();
            try
            {
                using var timeout = new CancellationTokenSource(TimeoutMs);
                await socket.ConnectAsync(host, port, timeout.Token);
                client = socket;
                master = new ModbusFactory().CreateMaster(socket);
                master.Transport.ReadTimeout = TimeoutMs;
                master.Transport.WriteTimeout = TimeoutMs;
                master.Transport.Retries = 0;
                this.unit = unit;
            }
            catch { socket.Dispose(); Close(); throw; }
        }
        finally { gate.Release(); }
    }

    public Task<ushort[]> ReadRegistersAsync(ushort address, ushort count, CancellationToken token = default)
        => ExecuteAsync(m => m.ReadHoldingRegistersAsync(unit, address, count), token);

    public async Task WriteRegistersAsync(ushort address, ushort[] values, CancellationToken token = default)
        => await ExecuteAsync(async m => { await m.WriteMultipleRegistersAsync(unit, address, values); return true; }, token);

    private async Task<T> ExecuteAsync<T>(Func<IModbusMaster, Task<T>> operation, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (master is null || !IsConnected) { Close(); throw new IOException("Modbus 尚未连接或连接已断开。"); }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeoutMs);
            // NModbus 没有 CancellationToken 接口；取消时关闭底层连接，终止实际 I/O。
            var activeClient = client;
            var activeSerial = serial;
            using var registration = timeout.Token.Register(() =>
            {
                try { activeClient?.Dispose(); activeSerial?.Close(); }
                catch (IOException) { }
                catch (InvalidOperationException) { }
            });
            try { return await operation(master).WaitAsync(timeout.Token); }
            catch { Close(); throw; }
        }
        finally { gate.Release(); }
    }

    public async Task DisconnectAsync()
    {
        client?.Dispose();
        serial?.Close();
        await gate.WaitAsync();
        try { Close(); }
        finally { gate.Release(); }
    }

    private void Close()
    {
        master?.Dispose();
        master = null;
        client?.Dispose();
        client = null;
        serial?.Dispose();
        serial = null;
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
