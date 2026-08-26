using System.Text.Json;

namespace PowerQuota.Core.Storage;

public class ConfigStorage
{
    private static readonly string StorageDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PowerQuota"
    );
    private static readonly string ConfigFile = Path.Combine(StorageDir, "config.json");

    private readonly object _lock = new();
    private PowerQuotaConfig _current = new();

    public ConfigStorage()
    {
        _current = Load();
    }

    public PowerQuotaConfig Current
    {
        get
        {
            lock (_lock) return _current;
        }
    }

    public void Save(PowerQuotaConfig config)
    {
        lock (_lock)
        {
            _current = config;
            try
            {
                Directory.CreateDirectory(StorageDir);
                var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigFile, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PowerQuota Config] Save error: {ex.Message}");
            }
        }
    }

    private PowerQuotaConfig Load()
    {
        if (!File.Exists(ConfigFile)) return new PowerQuotaConfig();
        try
        {
            var json = File.ReadAllText(ConfigFile);
            return JsonSerializer.Deserialize<PowerQuotaConfig>(json) ?? new PowerQuotaConfig();
        }
        catch
        {
            return new PowerQuotaConfig();
        }
    }
}

