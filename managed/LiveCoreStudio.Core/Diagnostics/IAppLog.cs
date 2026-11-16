namespace LiveCoreStudio.Core.Diagnostics;

public enum AppLogLevel { Debug, Info, Warning, Error }

public sealed record AppLogEntry(DateTimeOffset Timestamp, AppLogLevel Level, string Source, string Message);

/// <summary>Application-wide log sink. Implementations must be thread-safe.</summary>
public interface IAppLog
{
    void Write(AppLogLevel level, string source, string message);

    /// <summary>Raised for every entry, on the writing thread.</summary>
    event Action<AppLogEntry>? EntryWritten;
}

public static class AppLogExtensions
{
    public static void Info(this IAppLog log, string source, string message) => log.Write(AppLogLevel.Info, source, message);
    public static void Warn(this IAppLog log, string source, string message) => log.Write(AppLogLevel.Warning, source, message);
    public static void Error(this IAppLog log, string source, string message) => log.Write(AppLogLevel.Error, source, message);
    public static void Debug(this IAppLog log, string source, string message) => log.Write(AppLogLevel.Debug, source, message);
}
