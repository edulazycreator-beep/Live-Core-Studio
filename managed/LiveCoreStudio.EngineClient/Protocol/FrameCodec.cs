using System.Buffers.Binary;

namespace LiveCoreStudio.EngineClient.Protocol;

public enum MessageType : ushort
{
    Hello = 1,
    HelloAck = 2,
    Ping = 3,
    Pong = 4,
    GetStatus = 5,
    Status = 6,
    Shutdown = 7,
    ShutdownAck = 8,
    Ok = 9,
    ListVideoDevices = 16,
    VideoDevices = 17,
    AddSource = 18,
    SourceAdded = 19,
    RemoveSource = 20,
    SetPreview = 22,
    SetProgram = 23,
    Cut = 24,
    AutoTransition = 25,
    GetSwitcherState = 26,
    SwitcherState = 27,
    Error = 255,
}

public readonly record struct Frame(MessageType Type, uint RequestId, ReadOnlyMemory<byte> Payload);

/// <summary>Reads and writes protocol v1 frames: 16-byte little-endian header + UTF-8 JSON payload.</summary>
public static class FrameCodec
{
    public const uint Magic = 0x5053434C; // "LCSP"
    public const ushort Version = 1;
    public const int HeaderSize = 16;
    public const int MaxPayload = 1 << 20;

    public static byte[] Encode(MessageType type, uint requestId, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayload) throw new ArgumentException("Payload exceeds the protocol maximum.", nameof(payload));
        var buffer = new byte[HeaderSize + payload.Length];
        var h = buffer.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(h, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(h[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(h[6..], (ushort)type);
        BinaryPrimitives.WriteUInt32LittleEndian(h[8..], requestId);
        BinaryPrimitives.WriteUInt32LittleEndian(h[12..], (uint)payload.Length);
        payload.CopyTo(h[HeaderSize..]);
        return buffer;
    }

    /// <summary>Reads one frame. Returns null on clean end-of-stream before a header starts.</summary>
    /// <exception cref="EngineProtocolException">Malformed header.</exception>
    /// <exception cref="EndOfStreamException">Stream ended mid-frame.</exception>
    public static async ValueTask<Frame?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[HeaderSize];
        var first = await stream.ReadAtLeastAsync(header, HeaderSize, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        if (first == 0) return null;
        if (first < HeaderSize) throw new EndOfStreamException("Engine connection closed mid-header.");

        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic)
            throw new EngineProtocolException("bad_magic", "Frame does not start with the LCSP magic.");
        var version = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4));
        if (version != Version)
            throw new EngineProtocolException("protocol_mismatch", $"Engine sent protocol v{version}; client speaks v{Version}.");
        var type = (MessageType)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
        var requestId = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
        if (length > MaxPayload)
            throw new EngineProtocolException("payload_too_large", $"Payload of {length} bytes exceeds the maximum.");

        var payload = new byte[length];
        if (length > 0) await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return new Frame(type, requestId, payload);
    }
}
