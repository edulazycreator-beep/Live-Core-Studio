using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using LiveCoreStudio.EngineClient.Protocol;

namespace LiveCoreStudio.EngineClient;

/// <summary>
/// One control connection to the engine. Requests are correlated by id, so several
/// may be in flight at once; a background reader completes them as replies arrive.
/// </summary>
public sealed class EngineConnection : IAsyncDisposable
{
    public const string ClientName = "LiveCoreStudio";

    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<Frame>> _pending = new();
    private readonly CancellationTokenSource _readerCts = new();
    private readonly Task _reader;
    private int _nextRequestId;
    private volatile bool _connected = true;

    private EngineConnection(Stream stream)
    {
        _stream = stream;
        _reader = Task.Run(ReadLoopAsync);
    }

    public bool IsConnected => _connected;

    /// <summary>Raised once when the connection ends for any reason other than DisposeAsync.</summary>
    public event Action<Exception?>? Disconnected;

    /// <summary>Connects to the engine pipe (retrying until it exists or the timeout elapses) and performs the handshake.</summary>
    public static async Task<(EngineConnection Connection, HelloAck Hello)> ConnectAsync(
        string pipeName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var connection = new EngineConnection(pipe);
        try
        {
            var hello = await connection.RequestAsync<HelloRequest, HelloAck>(
                MessageType.Hello, new HelloRequest(ClientName, FrameCodec.Version), MessageType.HelloAck,
                timeout, cancellationToken).ConfigureAwait(false);
            return (connection, hello);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>For tests and tools: wraps an already-connected stream without handshaking.</summary>
    internal static EngineConnection FromStream(Stream stream) => new(stream);

    public Task<PongResponse> PingAsync(long seq, TimeSpan timeout, CancellationToken ct) =>
        RequestAsync<PingRequest, PongResponse>(MessageType.Ping,
            new PingRequest(seq, Environment.TickCount64), MessageType.Pong, timeout, ct);

    public Task<EngineStatus> GetStatusAsync(TimeSpan timeout, CancellationToken ct) =>
        RequestAsync<object, EngineStatus>(MessageType.GetStatus, null, MessageType.Status, timeout, ct);

    public async Task ShutdownAsync(string reason, TimeSpan timeout, CancellationToken ct) =>
        await SendRawAsync(MessageType.Shutdown, JsonSerializer.SerializeToUtf8Bytes(new ShutdownRequest(reason), ProtocolJson.Options),
            MessageType.ShutdownAck, timeout, ct).ConfigureAwait(false);

    internal async Task<TResponse> RequestAsync<TRequest, TResponse>(
        MessageType type, TRequest? request, MessageType expected, TimeSpan timeout, CancellationToken ct)
    {
        var payload = request is null ? ProtocolJson.EmptyObject : JsonSerializer.SerializeToUtf8Bytes(request, ProtocolJson.Options);
        var frame = await SendRawAsync(type, payload, expected, timeout, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<TResponse>(frame.Payload.Span, ProtocolJson.Options)
               ?? throw new EngineProtocolException("bad_payload", $"Empty {expected} payload.");
    }

    internal async Task<Frame> SendRawAsync(MessageType type, byte[] payload, MessageType expected, TimeSpan timeout, CancellationToken ct)
    {
        if (!_connected) throw new EngineDisconnectedException("Engine connection is closed.");

        var id = unchecked((uint)Interlocked.Increment(ref _nextRequestId));
        var tcs = new TaskCompletionSource<Frame>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            var bytes = FrameCodec.Encode(type, id, payload);
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await _stream.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw new EngineDisconnectedException("Failed to write to engine.", ex);
            }
            finally
            {
                _writeLock.Release();
            }

            var reply = await tcs.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
            if (reply.Type == MessageType.Error)
            {
                var err = JsonSerializer.Deserialize<ErrorResponse>(reply.Payload.Span, ProtocolJson.Options);
                throw new EngineProtocolException(err?.Code ?? "unknown", err?.Message ?? "Engine returned an error.");
            }
            if (reply.Type != expected)
                throw new EngineProtocolException("unexpected_reply", $"Expected {expected}, got {reply.Type}.");
            return reply;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReadLoopAsync()
    {
        Exception? reason = null;
        try
        {
            while (!_readerCts.IsCancellationRequested)
            {
                var frame = await FrameCodec.ReadAsync(_stream, _readerCts.Token).ConfigureAwait(false);
                if (frame is null) break;  // engine closed the pipe
                if (_pending.TryGetValue(frame.Value.RequestId, out var tcs)) tcs.TrySetResult(frame.Value);
                // Unsolicited frames (requestId 0) are reserved for engine events in later phases.
            }
        }
        catch (OperationCanceledException) when (_readerCts.IsCancellationRequested)
        {
            return;  // disposed
        }
        catch (Exception ex)
        {
            reason = ex;
        }

        _connected = false;
        var failure = new EngineDisconnectedException("Engine connection closed.", reason);
        foreach (var pending in _pending.Values) pending.TrySetException(failure);
        Disconnected?.Invoke(reason);
    }

    public async ValueTask DisposeAsync()
    {
        _connected = false;
        await _readerCts.CancelAsync().ConfigureAwait(false);
        await _stream.DisposeAsync().ConfigureAwait(false);
        try { await _reader.ConfigureAwait(false); } catch (Exception) { /* reader already reported */ }
        foreach (var pending in _pending.Values) pending.TrySetCanceled();
        _readerCts.Dispose();
        _writeLock.Dispose();
    }
}
