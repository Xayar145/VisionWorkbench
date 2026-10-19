using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisionWorkbench.AI;

public class AiSettings
{
    public string Endpoint { get; set; } = "https://api.openai.com/v1";
    public string Model { get; set; } = "gpt-4.1-mini";
    [JsonIgnore] public string ApiKey { get; set; } = "";
    public string ProtectedApiKey { get; set; } = "";
}

public static class SettingsStore
{
    public static string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VisionWorkbench");
    public static string FilePath => Path.Combine(DirectoryPath, "ai-settings.json");

    public static AiSettings Load()
    {
        if (!File.Exists(FilePath)) return new();
        var settings = JsonSerializer.Deserialize<AiSettings>(File.ReadAllText(FilePath)) ?? new();
        if (settings.ProtectedApiKey.Length > 0)
            settings.ApiKey = Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(settings.ProtectedApiKey), null, DataProtectionScope.CurrentUser));
        return settings;
    }

    public static void Save(AiSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        settings.ProtectedApiKey = settings.ApiKey.Length == 0 ? "" : Convert.ToBase64String(
            ProtectedData.Protect(Encoding.UTF8.GetBytes(settings.ApiKey), null, DataProtectionScope.CurrentUser));
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }
}
