using System.Text.Json;

namespace PowerQuota.Core.Storage;

public class ConfigStorage
{
    public static readonly string DefaultStorageDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PowerQuota"
    );
    public static readonly string DefaultConfigFile = Path.Combine(DefaultStorageDir, "config.json");

    private readonly string _configFilePath;
    private readonly object _lock = new();
    private PowerQuotaConfig _current;

    public string StorageDirectory => Path.GetDirectoryName(_configFilePath) ?? DefaultStorageDir;
    public string ConfigFilePath => _configFilePath;

    public ConfigStorage(string? customPath = null)
    {
        if (string.IsNullOrWhiteSpace(customPath))
        {
            _configFilePath = DefaultConfigFile;
        }
        else if (Directory.Exists(customPath) || !customPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            _configFilePath = Path.Combine(customPath, "config.json");
        }
        else
        {
            _configFilePath = customPath;
        }
        _current = Load();
    }

    public PowerQuotaConfig Current
    {
        get
        {
            lock (_lock)
            {
                return _current.Clone();
            }
        }
    }

    public PowerQuotaConfig Mutate(Action<PowerQuotaConfig> mutator)
    {
        ArgumentNullException.ThrowIfNull(mutator);
        lock (_lock)
        {
            var clone = _current.Clone();
            mutator(clone);
            if (SaveInternal(clone))
            {
                _current = clone;
            }
            return _current.Clone();
        }
    }

    public void Save(PowerQuotaConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        lock (_lock)
        {
            var clone = config.Clone();
            if (SaveInternal(clone))
            {
                _current = clone;
            }
        }
    }

    public PowerQuotaConfig Reload()
    {
        lock (_lock)
        {
            _current = Load();
            return _current.Clone();
        }
    }

    private bool SaveInternal(PowerQuotaConfig config)
    {
        var dir = Path.GetDirectoryName(_configFilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tempFile = Path.Combine(
            string.IsNullOrEmpty(dir) ? "." : dir,
            $"{Path.GetFileName(_configFilePath)}.{Guid.NewGuid():N}.tmp"
        );

        try
        {
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tempFile, json);
            File.Move(tempFile, _configFilePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PowerQuota Config] Save error: {ex.Message}");
            return false;
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { }
            }
        }
    }

    private PowerQuotaConfig Load()
    {
        if (!File.Exists(_configFilePath)) return new PowerQuotaConfig();
        try
        {
            var json = File.ReadAllText(_configFilePath);
            return JsonSerializer.Deserialize<PowerQuotaConfig>(json) ?? new PowerQuotaConfig();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PowerQuota Config] Load error: {ex.Message}");
            return new PowerQuotaConfig();
        }
    }
}
