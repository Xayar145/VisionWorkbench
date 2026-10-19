using VisionWorkbench.Models;

namespace VisionWorkbench.Communication;

public sealed class CommunicationSettings
{
    public bool TcpEnabled { get; set; }
    public string ListenAddress { get; set; } = "127.0.0.1";
    public int TcpPort { get; set; } = 8500;
    public string SerialPortName { get; set; } = "COM1";
    public int SerialBaudRate { get; set; } = 9600;
    public string SerialParity { get; set; } = "None";
    public int SerialDataBits { get; set; } = 8;
    public string SerialStopBits { get; set; } = "One";
    public string ModbusTransport { get; set; } = "tcp";
    public string ModbusSerialPortName { get; set; } = "COM1";
    public int ModbusBaudRate { get; set; } = 9600;
    public string ModbusParity { get; set; } = "None";
    public int ModbusDataBits { get; set; } = 8;
    public string ModbusStopBits { get; set; } = "One";
    public string ModbusHost { get; set; } = "127.0.0.1";
    public int ModbusPort { get; set; } = 502;
    public byte Unit { get; set; } = 1;
    public ushort TriggerAddress { get; set; }
    public ushort ResultAddress { get; set; } = 10;
    public int PollIntervalMs { get; set; } = 100;
}

public record DeviceRequest(string Command, string RequestId = "");
public record DeviceReply(string RequestId, bool Success, string State, RunReport? Report = null, string? Error = null);
