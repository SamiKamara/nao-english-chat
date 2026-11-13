using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NaoOpenAIChat;

public sealed class RobotSession : IAsyncDisposable
{
    private readonly RobotConnection connection;
    private readonly JsonElement recorder, files, audio, tts, dialog, memory;
    private readonly string fileName;
    private readonly string originalLanguage;
    private bool recording, speaking;
    private RobotSession(RobotConnection connection, JsonElement recorder, JsonElement files, JsonElement audio, JsonElement tts, JsonElement dialog, JsonElement memory, string clientId, string originalLanguage)
    {
        this.connection = connection; this.recorder = recorder; this.files = files; this.audio = audio; this.tts = tts; this.dialog = dialog; this.memory = memory;
        this.originalLanguage = originalLanguage;
        fileName = $"nao-openai-chat-{clientId}.wav";
    }
    public static async Task<RobotSession> ConnectAsync(string address, string clientId, CancellationToken token, int port = 80)
    {
        if (!Guid.TryParseExact(clientId, "N", out _)) throw new ArgumentException("The local recording identifier is invalid.");
        var connection = await RobotConnection.ConnectAsync(address, port, token);
        RobotSession? session = null;
        try
        {
            var dialog = await connection.ServiceAsync("ALDialog", token);
            var memory = await connection.ServiceAsync("ALMemory", token);
            if (!await IsIdleAsync(connection, dialog, memory, token)) throw new InvalidOperationException("Stop the active conversation or lesson in ELIAS or another robot app first, then connect here.");
            var tts = await connection.ServiceAsync("ALTextToSpeech", token);
            var languages = await connection.CallServiceAsync(tts, "getAvailableLanguages", [], token);
            if (!languages.EnumerateArray().Any(item => item.GetString() == "English")) throw new InvalidOperationException("NAO does not list an English speaking voice. Check its language installation first.");
            var original = (await connection.CallServiceAsync(tts, "getLanguage", [], token)).GetString() ?? "";
            var recorder = await connection.ServiceAsync("ALAudioRecorder", token);
            var files = await connection.ServiceAsync("ALFileManager", token);
            var audio = await connection.ServiceAsync("ALAudioDevice", token);
            session = new(connection, recorder, files, audio, tts, dialog, memory, clientId, original);
            if ((await connection.CallServiceAsync(audio, "isInputClosed", [], token)).GetBoolean())
                throw new InvalidOperationException("NAO's microphone input is closed. Open it with the robot's normal audio tools before connecting.");
            if (original != "English") await connection.CallServiceAsync(tts, "setLanguage", ["English"], token, 25);
            await connection.CallServiceAsync(audio, "enableEnergyComputation", [], token);
            return session;
        }
        catch { if (session is not null) await session.DisposeAsync(); else await connection.DisposeAsync(); throw; }
    }
    public async Task EnsureIdleAsync(CancellationToken token)
    {
        if (!await IsIdleAsync(connection, dialog, memory, token)) throw new InvalidOperationException("NAO is speaking or listening in another robot app. Stop that conversation before continuing here.");
    }
    private static async Task<bool> IsIdleAsync(RobotConnection connection, JsonElement dialog, JsonElement memory, CancellationToken token)
    {
        // A stopped lesson can leave activated topics behind. These do not mean the engine is running.
        var state = await connection.CallServiceAsync(memory, "getListData", [new[] { "ALSpeechRecognition/IsRunning", "Dialog/IsStarted", "ALTextToSpeech/TextStarted" }], token);
        var subscribers = await connection.CallServiceAsync(dialog, "getSubscribersInfo", [], token);
        return state.ValueKind == JsonValueKind.Array && state.GetArrayLength() == 3 && state.EnumerateArray().All(IsFalse)
            && subscribers.ValueKind == JsonValueKind.Array && subscribers.GetArrayLength() == 0;
    }
    private static bool IsFalse(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.False => true,
        JsonValueKind.Number => value.TryGetInt32(out var number) && number == 0,
        JsonValueKind.String => value.GetString() is "0" or "false" or "False",
        _ => false
    };
    public async Task<double> CalibrateAsync(CancellationToken token)
    {
        var values = new List<double>();
        for (var i = 0; i < 7; i++) { values.Add(await EnergyAsync(token)); await Task.Delay(130, token); }
        values.Sort();
        return values[values.Count / 2];
    }
    private async Task<double> EnergyAsync(CancellationToken token) =>
        (await connection.CallServiceAsync(audio, "getFrontMicEnergy", [], token)).GetDouble();

    public async Task<WaveAudio?> CaptureTurnAsync(bool automatic, double noiseFloor, Task finish, Action<double>? level, CancellationToken token)
    {
        await EnsureIdleAsync(token);
        var gate = new EnergyGate(noiseFloor);
        var watch = new Stopwatch();
        try
        {
            recording = true;
            await connection.CallServiceAsync(recorder, "startMicrophonesRecording", ["/home/nao/" + fileName, "wav", 16000, new[] { 0, 0, 1, 0 }], token);
            watch.Start();
            while (watch.Elapsed.TotalSeconds < 25 && !finish.IsCompleted)
            {
                token.ThrowIfCancellationRequested();
                var energy = await EnergyAsync(token);
                level?.Invoke(Math.Clamp(energy / Math.Max(800, gate.Threshold * 3), 0, 1));
                var finished = gate.Update(watch.Elapsed.TotalSeconds, energy);
                if (automatic && finished) break;
                await Task.Delay(120, token);
            }
            await StopRecordingAsync();
            token.ThrowIfCancellationRequested();
            if (automatic && !gate.HasSpeech && !finish.IsCompleted) return null;
            var data = await connection.CallServiceAsync(files, "getFileContents", [fileName], token, 25);
            var wave = WaveAudio.FromRobot(data);
            if (automatic && gate.FirstSpeech.HasValue)
                wave = wave.Trim(Math.Max(0, gate.FirstSpeech.Value - 0.5), Math.Min(wave.DurationSeconds, gate.LastSpeech + 0.6));
            if (wave.DurationSeconds < 0.25 || wave.Rms < 15) return null;
            return wave;
        }
        finally { if (recording) await StopRecordingAsync(); level?.Invoke(0); }
    }
    public async Task<WaveAudio> TestMicrophoneAsync(CancellationToken token)
    {
        await EnsureIdleAsync(token);
        try
        {
            recording = true;
            await connection.CallServiceAsync(recorder, "startMicrophonesRecording", ["/home/nao/" + fileName, "wav", 16000, new[] { 0, 0, 1, 0 }], token);
            await Task.Delay(2200, token);
            await StopRecordingAsync();
            token.ThrowIfCancellationRequested();
            return WaveAudio.FromRobot(await connection.CallServiceAsync(files, "getFileContents", [fileName], token, 25));
        }
        finally { if (recording) await StopRecordingAsync(); }
    }
    private async Task StopRecordingAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        // Check ownership: never stop a recording another app started.
        var ours = await connection.CallServiceAsync(recorder, "_isRecordingThisFile", ["/home/nao/" + fileName], timeout.Token, 5);
        if (ours.ValueKind == JsonValueKind.True) await connection.CallServiceAsync(recorder, "stopMicrophonesRecording", [], timeout.Token, 5);
        recording = false;
    }
    public static string SpokenText(string text) => Regex.Replace(text.Replace('\\', ' ').Replace('^', ' '), @"\s+", " ").Trim();
    public async Task SayAsync(string text, CancellationToken token)
    {
        await EnsureIdleAsync(token);
        var spoken = SpokenText(text);
        if (spoken.Length == 0) return;
        speaking = true;
        try { await connection.CallServiceAsync(tts, "say", [spoken], token, 110); }
        catch { await StopSpeechAsync(); throw; }
        finally { speaking = false; }
    }
    private async Task StopSpeechAsync()
    {
        if (!speaking) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await connection.CallServiceAsync(tts, "stopAll", [], timeout.Token, 4);
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (recording) await StopRecordingAsync();
            if (speaking) await StopSpeechAsync();
            if (!string.IsNullOrWhiteSpace(originalLanguage) && originalLanguage != "English")
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var current = (await connection.CallServiceAsync(tts, "getLanguage", [], timeout.Token)).GetString();
                if (current == "English" && await IsIdleAsync(connection, dialog, memory, timeout.Token))
                    await connection.CallServiceAsync(tts, "setLanguage", [originalLanguage], timeout.Token, 7);
            }
        }
        catch (Exception error) when (error is IOException or RobotRpcException or OperationCanceledException or TimeoutException or System.Net.WebSockets.WebSocketException) { }
        finally { await connection.DisposeAsync(); }
    }
}
