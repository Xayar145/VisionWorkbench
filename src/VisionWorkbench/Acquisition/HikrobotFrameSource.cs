using System.Reflection;
using System.Runtime.InteropServices;
using OpenCvSharp;
using VisionWorkbench.Models;

namespace VisionWorkbench.Acquisition;

// 对接海康 MVS 的 MvCamCtrl.NET.MyCamera API。厂商程序集和驱动由用户安装，不随仓库分发。
internal sealed class HikrobotFrameSource : IFrameSource
{
    private HikrobotSdk? sdk;
    private object? camera;
    private IntPtr buffer;
    private uint bufferSize;
    private bool created, opened, grabbing;

    public void Open(CameraSettings settings)
    {
        sdk = HikrobotSdk.Load(settings.SdkPath);
        var devices = sdk.Enumerate();
        var selected = string.IsNullOrWhiteSpace(settings.Device) ? devices.FirstOrDefault()
            : devices.FirstOrDefault(d => d.Id == settings.Device);
        if (selected is null) throw new IOException("未找到指定海康相机。请先枚举设备，填写返回的序列号或序号。");
        camera = Activator.CreateInstance(sdk.CameraType)!;
        object?[] create = [selected.Info];
        sdk.Call(camera, "MV_CC_CreateDevice_NET", create);
        created = true;
        sdk.Call(camera, "MV_CC_OpenDevice_NET", [1u, (ushort)0]);
        opened = true;
        // 单拍和预览均逐帧软触发，避免停止预览后读到连续流缓冲中的旧帧。
        sdk.Call(camera, "MV_CC_SetEnumValue_NET", ["TriggerMode", 1u]);
        sdk.Call(camera, "MV_CC_SetEnumValue_NET", ["TriggerSource", 7u]);
        ApplyParameters(settings.Exposure, settings.Gain);
        uint width = GetInteger("Width"), height = GetInteger("Height");
        bufferSize = checked(width * height * 3);
        buffer = Marshal.AllocHGlobal(checked((int)bufferSize));
        sdk.Call(camera, "MV_CC_StartGrabbing_NET", []);
        grabbing = true;
    }

    private uint GetInteger(string name)
    {
        object?[] args = [name, sdk!.NewStruct("MVCC_INTVALUE")];
        sdk.Call(camera!, "MV_CC_GetIntValue_NET", args);
        return Convert.ToUInt32(HikrobotSdk.Field(args[1]!, "nCurValue"));
    }

    public PreviewFrame Grab(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        sdk!.Call(camera!, "MV_CC_SetCommandValue_NET", ["TriggerSoftware"]);
        object?[] args = [buffer, bufferSize, sdk!.NewStruct("MV_FRAME_OUT_INFO_EX"), 1500];
        // SDK 转换为 BGR，避免将 Bayer、彩色或高位深原始数据误当灰度。
        sdk.Call(camera!, "MV_CC_GetImageForBGR_NET", args);
        token.ThrowIfCancellationRequested();
        int width = Convert.ToInt32(HikrobotSdk.Field(args[2]!, "nWidth"));
        int height = Convert.ToInt32(HikrobotSdk.Field(args[2]!, "nHeight"));
        if ((long)width * height * 3 > bufferSize) throw new IOException("海康图像尺寸变化，请重新连接相机。");
        using var image = Mat.FromPixelData(height, width, MatType.CV_8UC3, buffer);
        return FrameSources.ToFrame(image);
    }

    public void ApplyParameters(double? exposure, double? gain)
    {
        if (exposure.HasValue)
        {
            sdk!.Call(camera!, "MV_CC_SetEnumValue_NET", ["ExposureAuto", 0u]);
            sdk.Call(camera!, "MV_CC_SetFloatValue_NET", ["ExposureTime", (float)exposure.Value]);
        }
        if (gain.HasValue)
        {
            sdk!.Call(camera!, "MV_CC_SetEnumValue_NET", ["GainAuto", 0u]);
            sdk.Call(camera!, "MV_CC_SetFloatValue_NET", ["Gain", (float)gain.Value]);
        }
    }

    public void Dispose()
    {
        // 关闭时逐项尝试，确保断线后的句柄和本地缓存仍被释放。
        List<Exception> errors = [];
        void Release(string name)
        {
            try { sdk!.Call(camera!, name, []); }
            catch (Exception ex) { errors.Add(ex); }
        }
        if (grabbing) Release("MV_CC_StopGrabbing_NET");
        if (opened) Release("MV_CC_CloseDevice_NET");
        if (created) Release("MV_CC_DestroyDevice_NET");
        grabbing = opened = created = false;
        camera = null;
        if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        buffer = IntPtr.Zero;
        if (errors.Count > 0) throw new AggregateException("海康设备已释放，部分关闭操作返回错误。", errors);
    }
}

internal record HikrobotDevice(string Id, string Name, object Info);

internal sealed class HikrobotSdk
{
    public Type CameraType { get; }
    private HikrobotSdk(Assembly assembly) => CameraType = assembly.GetType("MvCamCtrl.NET.MyCamera")
        ?? throw new NotSupportedException("程序集不包含 MvCamCtrl.NET.MyCamera，请选择 MVS 旧版兼容 .NET SDK MvCameraControl.Net.dll。");

    public static HikrobotSdk Load(string configuredPath)
    {
        string? root = Environment.GetEnvironmentVariable("MVCAM_COMMON_RUNENV");
        string[] candidates = [configuredPath, Path.Combine(AppContext.BaseDirectory, "MvCameraControl.Net.dll"),
            Path.Combine(root ?? "", "DotNet", "win64", "MvCameraControl.Net.dll"),
            @"C:\Program Files (x86)\MVS\Development\DotNet\win64\MvCameraControl.Net.dll",
            @"C:\Program Files\MVS\Development\DotNet\win64\MvCameraControl.Net.dll"];
        string? path = candidates.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));
        if (path is null) throw new FileNotFoundException("未安装海康 MVS .NET SDK。请安装 x64 MVS 运行库，并选择 MvCameraControl.Net.dll 路径。不会自动切换为模拟相机。");
        return new(Assembly.LoadFrom(Path.GetFullPath(path)));
    }

    public object NewStruct(string name) => Activator.CreateInstance(CameraType.GetNestedType(name)
        ?? throw new MissingMemberException(CameraType.FullName, name))!;
    public static object Field(object value, string name) => value.GetType().GetField(name)!.GetValue(value)!;

    public void Call(object? target, string name, object?[] args)
    {
        var method = CameraType.GetMethods().FirstOrDefault(m => m.Name == name && m.GetParameters().Length == args.Length)
            ?? throw new MissingMethodException(CameraType.FullName, name);
        int result;
        try { result = Convert.ToInt32(method.Invoke(target, args)); }
        catch (TargetInvocationException ex) { throw new IOException("海康 SDK 调用失败：" + name, ex.InnerException ?? ex); }
        if (result != 0) throw new IOException($"海康 SDK {name} 返回 0x{unchecked((uint)result):X8}。");
    }

    public IReadOnlyList<HikrobotDevice> Enumerate()
    {
        object?[] args = [5u, NewStruct("MV_CC_DEVICE_INFO_LIST")];
        Call(null, "MV_CC_EnumDevices_NET", args);
        object list = args[1]!;
        int count = Convert.ToInt32(Field(list, "nDeviceNum"));
        var pointers = (IntPtr[])Field(list, "pDeviceInfo");
        var infoType = CameraType.GetNestedType("MV_CC_DEVICE_INFO")!;
        var result = new List<HikrobotDevice>();
        for (int index = 0; index < count; index++)
        {
            object info = Marshal.PtrToStructure(pointers[index], infoType)!;
            uint layer = Convert.ToUInt32(Field(info, "nTLayerType"));
            object special = Field(info, "SpecialInfo");
            string serial = "", model = "";
            string field = layer == 1 ? "stGigEInfo" : "stUsb3VInfo";
            var details = special.GetType().GetField(field)?.GetValue(special);
            if (details is byte[] bytes)
            {
                var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                try { details = Marshal.PtrToStructure(pinned.AddrOfPinnedObject(), CameraType.GetNestedType(layer == 1 ? "MV_GIGE_DEVICE_INFO" : "MV_USB3_DEVICE_INFO")!); }
                finally { pinned.Free(); }
            }
            if (details is not null)
            {
                serial = Text(details, "chSerialNumber");
                model = Text(details, "chModelName");
            }
            result.Add(new(serial.Length > 0 ? serial : index.ToString(), $"{(layer == 1 ? "GigE" : "USB3")} {model} {serial}".Trim(), info));
        }
        return result;
    }

    private static string Text(object details, string field)
    {
        object? value = details.GetType().GetField(field)?.GetValue(details);
        return value switch { string text => text.TrimEnd('\0'), byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes).TrimEnd('\0'), _ => "" };
    }
}
