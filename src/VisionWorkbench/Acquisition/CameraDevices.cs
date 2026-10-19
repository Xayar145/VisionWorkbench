using HalconDotNet;
using VisionWorkbench.Vision;

namespace VisionWorkbench.Acquisition;

public record CameraDevice(string Id, string Name);

public static class CameraDevices
{
    public static Task<IReadOnlyList<CameraDevice>> EnumerateAsync(CameraSettings settings) => Task.Run<IReadOnlyList<CameraDevice>>(() =>
    {
        if (settings.Kind == "simulated") return [new("", "内置模拟相机（离线）")];
        if (settings.Kind == "hikrobot") return HikrobotSdk.Load(settings.SdkPath).Enumerate().Select(d => new CameraDevice(d.Id, d.Name)).ToArray();
        if (settings.Kind == "halcon")
        {
            HalconRuntime.Configure();
            HOperatorSet.InfoFramegrabber(settings.InterfaceName, "device", out HTuple information, out HTuple values);
            using (information) using (values)
                return values.SArr.Select(id => new CameraDevice(id, id)).ToArray();
        }
        if (settings.Kind == "usb")
            throw new NotSupportedException("OpenCV 不提供 DirectShow 设备枚举；请在设备栏填写 Windows 相机序号，例如 0。");
        return [];
    });
}
