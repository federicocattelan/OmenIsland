using System.IO;
using System.Text.Json;

namespace OmenIsland.App.Services;

public sealed class AppSettings
{
    public bool HardwareControlAccepted { get; set; }

    public bool RestoreAutomaticOnExit { get; set; } = true;

    public bool StartWithWindows { get; set; }

    public string LastProfile { get; set; } = "Default";

    public bool ProfileChosen { get; set; }

    public string LastFanMode { get; set; } = "Auto";

    public int LastManualFanPercent { get; set; } = 50;

    public string GoogleClientId { get; set; } = "";

    public string GoogleClientSecret { get; set; } = "";
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _settingsPath;

    public SettingsStore(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "OmenIsland",
            "settings.json");
    }

    public AppSettings Current { get; private set; } = new();

    public async Task<AppSettings> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_settingsPath))
            {
                Current = new AppSettings();
                return Current;
            }

            await using var stream = File.OpenRead(_settingsPath);
            Current = await JsonSerializer.DeserializeAsync<AppSettings>(
                          stream,
                          JsonOptions,
                          cancellationToken)
                      ?? new AppSettings();
            if (Current.LastProfile == "Balanced")
            {
                Current.LastProfile = "Default";
            }

            if (Current.LastFanMode == "Manual")
            {
                Current.LastFanMode = "Auto";
            }

            return Current;
        }
        catch (JsonException)
        {
            Current = new AppSettings();
            return Current;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(_settingsPath)
                ?? throw new InvalidOperationException(
                    "Percorso impostazioni non valido.");
            Directory.CreateDirectory(directory);

            var temporaryPath = _settingsPath + ".tmp";
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    Current,
                    JsonOptions,
                    cancellationToken);
            }

            File.Move(temporaryPath, _settingsPath, true);
        }
        finally
        {
            _lock.Release();
        }
    }
}
