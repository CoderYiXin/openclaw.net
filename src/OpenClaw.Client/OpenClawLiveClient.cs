using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using OpenClaw.Core.Models;

namespace OpenClaw.Client;

public sealed class OpenClawLiveClient : IAsyncDisposable
{
    private readonly int _maxMessageBytes;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _stateLock = new();
    private WebSocket? _ws;
    private CancellationTokenSource? _rxCts;
    private Task? _rxLoop;

    public OpenClawLiveClient(int maxMessageBytes = 512 * 1024)
    {
        _maxMessageBytes = maxMessageBytes;
    }

    public bool IsConnected
    {
        get
        {
            lock (_stateLock)
            {
                return _ws?.State == WebSocketState.Open;
            }
        }
    }

    public event Action<LiveServerEnvelope>? OnEnvelopeReceived;
    public event Action<string>? OnTextChunk;
    public event Action<string>? OnError;

    public static Uri BuildWebSocketUri(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("Base URL is required.", nameof(baseUrl));

        if (!Uri.TryCreate(baseUrl.TrimEnd('/'), UriKind.Absolute, out var baseUri))
            throw new ArgumentException($"Invalid base URL: {baseUrl}", nameof(baseUrl));

        return BuildWebSocketUri(baseUri);
    }

    public static Uri BuildWebSocketUri(Uri baseUri)
    {
        var builder = new UriBuilder(baseUri)
        {
            Scheme = string.Equals(baseUri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
            Path = "/ws/live",
            Query = string.Empty
        };
        return builder.Uri;
    }

    public async Task ConnectAsync(
        Uri wsUri,
        string? bearerToken,
        LiveSessionOpenRequest request,
        CancellationToken ct)
    {
        await DisconnectAsync(ct);

        var ws = new ClientWebSocket();
        if (!string.IsNullOrWhiteSpace(bearerToken))
            ws.Options.SetRequestHeader("Authorization", $"Bearer {bearerToken}");

        await ws.ConnectAsync(wsUri, ct);

        var payload = JsonSerializer.Serialize(request, CoreJsonContext.Default.LiveSessionOpenRequest);
        var bytes = Encoding.UTF8.GetBytes(payload);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);

        var rxCts = new CancellationTokenSource();
        var rxLoop = Task.Run(() => ReceiveLoopAsync(ws, rxCts.Token), rxCts.Token);

        lock (_stateLock)
        {
            _ws = ws;
            _rxCts = rxCts;
            _rxLoop = rxLoop;
        }
    }

    public async Task SendTextAsync(string text, bool turnComplete, CancellationToken ct)
        => await SendEnvelopeAsync(new LiveClientEnvelope
        {
            Type = "text",
            Text = text,
            TurnComplete = turnComplete
        }, ct);

    public async Task SendAudioAsync(string base64Data, string mimeType, bool turnComplete, CancellationToken ct)
        => await SendEnvelopeAsync(new LiveClientEnvelope
        {
            Type = "audio",
            Base64Data = base64Data,
            MimeType = mimeType,
            TurnComplete = turnComplete
        }, ct);

    public async Task InterruptAsync(CancellationToken ct)
        => await SendEnvelopeAsync(new LiveClientEnvelope
        {
            Type = "interrupt"
        }, ct);

    public async Task CloseSessionAsync(CancellationToken ct)
    {
        try
        {
            await SendEnvelopeAsync(new LiveClientEnvelope { Type = "close" }, ct);
        }
        catch
        {
        }

        await DisconnectAsync(ct);
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        WebSocket? ws;
        CancellationTokenSource? rxCts;
        Task? rxLoop;

        await _sendLock.WaitAsync(ct);
        try
        {
            lock (_stateLock)
            {
                ws = _ws;
                rxCts = _rxCts;
                rxLoop = _rxLoop;
                _ws = null;
                _rxCts = null;
                _rxLoop = null;
            }

            try
            {
                if (rxCts is not null)
                    await rxCts.CancelAsync();
            }
            catch
            {
            }

            if (rxLoop is not null)
            {
                try { await rxLoop.WaitAsync(TimeSpan.FromSeconds(2), ct); } catch { }
            }

            if (rxCts is not null)
            {
                try { rxCts.Dispose(); } catch { }
            }

            if (ws is null)
                return;

            try
            {
                if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "client closing", ct);
            }
            catch
            {
            }
            finally
            {
                ws.Dispose();
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task SendEnvelopeAsync(LiveClientEnvelope envelope, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(envelope, CoreJsonContext.Default.LiveClientEnvelope);
        var bytes = Encoding.UTF8.GetBytes(payload);
        if (bytes.Length > _maxMessageBytes)
            throw new InvalidOperationException("Message too large.");

        await _sendLock.WaitAsync(ct);
        try
        {
            WebSocket? ws;
            lock (_stateLock)
            {
                ws = _ws;
            }

            if (ws is null || ws.State != WebSocketState.Open)
                throw new InvalidOperationException("Live WebSocket is not connected.");

            await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        var writer = new ArrayBufferWriter<byte>(16 * 1024);

        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                writer.Clear();
                WebSocketReceiveResult result;

                do
                {
                    result = await ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;

                    if (writer.WrittenCount + result.Count > _maxMessageBytes)
                        throw new InvalidOperationException("Inbound live message too large.");

                    writer.Write(buffer.AsSpan(0, result.Count));
                }
                while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                var text = Encoding.UTF8.GetString(writer.WrittenSpan);
                LiveServerEnvelope? envelope;
                try
                {
                    envelope = JsonSerializer.Deserialize(text, CoreJsonContext.Default.LiveServerEnvelope);
                }
                catch (Exception ex)
                {
                    OnError?.Invoke(ex.Message);
                    continue;
                }

                if (envelope is null)
                    continue;

                if (!string.IsNullOrWhiteSpace(envelope.Text) &&
                    string.Equals(envelope.Type, "text", StringComparison.OrdinalIgnoreCase))
                {
                    OnTextChunk?.Invoke(envelope.Text);
                }

                try
                {
                    OnEnvelopeReceived?.Invoke(envelope);
                }
                catch (Exception ex)
                {
                    OnError?.Invoke(ex.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            OnError?.Invoke(ex.Message);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await DisconnectAsync(CancellationToken.None); } catch { }
        _sendLock.Dispose();
    }

    internal void SetConnectedSocketForTest(WebSocket ws)
    {
        lock (_stateLock)
        {
            _ws = ws;
            _rxCts = null;
            _rxLoop = null;
        }
    }

    internal Task RunReceiveLoopForTest(WebSocket ws, CancellationToken ct)
        => ReceiveLoopAsync(ws, ct);
}
