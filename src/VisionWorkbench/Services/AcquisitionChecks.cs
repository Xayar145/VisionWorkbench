using OpenCvSharp;
using VisionWorkbench.Acquisition;

namespace VisionWorkbench.Services;

public static partial class SmokeChecks
{
    private static async Task CheckAcquisition(string folder, List<object> checks)
    {
        await using var camera = new CameraSession();
        int received = 0;
        camera.FrameReceived += _ => Interlocked.Increment(ref received);
        await camera.ConnectAsync(new() { FrameIntervalMs = 15 });
        var first = await camera.SnapAsync();
        var second = await camera.SnapAsync();
        Require(first.Width == 640 && first.Height == 400 && first.Pixels.Length == 640 * 400, "模拟采集必须输出独立 8 位灰度帧。");
        Require(!first.Pixels.SequenceEqual(second.Pixels), "模拟图像必须随帧变化。");
        await camera.ApplyParametersAsync(7000, 1);
        Require(camera.Settings.Exposure == 7000 && camera.Settings.Gain == 1, "相机参数必须更新。");
        checks.Add(new { name = "模拟相机连接、单帧、动态场景与参数", passed = true });

        await camera.StartPreviewAsync();
        await camera.StartPreviewAsync();
        bool snapBlocked = false;
        try { await camera.SnapAsync(); }
        catch (InvalidOperationException) { snapBlocked = true; }
        await WaitFrames(camera, 6);
        await camera.StopPreviewAsync();
        long stoppedCount = camera.FrameCount;
        await Task.Delay(60);
        Require(camera.FrameCount == stoppedCount && !camera.IsPreviewing && snapBlocked, "停止后不得继续出帧，预览期间单拍须拒绝竞争。");
        await camera.StartPreviewAsync();
        await WaitFrames(camera, stoppedCount + 2);
        await camera.DisconnectAsync();
        Require(!camera.IsConnected && !camera.IsPreviewing && received >= 8, "断开必须停止预览并释放采集源。");
        checks.Add(new { name = "连续采集、重复启动、停止重启、断开与竞争保护", passed = true });

        string replay = Path.Combine(folder, "采集回放");
        Directory.CreateDirectory(replay);
        using (var image = new Mat(48, 64, MatType.CV_8UC1, Scalar.All(36))) Cv2.ImWrite(Path.Combine(replay, "01.png"), image);
        using (var image = new Mat(48, 64, MatType.CV_8UC1, Scalar.All(184))) Cv2.ImWrite(Path.Combine(replay, "02.png"), image);
        await camera.ConnectAsync(new() { Kind = "replay", Device = replay, FrameIntervalMs = 15 });
        var replayFirst = await camera.SnapAsync();
        var replaySecond = await camera.SnapAsync();
        var replayLoop = await camera.SnapAsync();
        Require(replayFirst.Pixels.All(p => p == 36) && replaySecond.Pixels.All(p => p == 184) && replayLoop.Pixels.SequenceEqual(replayFirst.Pixels), "目录回放必须按名称顺序循环。");
        await camera.StartPreviewAsync();
        await WaitFrames(camera, 6);
        await camera.DisconnectAsync();
        checks.Add(new { name = "文件序列回放、循环与实时预览", passed = true });

        string video = Path.Combine(folder, "采集回放.avi");
        using (var writer = new VideoWriter(video, FourCC.MJPG, 10, new Size(64, 48)))
        {
            Require(writer.IsOpened(), "测试视频编码器不可用。");
            using var dark = new Mat(48, 64, MatType.CV_8UC3, Scalar.All(35));
            using var light = new Mat(48, 64, MatType.CV_8UC3, Scalar.All(185));
            writer.Write(dark);
            writer.Write(light);
        }
        await camera.ConnectAsync(new() { Kind = "replay", Device = video });
        var videoFirst = await camera.SnapAsync();
        var videoSecond = await camera.SnapAsync();
        var videoLoop = await camera.SnapAsync();
        Require(videoFirst.Pixels.Average(p => (double)p) < 50 && videoSecond.Pixels.Average(p => (double)p) > 170
            && videoLoop.Pixels.SequenceEqual(videoFirst.Pixels), "视频回放必须读取不同帧并循环。");
        checks.Add(new { name = "实际 AVI 视频解码与循环", passed = true });

        await camera.ConnectAsync(new() { Kind = "halcon", InterfaceName = "File", Device = replay });
        var halconFirst = await camera.SnapAsync();
        var halconSecond = await camera.SnapAsync();
        Require(halconFirst.Width == 64 && halconFirst.Pixels.All(p => p == 36) && halconSecond.Pixels.All(p => p == 184), "HALCON File 接口必须通过真实 Framegrabber 读取连续图像。");
        await camera.DisconnectAsync();
        checks.Add(new { name = "真实 HALCON File Framegrabber 连续采图", passed = true });

        bool missingRejected = false;
        try { await camera.ConnectAsync(new() { Kind = "replay", Device = Path.Combine(folder, "不存在的采集文件.avi") }); }
        catch (FileNotFoundException) { missingRejected = true; }
        Require(missingRejected && !camera.IsConnected, "无效设备配置必须报错且保持断开。");
        bool sdkMissingRejected = false;
        try { HikrobotSdk.Load(Path.Combine(folder, "不存在的SDK.dll")); }
        catch (FileNotFoundException) { sdkMissingRejected = true; }
        checks.Add(new { name = "无效采集源明确失败，海康 SDK 状态与实机边界", passed = true,
            sdkInstalled = !sdkMissingRejected, physicalCameraTested = false, note = "未连接 USB/GigE/海康实机，不计作硬件采集验证。" });
        await camera.ConnectAsync(new());
        Require((await camera.SnapAsync()).Pixels.Length > 0, "失败后应允许重新连接。");
        await camera.DisconnectAsync();
        checks.Add(new { name = "连接失败后恢复连接", passed = true });

        FrameSources.Register("check_failure", () => new InterruptedCheckSource());
        await camera.ConnectAsync(new() { Kind = "check_failure", FrameIntervalMs = 15 });
        var failure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFault(string text) { if (text.Contains("采集失败")) failure.TrySetResult(); }
        camera.Logged += OnFault;
        await camera.StartPreviewAsync();
        await failure.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await camera.StopPreviewAsync();
        camera.Logged -= OnFault;
        Require(!camera.IsConnected && !camera.IsPreviewing, "采集异常应自动释放故障源并退出预览。");
        await camera.ConnectAsync(new());
        Require((await camera.SnapAsync()).Width == 640, "异常断开后应允许重新连接。");
        checks.Add(new { name = "注入采集异常后自动断开、停止与重连", passed = true });
    }

    private static async Task WaitFrames(CameraSession camera, long target)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        while (camera.FrameCount < target)
        {
            if (!camera.IsConnected) throw new InvalidOperationException("连续采集意外断开。");
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class InterruptedCheckSource : IFrameSource
    {
        private bool first = true;
        public void Open(CameraSettings settings) { }
        public Models.PreviewFrame Grab(CancellationToken token)
        {
            if (!first) throw new IOException("检查用采集故障。");
            first = false;
            return new([0], 1, 1, []);
        }
        public void ApplyParameters(double? exposure, double? gain) { }
        public void Dispose() { }
    }
}
