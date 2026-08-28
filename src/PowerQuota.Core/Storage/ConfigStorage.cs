using System.Text.Json;

namespace PowerQuota.Core.Storage;

public class ConfigStorage
{
    public static readonly string DefaultStorageDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PowerQuota"
    );

    public string StorageDirectory { get; }
    public string ConfigFilePath { get; }

    private readonly object _lock = new();
    private PowerQuotaConfig _current = new();

    public ConfigStorage(string? customDirectoryPath = null)
    {
        StorageDirectory = !string.IsNullOrWhiteSpace(customDirectoryPath)
            ? customDirectoryPath
            : DefaultStorageDir;
        ConfigFilePath = Path.Combine(StorageDirectory, "config.json");
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
                Directory.CreateDirectory(StorageDirectory);
                var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigFilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PowerQuota Config] Save error: {ex.Message}");
            }
        }
    }

    private PowerQuotaConfig Load()
    {
        if (!File.Exists(ConfigFilePath)) return new PowerQuotaConfig();
        try
        {
            var json = File.ReadAllText(ConfigFilePath);
            return JsonSerializer.Deserialize<PowerQuotaConfig>(json) ?? new PowerQuotaConfig();
        }
        catch
        {
            return new PowerQuotaConfig();
        }
    }
}

