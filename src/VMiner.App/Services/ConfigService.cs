using System.Text.Json;
using System.IO;
using VMiner.Models;

namespace VMiner.Services;

public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public string ConfigPath { get; } = Path.Combine(AppContext.BaseDirectory, "config.json");

    public AppConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
                return new AppConfig();
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), JsonOptions)
                   ?? new AppConfig();
        }
        catch
        {
            return new AppConfig();
        }
    }

    public void Save(AppConfig config)
    {
        var temporary = ConfigPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(config, JsonOptions));
        File.Move(temporary, ConfigPath, true);
    }
}
