using VisionWorkbench.Models;

namespace VisionWorkbench.Acquisition;

public sealed class CameraSettings
{
    public string Kind { get; set; } = "simulated";
    public string Device { get; set; } = "";
    public string InterfaceName { get; set; } = "GigEVision2";
    public string SdkPath { get; set; } = "";
    public double? Exposure { get; set; }
    public double? Gain { get; set; }
    public int FrameIntervalMs { get; set; } = 100;

    public CameraSettings Copy() => (CameraSettings)MemberwiseClone();
}

public interface IFrameSource : IDisposable
{
    void Open(CameraSettings settings);
    PreviewFrame Grab(CancellationToken token);
    void ApplyParameters(double? exposure, double? gain);
}
