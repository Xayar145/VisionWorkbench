using System.Runtime.InteropServices;
using HalconDotNet;
using OpenCvSharp;
using VisionWorkbench.Models;
using VisionWorkbench.Vision;

namespace VisionWorkbench.Acquisition;

public static class FrameSources
{
    private static readonly Dictionary<string, Func<IFrameSource>> extensions = new(StringComparer.OrdinalIgnoreCase);
    // 品牌扩展只需实现 IFrameSource 并注册，不改采集会话和界面。
    public static void Register(string kind, Func<IFrameSource> factory)
    {
        lock (extensions) extensions[kind] = factory;
    }
    public static IFrameSource Create(string kind)
    {
        lock (extensions)
            if (extensions.TryGetValue(kind, out var factory)) return factory();
        return kind switch
    {
        "simulated" => new SimulatedFrameSource(),
        "hikrobot" => new HikrobotFrameSource(),
        "halcon" => new HalconFrameSource(),
        "usb" => new OpenCvFrameSource(),
        "replay" => new ReplayFrameSource(),
        _ => throw new ArgumentException("未知采集类型：" + kind)
    };
    }

    internal static PreviewFrame ToFrame(Mat image)
    {
        using var gray = new Mat();
        if (image.Channels() == 1) image.CopyTo(gray);
        else Cv2.CvtColor(image, gray, image.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
        using var bytes = new Mat();
        gray.ConvertTo(bytes, MatType.CV_8UC1);
        byte[] pixels = new byte[bytes.Width * bytes.Height];
        Marshal.Copy(bytes.Data, pixels, 0, pixels.Length);
        return new(pixels, bytes.Width, bytes.Height, []);
    }
}

// 合成图仅用于离线调试，不代表相机实采。
internal sealed class SimulatedFrameSource : IFrameSource
{
    private long index;
    private double brightness = 1;
    public void Open(CameraSettings settings) { index = 0; ApplyParameters(settings.Exposure, settings.Gain); }
    public PreviewFrame Grab(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var image = new Mat(400, 640, MatType.CV_8UC1, Scalar.All(22));
        int offset = (int)(Math.Sin(index++ * .16) * 22);
        Cv2.Rectangle(image, new Rect(55 + offset, 115, 110, 150), Scalar.All(210 * brightness), -1);
        Cv2.Circle(image, new Point(315 + offset, 190), 62, Scalar.All(225 * brightness), -1);
        Cv2.Rectangle(image, new Rect(465 + offset, 120, 100, 140), Scalar.All(200 * brightness), -1);
        return FrameSources.ToFrame(image);
    }
    public void ApplyParameters(double? exposure, double? gain) => brightness = Math.Clamp((exposure ?? 10000) / 10000 * (1 + (gain ?? 0) / 20), .1, 1.1);
    public void Dispose() { }
}

internal sealed class OpenCvFrameSource : IFrameSource
{
    private VideoCapture? camera;
    public void Open(CameraSettings settings)
    {
        if (!int.TryParse(settings.Device.Length == 0 ? "0" : settings.Device, out int index) || index < 0)
            throw new ArgumentException("USB 设备填写非负相机序号，例如 0。");
        camera = new VideoCapture(index, VideoCaptureAPIs.DSHOW);
        if (!camera.IsOpened()) throw new IOException("无法打开 USB 相机：" + index);
        ApplyParameters(settings.Exposure, settings.Gain);
    }
    public PreviewFrame Grab(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var image = new Mat();
        if (!camera!.Read(image) || image.Empty()) throw new IOException("USB 相机未返回图像。");
        token.ThrowIfCancellationRequested();
        return FrameSources.ToFrame(image);
    }
    public void ApplyParameters(double? exposure, double? gain)
    {
        // DirectShow 曝光值单位由驱动定义，常见为 log2 秒；不伪装成微秒。
        if (exposure.HasValue && !camera!.Set(VideoCaptureProperties.Exposure, exposure.Value)) throw new IOException("相机驱动不支持设置曝光。");
        if (gain.HasValue && !camera!.Set(VideoCaptureProperties.Gain, gain.Value)) throw new IOException("相机驱动不支持设置增益。");
    }
    public void Dispose() { camera?.Dispose(); camera = null; }
}

internal sealed class ReplayFrameSource : IFrameSource
{
    private string[] images = [];
    private int index;
    private VideoCapture? video;
    public void Open(CameraSettings settings)
    {
        string path = Path.GetFullPath(settings.Device);
        if (Directory.Exists(path))
        {
            string[] extensions = [".png", ".bmp", ".jpg", ".jpeg", ".tif", ".tiff"];
            images = Directory.GetFiles(path).Where(p => extensions.Contains(Path.GetExtension(p).ToLowerInvariant())).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            if (images.Length == 0) throw new IOException("回放目录没有图像。");
        }
        else if (File.Exists(path) && new[] { ".png", ".bmp", ".jpg", ".jpeg", ".tif", ".tiff" }.Contains(Path.GetExtension(path).ToLowerInvariant())) images = [path];
        else
        {
            if (!File.Exists(path)) throw new FileNotFoundException("找不到回放文件或目录。", path);
            video = new VideoCapture(path);
            if (!video.IsOpened()) throw new IOException("无法打开视频文件。");
        }
        index = 0;
        ApplyParameters(settings.Exposure, settings.Gain);
    }
    public PreviewFrame Grab(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (images.Length > 0)
        {
            using var image = Cv2.ImRead(images[index++ % images.Length], ImreadModes.Grayscale);
            if (image.Empty()) throw new IOException("回放图像解码失败。");
            return FrameSources.ToFrame(image);
        }
        using var frame = new Mat();
        if (!video!.Read(frame) || frame.Empty())
        {
            video.Set(VideoCaptureProperties.PosFrames, 0);
            if (!video.Read(frame) || frame.Empty()) throw new IOException("视频回放无有效帧。");
        }
        return FrameSources.ToFrame(frame);
    }
    public void ApplyParameters(double? exposure, double? gain)
    {
        if (exposure.HasValue || gain.HasValue) throw new InvalidOperationException("文件回放不支持曝光和增益。");
    }
    public void Dispose() { video?.Dispose(); video = null; images = []; }
}

internal sealed class HalconFrameSource : IFrameSource
{
    private HTuple? handle;
    private string? sequencePath;
    public void Open(CameraSettings settings)
    {
        HalconRuntime.Configure();
        bool file = settings.InterfaceName == "File";
        string cameraType = "default";
        if (file)
        {
            cameraType = settings.Device;
            if (Directory.Exists(cameraType))
            {
                // File 接口在部分版本无法枚举中文目录；UTF-8 序列同时保证稳定顺序。
                string[] extensions = [".png", ".bmp", ".jpg", ".jpeg", ".tif", ".tiff", ".pgm", ".ppm"];
                var files = Directory.GetFiles(cameraType).Where(p => extensions.Contains(Path.GetExtension(p).ToLowerInvariant()))
                    .Order(StringComparer.OrdinalIgnoreCase).Select(Path.GetFullPath).ToArray();
                if (files.Length == 0) throw new IOException("HALCON File 目录没有支持的图像。");
                string temporary = Path.Combine(Path.GetTempPath(), "VisionWorkbench", "acquisition");
                Directory.CreateDirectory(temporary);
                sequencePath = Path.Combine(temporary, Guid.NewGuid().ToString("N") + ".seq");
                File.WriteAllLines(sequencePath, files, new System.Text.UTF8Encoding(false));
                cameraType = sequencePath;
            }
        }
        // File 接口的路径属于 CameraType；物理设备标识属于 Device。
        HOperatorSet.OpenFramegrabber(settings.InterfaceName, 1, 1, 0, 0, 0, 0, "default", -1,
            "default", -1, "false", cameraType, file ? "" : (string.IsNullOrWhiteSpace(settings.Device) ? "default" : settings.Device), -1, -1, out handle);
        if (!file) HOperatorSet.SetFramegrabberParam(handle, "grab_timeout", 1500);
        ApplyParameters(settings.Exposure, settings.Gain);
    }
    public PreviewFrame Grab(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        HOperatorSet.GrabImage(out HObject image, handle!);
        using (image)
        {
            token.ThrowIfCancellationRequested();
            HOperatorSet.CountChannels(image, out HTuple channels);
            using (channels)
            {
                HObject gray;
                if (channels.I >= 3) HOperatorSet.Rgb1ToGray(image, out gray);
                else HOperatorSet.CopyImage(image, out gray);
                using (gray)
                {
                    HOperatorSet.GetImageType(gray, out HTuple type);
                    using (type)
                    {
                        if (type.S == "byte") return ImageBridge.ToFrame(gray);
                        HOperatorSet.ScaleImageMax(gray, out HObject scaled);
                        using (scaled)
                        {
                            HOperatorSet.ConvertImageType(scaled, out HObject bytes, "byte");
                            using (bytes) return ImageBridge.ToFrame(bytes);
                        }
                    }
                }
            }
        }
    }
    public void ApplyParameters(double? exposure, double? gain)
    {
        if (exposure.HasValue) HOperatorSet.SetFramegrabberParam(handle!, "ExposureTime", exposure.Value);
        if (gain.HasValue) HOperatorSet.SetFramegrabberParam(handle!, "Gain", gain.Value);
    }
    public void Dispose()
    {
        try
        {
            if (handle is not null)
                try { HOperatorSet.CloseFramegrabber(handle); }
                finally { handle.Dispose(); handle = null; }
        }
        finally
        {
            if (sequencePath is not null && File.Exists(sequencePath)) File.Delete(sequencePath);
            sequencePath = null;
        }
    }
}
