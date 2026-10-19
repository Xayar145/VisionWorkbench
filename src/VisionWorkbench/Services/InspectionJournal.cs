using System.Text;
using System.Text.Json;
using OpenCvSharp;
using VisionWorkbench.Models;

namespace VisionWorkbench.Services;

public sealed class TraceSettings
{
    public bool Enabled { get; set; } = true;
    public bool SaveNgImages { get; set; } = true;
    public bool SaveAllImages { get; set; }
    public string Directory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VisionWorkbench", "Records");
}

public record InspectionRecord(DateTime Timestamp, string Source, string RequestId, RunReport Report, string? ImagePath);

public sealed class InspectionJournal(TraceSettings settings)
{
    private readonly object sync = new();
    private string FilePath => Path.Combine(settings.Directory, "inspections.jsonl");

    public InspectionRecord? Append(string source, string requestId, RunReport report, PreviewFrame frame)
    {
        if (!settings.Enabled) return null;
        lock (sync)
        {
            Directory.CreateDirectory(settings.Directory);
            string? imagePath = null;
            if (settings.SaveAllImages || settings.SaveNgImages && !report.Accepted)
            {
                string folder = Path.Combine(settings.Directory, report.StartedAt.ToString("yyyy-MM-dd"));
                Directory.CreateDirectory(folder);
                imagePath = Path.Combine(folder, report.RunId + ".png");
                using var mat = new Mat(frame.Height, frame.Width, MatType.CV_8UC1);
                System.Runtime.InteropServices.Marshal.Copy(frame.Pixels, 0, mat.Data, frame.Pixels.Length);
                if (!Cv2.ImWrite(imagePath, mat)) throw new IOException("检测图像保存失败");
            }
            var record = new InspectionRecord(report.StartedAt, source, requestId, report, imagePath);
            File.AppendAllText(FilePath, JsonSerializer.Serialize(record) + Environment.NewLine, new UTF8Encoding(false));
            return record;
        }
    }

    public IReadOnlyList<InspectionRecord> ReadRecent(int count = 200)
    {
        lock (sync)
        {
            if (!File.Exists(FilePath)) return [];
            var records = new Queue<InspectionRecord>();
            foreach (string line in File.ReadLines(FilePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                // 断电留下的不完整末行不应阻止读取已完成记录。
                try
                {
                    var record = JsonSerializer.Deserialize<InspectionRecord>(line);
                    if (record != null) records.Enqueue(record);
                    if (records.Count > count) records.Dequeue();
                }
                catch (JsonException) { }
            }
            return records.Reverse().ToArray();
        }
    }

    public void ExportCsv(string path)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine("时间,来源,请求编号,运行编号,执行成功,判定,耗时ms,图像");
        foreach (var row in ReadRecent(int.MaxValue).Reverse())
            writer.WriteLine(string.Join(",", Quote(row.Timestamp.ToString("O")), Quote(row.Source), Quote(row.RequestId), Quote(row.Report.RunId),
                row.Report.Success, row.Report.Accepted ? "OK" : "NG", row.Report.ElapsedMs.ToString("F2", System.Globalization.CultureInfo.InvariantCulture), Quote(row.ImagePath ?? "")));
    }
    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}
