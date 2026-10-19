using System.Runtime.InteropServices;

namespace VisionWorkbench.Vision;

public static class HalconRuntime
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string path);

    public static void Configure()
    {
        string? root = Environment.GetEnvironmentVariable("HALCONROOT");
        if (!string.IsNullOrWhiteSpace(root))
            SetDllDirectory(Path.Combine(root, "bin", "x64-win64"));
    }
}
