using VisionWorkbench.Models;

namespace VisionWorkbench.Acquisition;

// 生命周期操作与设备操作分别串行；采集事件来自后台线程，由界面负责调度显示。
public sealed class CameraSession : IAsyncDisposable
{
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly SemaphoreSlim device = new(1, 1);
    private IFrameSource? source;
    private CancellationTokenSource? previewStop;
    private Task? previewTask;
    private long frameCount;
    private volatile bool previewing;
    private bool disposed;
    public CameraSettings Settings { get; private set; } = new();
    public bool IsConnected => source is not null;
    public bool IsPreviewing => previewing;
    public long FrameCount => Interlocked.Read(ref frameCount);
    public event Action<PreviewFrame>? FrameReceived;
    public event Action<string>? Logged;
    public event Action? StateChanged;

    public async Task ConnectAsync(CameraSettings settings)
    {
        await lifecycle.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await StopCore();
            await device.WaitAsync();
            try
            {
                await Task.Run(() =>
                {
                    CloseSource();
                    var next = FrameSources.Create(settings.Kind);
                    try { next.Open(settings); }
                    catch { next.Dispose(); throw; }
                    source = next;
                    Settings = settings.Copy();
                    Interlocked.Exchange(ref frameCount, 0);
                });
                Logged?.Invoke("采集源已连接：" + settings.Kind);
            }
            finally { device.Release(); StateChanged?.Invoke(); }
        }
        finally { lifecycle.Release(); }
    }

    public async Task DisconnectAsync()
    {
        await lifecycle.WaitAsync();
        try
        {
            await StopCore();
            await device.WaitAsync();
            try { await Task.Run(CloseSource); Logged?.Invoke("采集源已断开。"); }
            finally { device.Release(); StateChanged?.Invoke(); }
        }
        finally { lifecycle.Release(); }
    }

    public async Task<PreviewFrame> SnapAsync(CancellationToken token = default)
    {
        await lifecycle.WaitAsync(token);
        try
        {
            if (previewing) throw new InvalidOperationException("请先停止实时预览，再执行单次采集。");
            var frame = await GrabCore(token);
            FrameReceived?.Invoke(frame);
            return frame;
        }
        finally { lifecycle.Release(); }
    }

    public async Task StartPreviewAsync()
    {
        await lifecycle.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (previewing) return;
            if (!IsConnected) throw new InvalidOperationException("请先连接采集源。");
            if (Settings.FrameIntervalMs < 10) throw new ArgumentException("预览间隔不能小于 10 毫秒。");
            if (previewTask is not null) await previewTask;
            previewStop?.Dispose();
            previewStop = new();
            previewing = true;
            var token = previewStop.Token;
            previewTask = Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        var frame = await GrabCore(token);
                        FrameReceived?.Invoke(frame);
                        await Task.Delay(Settings.FrameIntervalMs, token);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception ex) { Logged?.Invoke("实时采集停止：" + ex.Message); }
                finally { previewing = false; StateChanged?.Invoke(); }
            });
            StateChanged?.Invoke();
        }
        finally { lifecycle.Release(); }
    }

    public async Task StopPreviewAsync()
    {
        await lifecycle.WaitAsync();
        try { await StopCore(); }
        finally { lifecycle.Release(); }
    }

    public async Task ApplyParametersAsync(double? exposure, double? gain)
    {
        await device.WaitAsync();
        try
        {
            var current = source ?? throw new InvalidOperationException("请先连接采集源。");
            await Task.Run(() => current.ApplyParameters(exposure, gain));
            Settings.Exposure = exposure;
            Settings.Gain = gain;
            Logged?.Invoke("曝光与增益已应用。");
        }
        finally { device.Release(); }
    }

    private async Task<PreviewFrame> GrabCore(CancellationToken token)
    {
        await device.WaitAsync(token);
        try
        {
            var current = source ?? throw new InvalidOperationException("请先连接采集源。");
            try
            {
                var frame = await Task.Run(() => current.Grab(token), token);
                Interlocked.Increment(ref frameCount);
                return frame;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                await Task.Run(CloseSource);
                Logged?.Invoke("采集失败，设备已释放：" + ex.Message);
                StateChanged?.Invoke();
                throw;
            }
        }
        finally { device.Release(); }
    }

    private async Task StopCore()
    {
        previewStop?.Cancel();
        if (previewTask is not null) await previewTask;
        previewTask = null;
        previewStop?.Dispose();
        previewStop = null;
        previewing = false;
        StateChanged?.Invoke();
    }
    private void CloseSource()
    {
        var old = source;
        source = null;
        old?.Dispose();
    }
    public async ValueTask DisposeAsync()
    {
        await lifecycle.WaitAsync();
        try
        {
            if (disposed) return;
            disposed = true;
            await StopCore();
            await device.WaitAsync();
            try { await Task.Run(CloseSource); }
            finally { device.Release(); }
        }
        finally { lifecycle.Release(); }
    }
}
