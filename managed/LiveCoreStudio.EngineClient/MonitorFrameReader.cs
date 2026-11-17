using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;

namespace LiveCoreStudio.EngineClient;

/// <summary>
/// Reads a monitor feed (Program / Preview) that the engine publishes in shared memory.
/// Layout (PROTOCOL.md, "Monitor feeds"): 64-byte header, then BGRA8 pixels, top-down.
/// The engine writes with a sequence lock; a read is retried if it overlapped a write.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class MonitorFrameReader : IDisposable
{
    private const uint Magic = 0x464D434C; // "LCMF"
    private const int HeaderSize = 64;

    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _base;

    private MonitorFrameReader(MemoryMappedFile file, MemoryMappedViewAccessor view, byte* basePtr)
    {
        _file = file;
        _view = view;
        _base = basePtr;
        Width = *(int*)(_base + 8);
        Height = *(int*)(_base + 12);
        Stride = *(int*)(_base + 16);
    }

    public string Name { get; private init; } = "";
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public int BufferSize => Stride * Height;

    /// <summary>Opens a feed by name; null if the engine has not created it (or it is not a monitor feed).</summary>
    public static MonitorFrameReader? TryOpen(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        MemoryMappedFile? file = null;
        MemoryMappedViewAccessor? view = null;
        try
        {
            file = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
            view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            byte* p = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            p += view.PointerOffset;
            if (*(uint*)p != Magic || *(uint*)(p + 4) != 1 || *(int*)(p + 8) <= 0 || *(int*)(p + 12) <= 0)
            {
                view.SafeMemoryMappedViewHandle.ReleasePointer();
                view.Dispose();
                file.Dispose();
                return null;
            }
            return new MonitorFrameReader(file, view, p) { Name = name };
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            view?.Dispose();
            file?.Dispose();
            return null;
        }
    }

    /// <summary>Copies the latest complete frame into <paramref name="destination"/> (at least <see cref="BufferSize"/> bytes).</summary>
    /// <returns>False if no frame has been published yet or every attempt overlapped a write.</returns>
    public bool TryRead(byte[] destination, out ulong frameNumber)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (destination.Length < BufferSize) throw new ArgumentException("Destination buffer is too small.", nameof(destination));
        frameNumber = 0;
        ref ulong sequence = ref *(ulong*)(_base + 24);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var before = Volatile.Read(ref sequence);
            if (before == 0) return false;             // nothing written yet
            if ((before & 1) != 0) { Thread.SpinWait(64); continue; }  // write in progress
            fixed (byte* dst = destination)
            {
                Buffer.MemoryCopy(_base + HeaderSize, dst, destination.Length, BufferSize);
            }
            frameNumber = *(ulong*)(_base + 32);
            Interlocked.MemoryBarrier();
            if (Volatile.Read(ref sequence) == before) return true;
        }
        return false;
    }

    public void Dispose()
    {
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _file.Dispose();
    }
}
