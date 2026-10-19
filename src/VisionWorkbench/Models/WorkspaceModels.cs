using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace VisionWorkbench.Models;

public class Bindable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Changed(name);
        return true;
    }
}

public class ToolParameter : Bindable
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public double Minimum { get; set; }
    public double Maximum { get; set; } = 255;
    private double value;
    public double Value { get => value; set => Set(ref this.value, value); }
}

public class RoiSpec : Bindable
{
    private string shape = "rectangle";
    private bool enabled;
    private double x, y, width = 160, height = 120;
    public bool Enabled { get => enabled; set => Set(ref enabled, value); }
    public string Shape { get => shape; set => Set(ref shape, value); }
    public double X { get => x; set => Set(ref x, value); }
    public double Y { get => y; set => Set(ref y, value); }
    public double Width { get => width; set => Set(ref width, value); }
    public double Height { get => height; set => Set(ref height, value); }
}

public class VisionStep : Bindable
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Kind { get; set; } = "halcon.threshold";
    public string Name { get; set; } = "二值化";
    public string Backend { get; set; } = "HALCON";
    public ObservableCollection<ToolParameter> Parameters { get; set; } = [];
    private RoiSpec roi = new();
    public RoiSpec Roi { get => roi; set => Set(ref roi, value); }
    private string templatePath = "";
    public string TemplatePath { get => templatePath; set => Set(ref templatePath, value); }
    private bool enabled = true;
    public bool Enabled { get => enabled; set => Set(ref enabled, value); }
    private string status = "待运行";
    [JsonIgnore] public string Status { get => status; set => Set(ref status, value); }
    private double elapsedMs;
    [JsonIgnore] public double ElapsedMs { get => elapsedMs; set => Set(ref elapsedMs, value); }
    public double Get(string key) => Parameters.First(p => p.Key == key).Value;
}

public class ProjectDocument
{
    public int FormatVersion { get; set; } = 1;
    public string Name { get; set; } = "未命名方案";
    public string ImagePath { get; set; } = "";
    public ObservableCollection<VisionStep> Steps { get; set; } = [];
}

public record ToolDefinition(string Kind, string Name, string Backend, string Description);
public record OverlayBox(double X, double Y, double Width, double Height, string Label, string Shape = "rectangle");
public record MetricRow(string Name, string Value, string Unit = "");
public record StepResult(string StepId, string Name, bool Success, bool Accepted, double ElapsedMs,
    string Message, IReadOnlyList<MetricRow> Metrics);
public record RunReport(string RunId, DateTime StartedAt, bool Success, bool Accepted,
    double ElapsedMs, IReadOnlyList<StepResult> Steps);
public record PreviewFrame(byte[] Pixels, int Width, int Height, IReadOnlyList<OverlayBox> Overlays);
public record OperationResult(bool Success, string Message, object? Data = null);
