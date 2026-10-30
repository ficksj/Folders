using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ShareManager.Services;

public class AppConfig
{
    public bool ShowSystemShares { get; set; } = false;
    public string? LastSelectedServerId { get; set; }

    // Theme & Glass UI Settings
    public string ThemeName { get; set; } = "CyberEmerald"; // CyberEmerald, NeonMatrix, ObsidianMint, CyberBlue
    public bool EnableGlassEffects { get; set; } = true;

    // Share Creation Defaults
    public bool DefaultAbeEnabled { get; set; } = true;
    public bool DefaultCreateFolderIfMissing { get; set; } = true;
    public string DefaultBaseSharePath { get; set; } = @"C:\Shares\";

    // Network & Behavior Settings
    public int AutoRefreshIntervalSeconds { get; set; } = 0; // 0 = Off, 30, 60, 300
    public int WinRmTimeoutSeconds { get; set; } = 25;
    public bool ConfirmDangerousActions { get; set; } = true;

    public List<SavedServerEntry> Servers { get; set; } = new();
    public List<string> DfsRoots { get; set; } = new();
}

public class SavedServerEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Hostname { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsLocal { get; set; }
}

public static class ConfigService
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Folders_CrazyMonkeys");

    private static readonly string ConfigFilePath = Path.Combine(ConfigDir, "config.json");

    public static string GetConfigFilePath() => ConfigFilePath;

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigFilePath))
            {
                var json = File.ReadAllText(ConfigFilePath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json);
                if (cfg != null && cfg.Servers.Count > 0)
                    return cfg;
            }
        }
        catch
        {
            // Ignore corrupt config and return default
        }

        var defaultConfig = new AppConfig
        {
            ShowSystemShares = false,
            ThemeName = "CyberEmerald",
            EnableGlassEffects = true,
            DefaultAbeEnabled = true,
            DefaultCreateFolderIfMissing = true,
            DefaultBaseSharePath = @"C:\Shares\",
            AutoRefreshIntervalSeconds = 0,
            WinRmTimeoutSeconds = 25,
            ConfirmDangerousActions = true,
            Servers = new List<SavedServerEntry>
            {
                new SavedServerEntry
                {
                    Id = "local-machine",
                    Hostname = Environment.MachineName,
                    DisplayName = $"Этот компьютер ({Environment.MachineName})",
                    Description = "Локальный сервер",
                    IsLocal = true
                }
            }
        };
        Save(defaultConfig);
        return defaultConfig;
    }

    public static void Save(AppConfig config)
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(ConfigFilePath, json);
        }
        catch
        {
            // Ignore write errors
        }
    }
}
