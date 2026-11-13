using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace NaoOpenAIChat;

public sealed class RobotRpcException(string message) : Exception(message);

public sealed class RobotConnection : IAsyncDisposable
{
    private readonly ClientWebSocket socket = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? receiveTask;
    private int nextId;

    private RobotConnection() { }

    public static async Task<RobotConnection> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(host, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new ArgumentException("Enter the IPv4 address announced by your NAO.");
        var connection = new RobotConnection();
        try
        {
            using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
            using var response = await http.GetAsync($"http://{address}:{port}/libs/qimessaging/2/socket.io/1/", cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new RobotRpcException($"NAO's connection endpoint returned HTTP {(int)response.StatusCode}.");
            var handshake = (await response.Content.ReadAsStringAsync(cancellationToken)).Split(':');
            if (handshake.Length < 4 || handshake[0].Length is < 1 or > 128 || !handshake[0].All(char.IsAsciiLetterOrDigit))
                throw new RobotRpcException("The device did not return a recognized NAO connection handshake.");
            if (!handshake[3].Split(',').Contains("websocket"))
                throw new RobotRpcException("This device does not offer NAO's WebSocket connection.");
            connection.socket.Options.Proxy = null;
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(8));
            await connection.socket.ConnectAsync(new Uri($"ws://{address}:{port}/libs/qimessaging/2/socket.io/1/websocket/{handshake[0]}"), connectTimeout.Token);
            connection.receiveTask = connection.ReceiveAsync();
            await connection.ready.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<JsonElement> ServiceAsync(string name, CancellationToken cancellationToken) =>
        await CallAsync(JsonSerializer.SerializeToElement("ServiceDirectory"), "service", [name], cancellationToken);

    public Task<JsonElement> CallServiceAsync(JsonElement service, string method, object?[] args, CancellationToken cancellationToken, int timeoutSeconds = 10) =>
        CallAsync(service.GetProperty("pyobject"), method, args, cancellationToken, timeoutSeconds);

    private async Task<JsonElement> CallAsync(JsonElement obj, string member, object?[] args, CancellationToken cancellationToken, int timeoutSeconds = 10)
    {
        var id = Interlocked.Increment(ref nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        try
        {
            var packet = new { name = "call", args = new[] { new { idm = id, @params = new { obj, member, args } } } };
            await SendAsync("5:::" + JsonSerializer.Serialize(packet), cancellationToken);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"NAO did not finish '{member}' within {timeoutSeconds} seconds.");
        }
        finally { pending.TryRemove(id, out _); }
    }

    private async Task SendAsync(string text, CancellationToken cancellationToken)
    {
        await sendLock.WaitAsync(cancellationToken);
        try
        {
            if (socket.State != WebSocketState.Open) throw new IOException("The robot connection has closed.");
            var bytes = Encoding.UTF8.GetBytes(text);
            await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, cancellationToken);
        }
        finally { sendLock.Release(); }
    }

    private async Task ReceiveAsync()
    {
        try
        {
            var buffer = new byte[8192];
            while (!lifetime.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                ValueWebSocketReceiveResult part;
                do
                {
                    part = await socket.ReceiveAsync(buffer.AsMemory(), lifetime.Token);
                    if (part.MessageType == WebSocketMessageType.Close) throw new IOException("NAO closed the connection.");
                    message.Write(buffer, 0, part.Count);
                    if (message.Length > 4 * 1024 * 1024) throw new IOException("The robot sent an unexpectedly large response.");
                } while (!part.EndOfMessage);
                var frame = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                if (frame.StartsWith("1::", StringComparison.Ordinal)) { ready.TrySetResult(); continue; }
                if (frame.StartsWith("2::", StringComparison.Ordinal)) { await SendAsync("2::", lifetime.Token); continue; }
                if (frame.StartsWith("0::", StringComparison.Ordinal)) throw new IOException("NAO disconnected the session.");
                if (!frame.StartsWith("5:::", StringComparison.Ordinal)) continue;
                using var parsed = JsonDocument.Parse(frame.AsMemory(4));
                var root = parsed.RootElement;
                if (!root.TryGetProperty("args", out var body)) continue;
                if (body.ValueKind == JsonValueKind.Array) body = body[0];
                if (!body.TryGetProperty("idm", out var idValue)) continue;
                var id = idValue.ValueKind == JsonValueKind.String ? int.Parse(idValue.GetString()!) : idValue.GetInt32();
                if (!pending.TryGetValue(id, out var completion)) continue;
                var result = body.TryGetProperty("result", out var value) ? value.Clone() : JsonSerializer.SerializeToElement<object?>(null);
                if (root.GetProperty("name").GetString() == "error")
                    completion.TrySetException(new RobotRpcException(result.ValueKind == JsonValueKind.String ? result.GetString()! : result.ToString()));
                else completion.TrySetResult(result);
            }
        }
        catch (Exception error)
        {
            ready.TrySetException(error);
            foreach (var waiter in pending.Values) waiter.TrySetException(error);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        socket.Abort();
        if (receiveTask is not null) await receiveTask;
        socket.Dispose();
        lifetime.Dispose();
        sendLock.Dispose();
    }
}
