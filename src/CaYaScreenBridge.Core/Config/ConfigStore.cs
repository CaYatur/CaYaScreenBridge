using System.Text.Json;
using System.Text.Json.Serialization;
using CaYaScreenBridge.Core.Diagnostics;

namespace CaYaScreenBridge.Core.Config;

/// <summary>
/// Loads and saves <see cref="AppConfig"/> with crash resilience: writes go to a temporary file and
/// are swapped in atomically while the previous good copy is kept as a backup, and a corrupt file is
/// recovered from that backup rather than silently resetting the user's calibration.
/// </summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly object _ioLock = new();
    private readonly ILogSink _log;

    public ConfigStore(string directory, ILogSink log)
    {
        Directory = directory;
        _log = log;
        FilePath = Path.Combine(directory, "config.json");
        BackupPath = Path.Combine(directory, "config.backup.json");
    }

    public string Directory { get; }

    public string FilePath { get; }

    public string BackupPath { get; }

    public static string DefaultDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CaYaScreenBridge");

    public AppConfig Load()
    {
        lock (_ioLock)
        {
            if (TryRead(FilePath, out AppConfig? primary))
            {
                return Migrate(primary!);
            }

            if (File.Exists(FilePath))
            {
                _log.Warn("Config", "Primary configuration file is unreadable, trying the backup.");
                TryQuarantine(FilePath);
            }

            if (TryRead(BackupPath, out AppConfig? backup))
            {
                _log.Warn("Config", "Recovered configuration from the backup copy.");
                return Migrate(backup!);
            }

            _log.Info("Config", "No usable configuration found, starting from defaults.");
            return new AppConfig();
        }
    }

    public void Save(AppConfig config)
    {
        lock (_ioLock)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);

                string temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(config, SerializerOptions));

                if (File.Exists(FilePath))
                {
                    // File.Replace performs the swap and the backup rotation in one step, so a crash
                    // mid-save can never leave both copies truncated.
                    File.Replace(temp, FilePath, BackupPath, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temp, FilePath);
                }
            }
            catch (Exception ex)
            {
                _log.Error("Config", $"Failed to save configuration: {ex.Message}");
            }
        }
    }

    private bool TryRead(string path, out AppConfig? config)
    {
        config = null;

        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            config = JsonSerializer.Deserialize<AppConfig>(json, SerializerOptions);
            return config is not null;
        }
        catch (Exception ex)
        {
            _log.Warn("Config", $"Could not read '{Path.GetFileName(path)}': {ex.Message}");
            return false;
        }
    }

    private void TryQuarantine(string path)
    {
        try
        {
            string target = path + ".corrupt";
            File.Copy(path, target, overwrite: true);
            _log.Warn("Config", $"Kept a copy of the unreadable file as '{Path.GetFileName(target)}'.");
        }
        catch
        {
            // Quarantining is a convenience for diagnostics; never let it block recovery.
        }
    }

    private AppConfig Migrate(AppConfig config)
    {
        int sourceSchemaVersion = config.SchemaVersion;

        if (config.SchemaVersion > AppConfig.CurrentSchemaVersion)
        {
            _log.Warn(
                "Config",
                $"Configuration was written by a newer version (schema {config.SchemaVersion}); " +
                "unknown settings will be preserved but not used.");
        }

        config.SchemaVersion = AppConfig.CurrentSchemaVersion;
        config.General ??= new GeneralSettings();
        config.Transition ??= new TransitionSettings();
        config.Drag ??= new DragSettings();
        config.Games ??= new GameSettings();
        config.Rules ??= new List<AppRule>();
        config.Profiles ??= new List<LayoutProfile>();

        if (sourceSchemaVersion < 2)
        {
            config.Drag.Mode = DragScalingMode.Off;
            config.Games.PauseInExclusiveFullScreen = false;
            config.Games.PauseForAntiCheat = false;
        }

        // Guard against out of range values that a hand edited file could introduce and that would
        // otherwise reach the hook callback.
        config.Transition.BorderResistanceMm = Math.Clamp(config.Transition.BorderResistanceMm, 0, 200);
        config.Transition.ResistanceResetMs = Math.Clamp(config.Transition.ResistanceResetMs, 50, 5000);
        config.Drag.LiveThrottleMs = Math.Clamp(config.Drag.LiveThrottleMs, 0, 500);

        return config;
    }
}
