using System.Text;
using LiveCoreStudio.Application.Abstractions;
using LiveCoreStudio.Core.Configuration;
using LiveCoreStudio.EngineClient.Protocol;
using LiveCoreStudio.Infrastructure.Configuration;

namespace LiveCoreStudio.Tests;

public static class ProtocolTests
{
    [Test]
    public static async Task Frame_round_trips_through_a_stream()
    {
        var payload = Encoding.UTF8.GetBytes("{\"seq\":42}");
        var bytes = FrameCodec.Encode(MessageType.Ping, 0xDEADBEEF, payload);
        Assert.Equal(FrameCodec.HeaderSize + payload.Length, bytes.Length);
        Assert.Equal((byte)'L', bytes[0]);
        Assert.Equal((byte)'P', bytes[3]);

        using var ms = new MemoryStream(bytes);
        var frame = await FrameCodec.ReadAsync(ms, CancellationToken.None);
        Assert.True(frame.HasValue);
        Assert.Equal(MessageType.Ping, frame!.Value.Type);
        Assert.Equal(0xDEADBEEFu, frame.Value.RequestId);
        Assert.Equal("{\"seq\":42}", Encoding.UTF8.GetString(frame.Value.Payload.Span));
        Assert.True(await FrameCodec.ReadAsync(ms, CancellationToken.None) is null, "clean EOF returns null");
    }

    [Test]
    public static async Task Frame_reader_rejects_bad_magic_and_version()
    {
        var bytes = FrameCodec.Encode(MessageType.Ping, 1, []);
        bytes[0] = (byte)'X';
        var ex = await Assert.ThrowsAsync<EngineProtocolException>(async () => await FrameCodec.ReadAsync(new MemoryStream(bytes), CancellationToken.None));
        Assert.Equal("bad_magic", ex.Code);

        bytes = FrameCodec.Encode(MessageType.Ping, 1, []);
        bytes[4] = 2;
        ex = await Assert.ThrowsAsync<EngineProtocolException>(async () => await FrameCodec.ReadAsync(new MemoryStream(bytes), CancellationToken.None));
        Assert.Equal("protocol_mismatch", ex.Code);
    }

    [Test]
    public static async Task Frame_reader_detects_truncation()
    {
        var bytes = FrameCodec.Encode(MessageType.Ping, 1, "{\"a\":1}"u8);
        await Assert.ThrowsAsync<EndOfStreamException>(async () =>
            await FrameCodec.ReadAsync(new MemoryStream(bytes[..(bytes.Length - 2)]), CancellationToken.None));
    }
}

public static class SettingsTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"lcs-settings-{Guid.NewGuid():N}.json");

    [Test]
    public static async Task Settings_round_trip()
    {
        var path = TempFile();
        try
        {
            using var store = new JsonSettingsStore(path);
            var original = new AppSettings { Engine = new EngineSettings { HeartbeatIntervalMs = 500, HeartbeatTimeoutMs = 1500, LogLevel = "debug" } };
            await store.SaveAsync(original);
            var loaded = store.Load();
            Assert.Equal(original.Engine, loaded.Engine);
            Assert.True(!File.Exists(path + ".tmp"), "temp file cleaned up");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public static void Missing_file_yields_defaults()
    {
        using var s = new JsonSettingsStore(TempFile());
        var loaded = s.Load();
        Assert.Equal(new EngineSettings(), loaded.Engine);
    }

    [Test]
    public static void Corrupt_file_yields_defaults_and_is_backed_up()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, "{ this is not json");
            using var s = new JsonSettingsStore(path);
            var loaded = s.Load();
            Assert.Equal(new EngineSettings(), loaded.Engine);
            var backups = Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".corrupt-*");
            Assert.True(backups.Length == 1, "one backup created");
            foreach (var b in backups) File.Delete(b);
        }
        finally { File.Delete(path); }
    }

    [Test]
    public static async Task Invalid_settings_are_rejected_on_save()
    {
        using var store = new JsonSettingsStore(TempFile());
        var bad = new AppSettings { Engine = new EngineSettings { HeartbeatIntervalMs = 5000, HeartbeatTimeoutMs = 1000, LogLevel = "loud" } };
        var ex = await Assert.ThrowsAsync<SettingsValidationException>(() => store.SaveAsync(bad));
        Assert.Equal(2, ex.Errors.Count, "error count");
    }
}
