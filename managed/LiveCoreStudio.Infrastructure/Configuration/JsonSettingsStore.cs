using System.Text.Json;
using System.Text.Json.Serialization;
using LiveCoreStudio.Application.Abstractions;
using LiveCoreStudio.Core.Configuration;
using LiveCoreStudio.Core.Diagnostics;

namespace LiveCoreStudio.Infrastructure.Configuration;

/// <summary>
/// Stores settings as indented JSON. Writes go to a temp file first and are then
/// moved over the original, so a crash mid-save never leaves a half-written file.
/// </summary>
public sealed class JsonSettingsStore(string path, IAppLog? log = null) : ISettingsStore, IDisposable
{
    private const string Source = "settings";

    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public string Location { get; } = path;

    public AppSettings Load()
    {
        if (!File.Exists(Location))
        {
            log?.Info(Source, $"No settings file at {Location}; using defaults.");
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(Location);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
            var errors = settings.Validate();
            if (errors.Count > 0)
            {
                log?.Warn(Source, "Settings file has invalid values; engine settings reset to defaults. " + string.Join(" ", errors));
                settings = settings with { Engine = new EngineSettings() };
            }
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            var backup = $"{Location}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            try { File.Copy(Location, backup, overwrite: true); } catch (IOException) { /* best effort */ }
            log?.Error(Source, $"Could not read settings ({ex.Message}). Kept a copy at {backup}; using defaults.");
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var errors = settings.Validate();
        if (errors.Count > 0) throw new SettingsValidationException(errors);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(Location))!);
            var temp = Location + ".tmp";
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, settings, Options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temp, Location, overwrite: true);
            log?.Info(Source, $"Settings saved to {Location}.");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose() => _writeLock.Dispose();
}
