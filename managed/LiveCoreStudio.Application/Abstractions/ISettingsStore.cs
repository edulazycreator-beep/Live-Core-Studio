using LiveCoreStudio.Core.Configuration;

namespace LiveCoreStudio.Application.Abstractions;

/// <summary>Loads and persists <see cref="AppSettings"/>.</summary>
public interface ISettingsStore
{
    /// <summary>Location of the settings file, for display in diagnostics.</summary>
    string Location { get; }

    /// <summary>Loads settings. Missing or unreadable files yield defaults (a corrupt file is preserved as a backup).</summary>
    AppSettings Load();

    /// <summary>Validates and writes atomically. Throws <see cref="SettingsValidationException"/> on invalid input.</summary>
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public sealed class SettingsValidationException(IReadOnlyList<string> errors)
    : Exception("Settings are invalid: " + string.Join(" ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
