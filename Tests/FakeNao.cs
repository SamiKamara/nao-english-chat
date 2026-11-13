using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NaoOpenAIChat.Tests;

internal enum Scenario { Idle, Busy, DormantTopics, NoEnglish, ClosedInput, StartRefused, StolenRecorder, SpeechFailure, Finnish }

internal sealed class FakeNao : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task server;
    private readonly Scenario scenario;
    private bool recording;
    private string recordPath = "";
    private string language;
    public TaskCompletionSource RecordingStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ConcurrentQueue<string> Mutations { get; } = new();
    public int Port { get; }
    public bool Recording => recording;
    public bool OtherRecordingStopped { get; private set; }
    public string? Spoken { get; private set; }

    public FakeNao(Scenario scenario = Scenario.Idle)
    {
        this.scenario = scenario;
        language = scenario == Scenario.Finnish ? "Finnish" : "English";
        listener.Start(); Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        server = ServeAsync();
    }
    private async Task ServeAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(lifetime.Token);
                await HandleAsync(client);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (IOException) when (lifetime.IsCancellationRequested) { }
        catch (WebSocketException) { }
    }
    private async Task HandleAsync(TcpClient client)
    {
        var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
        var line = await reader.ReadLineAsync(lifetime.Token);
        if (line is null) return;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while ((line = await reader.ReadLineAsync(lifetime.Token)) is { Length: > 0 })
        {
            var colon = line.IndexOf(':'); if (colon > 0) headers[line[..colon]] = line[(colon + 1)..].Trim();
        }
        if (!headers.TryGetValue("Sec-WebSocket-Key", out var key))
        {
            const string body = "testsession:60:60:websocket";
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"), lifetime.Token); return;
        }
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), lifetime.Token);
        using var socket = WebSocket.CreateFromStream(stream, true, null, TimeSpan.FromSeconds(30));
        await SendAsync(socket, "1::"); await SendAsync(socket, "2::");
        try
        {
            var buffer = new byte[8192];
            while (!lifetime.IsCancellationRequested)
            {
                using var body = new MemoryStream(); WebSocketReceiveResult part;
                do
                {
                    part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), lifetime.Token);
                    if (part.MessageType == WebSocketMessageType.Close) return;
                    body.Write(buffer, 0, part.Count);
                } while (!part.EndOfMessage);
                var packet = Encoding.UTF8.GetString(body.ToArray());
                if (!packet.StartsWith("5:::")) continue;
                using var document = JsonDocument.Parse(packet[4..]);
                var request = document.RootElement.GetProperty("args")[0];
                var (result, error) = Respond(request.GetProperty("params"));
                var reply = "5:::" + JsonSerializer.Serialize(new { name = error ? "error" : "reply", args = new[] { new { idm = request.GetProperty("idm").ToString(), result } } });
                await SendAsync(socket, reply);
            }
        }
        catch (WebSocketException) { }
        catch (IOException) { }
    }
    private (object? Result, bool Error) Respond(JsonElement p)
    {
        var obj = p.GetProperty("obj"); var method = p.GetProperty("member").GetString(); var args = p.GetProperty("args");
        if (obj.ValueKind == JsonValueKind.String)
        {
            var id = args[0].GetString() switch { "ALDialog" => 1, "ALTextToSpeech" => 2, "ALAudioRecorder" => 3, "ALFileManager" => 4, "ALAudioDevice" => 5, "ALMemory" => 6, _ => 0 };
            return id != 0 ? (new { pyobject = id }, false) : ("Unknown service", true);
        }
        switch ((obj.GetInt32(), method))
        {
            case (1, "getActivatedTopics"): return (scenario is Scenario.Busy or Scenario.DormantTopics ? new[] { "EliasChatEN428" } : Array.Empty<string>(), false);
            case (1, "getSubscribersInfo"): return (scenario == Scenario.Busy ? new object[] { new object[] { "other-app", 0, 0 } } : Array.Empty<object>(), false);
            case (2, "getAvailableLanguages"): return (scenario == Scenario.NoEnglish ? new[] { "Finnish" } : new[] { "English", "Finnish" }, false);
            case (2, "getLanguage"): return (language, false);
            case (2, "setLanguage"): language = args[0].GetString()!; Mutations.Enqueue("language:" + language); return (null, false);
            case (2, "say"):
                Spoken = args[0].GetString(); Mutations.Enqueue("say"); return scenario == Scenario.SpeechFailure ? ("Speech engine failed", true) : (null, false);
            case (2, "stopAll"): Mutations.Enqueue("stop-speech"); return (null, false);
            case (3, "startMicrophonesRecording"):
                if (scenario == Scenario.StartRefused) return ("Another recorder is active", true);
                if (recording) return ("Recording already active", true);
                recordPath = args[0].GetString()!;
                if (args[1].GetString() != "wav" || args[2].GetInt32() != 16000 || args[3].ToString() != "[0,0,1,0]") return ("Wrong audio format", true);
                recording = true; Mutations.Enqueue("record"); RecordingStarted.TrySetResult(); return (null, false);
            case (3, "_isRecordingThisFile"): return (recording && scenario != Scenario.StolenRecorder && args[0].GetString() == recordPath, false);
            case (3, "stopMicrophonesRecording"):
                if (scenario == Scenario.StolenRecorder) OtherRecordingStopped = true;
                recording = false; Mutations.Enqueue("stop-record"); return (null, false);
            case (4, "getFileContents"): return (AudioFixture.Create().Select(value => (int)unchecked((sbyte)value)).ToArray(), false);
            case (5, "isInputClosed"): return (scenario == Scenario.ClosedInput, false);
            case (5, "enableEnergyComputation"): Mutations.Enqueue("energy"); return (null, false);
            case (5, "getFrontMicEnergy"): return (100.0, false);
            case (6, "getListData"): return (new object[] { scenario == Scenario.Busy, scenario == Scenario.Busy ? "1" : "0", false }, false);
            default: Mutations.Enqueue("UNEXPECTED:" + obj + ":" + method); return ("Unexpected method", true);
        }
    }
    private async Task SendAsync(WebSocket socket, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > 10)
        {
            await socket.SendAsync(new ArraySegment<byte>(bytes, 0, 7), WebSocketMessageType.Text, false, lifetime.Token);
            await socket.SendAsync(new ArraySegment<byte>(bytes, 7, bytes.Length - 7), WebSocketMessageType.Text, true, lifetime.Token);
        }
        else await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, lifetime.Token);
    }
    public async ValueTask DisposeAsync() { lifetime.Cancel(); listener.Stop(); await server; lifetime.Dispose(); }
}

internal static class AudioFixture
{
    public static byte[] Create(int sampleCount = 16000, short amplitude = 400)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + sampleCount * 2); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(sampleCount * 2);
        for (var i = 0; i < sampleCount; i++) writer.Write((short)(i % 2 == 0 ? amplitude : -amplitude));
        return stream.ToArray();
    }
}
