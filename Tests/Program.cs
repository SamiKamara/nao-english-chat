using System.Net;
using System.Text;
using System.Text.Json;
using NaoOpenAIChat;
using NaoOpenAIChat.Tests;

var passed = 0;
const string TestKey = "local-test-credential-123456789";
const string ClientId = "539de8f4cd8b43c9a6bcf7f8f67703b5";
void Assert(bool value, string message) { if (!value) throw new Exception(message); }
async Task Run(string name, Func<Task> test) { await test(); Console.WriteLine("PASS " + name); passed++; }
async Task Reject<T>(Func<Task> action) where T : Exception
{
    try { await action(); throw new Exception("Expected " + typeof(T).Name); } catch (T) { }
}

await Run("Robot signed audio bytes decode to valid mono WAV", () =>
{
    var source = AudioFixture.Create(); var json = JsonSerializer.SerializeToElement(source.Select(b => (int)unchecked((sbyte)b)).ToArray());
    var clip = WaveAudio.FromRobot(json);
    Assert(clip.Bytes.SequenceEqual(source) && clip.DurationSeconds == 1 && clip.Rms == 400, "Audio bytes or timing changed"); return Task.CompletedTask;
});
await Run("Trimming removes idle audio and preserves valid WAV framing", () =>
{
    var clip = WaveAudio.Parse(AudioFixture.Create()).Trim(0.2, 0.8);
    Assert(Math.Abs(clip.DurationSeconds - 0.6) < 0.001 && clip.Rms == 400, "Trimming changed PCM samples"); return Task.CompletedTask;
});
await Run("Truncated and oversized audio is rejected", async () =>
{
    await Reject<InvalidDataException>(() => Task.FromResult(WaveAudio.Parse(AudioFixture.Create()[..100])));
    await Reject<InvalidDataException>(() => Task.FromResult(WaveAudio.FromRobot(JsonSerializer.SerializeToElement(new int[1_100_001]))));
});
await Run("Out-of-range audio samples and foreign format are rejected", async () =>
{
    var bytes = AudioFixture.Create().Select(b => (int)b).ToArray(); bytes[80] = 300;
    await Reject<InvalidDataException>(() => Task.FromResult(WaveAudio.FromRobot(JsonSerializer.SerializeToElement(bytes))));
    var bad = AudioFixture.Create(); bad[22] = 2;
    await Reject<InvalidDataException>(() => Task.FromResult(WaveAudio.Parse(bad)));
});
await Run("Ambient noise and short spikes do not trigger a speaking turn", () =>
{
    var gate = new EnergyGate(100);
    for (var i = 0; i < 10; i++) gate.Update(i * 0.12, 110);
    gate.Update(1.3, 600); gate.Update(1.42, 100);
    Assert(!gate.HasSpeech, "A short sound triggered speech"); return Task.CompletedTask;
});
await Run("A spoken turn ends after a sustained pause", () =>
{
    var gate = new EnergyGate(100);
    gate.Update(0, 100); gate.Update(0.2, 600); gate.Update(0.32, 600); gate.Update(0.45, 600);
    Assert(gate.HasSpeech && !gate.Update(1.5, 100) && gate.Update(1.75, 100), "Speech/pause timing is wrong"); return Task.CompletedTask;
});
await Run("Key is encrypted and reloads with a fresh store instance", () =>
{
    var path = Path.GetFullPath(Path.Combine("work", "vault-test-" + Guid.NewGuid().ToString("N")));
    try
    {
        var store = new SecretStore(path); store.Save(TestKey);
        Assert(new SecretStore(path).Load() == TestKey, "Saved key did not reload");
        var disk = File.ReadAllBytes(Path.Combine(path, "openai-key.dat"));
        Assert(!Encoding.UTF8.GetString(disk).Contains(TestKey), "The key was written as plaintext");
        store.Forget(); Assert(!new SecretStore(path).Exists, "Forget did not remove the saved key");
    }
    finally { if (File.Exists(Path.Combine(path, "openai-key.dat"))) File.Delete(Path.Combine(path, "openai-key.dat")); if (Directory.Exists(path)) Directory.Delete(path); }
    return Task.CompletedTask;
});
await Run("Settings remember the robot and models without storing a key", () =>
{
    var path = Path.GetFullPath(Path.Combine("work", "settings-test-" + Guid.NewGuid().ToString("N")));
    try
    {
        var original = new ChatSettings { Address = "192.0.2.10", ConversationModel = "gpt-6-luna", ClientId = ClientId, HandsFree = false };
        new SettingsStore(path).Save(original); var loaded = new SettingsStore(path).Load();
        Assert(loaded == original, "Settings did not survive reload");
        Assert(!File.ReadAllText(Path.Combine(path, "settings.json")).Contains(TestKey), "A key reached preferences");
    }
    finally { if (File.Exists(Path.Combine(path, "settings.json"))) File.Delete(Path.Combine(path, "settings.json")); if (Directory.Exists(path)) Directory.Delete(path); }
    return Task.CompletedTask;
});
await Run("Invalid credentials and model IDs are rejected before use", async () =>
{
    await Reject<ArgumentException>(() => { SecretStore.Validate("too short"); return Task.CompletedTask; });
    await Reject<ArgumentException>(() => { OpenAiGateway.ValidateModel("https://other.example/model"); return Task.CompletedTask; });
});

await Run("Transcription uploads WAV only to OpenAI with English hints", async () =>
{
    var handler = new FakeApi(async request =>
    {
        Assert(request.RequestUri!.AbsoluteUri == "https://api.openai.com/v1/audio/transcriptions", "Wrong API destination");
        Assert(request.Headers.Authorization?.Parameter == TestKey, "Authentication is missing");
        var body = await request.Content!.ReadAsStringAsync();
        Assert(body.Contains("gpt-transcribe") && body.Contains("languages[]") && body.Contains("speech.wav") && body.Contains("RIFF"), "The recording or language hint is missing");
        Assert(!body.Contains(TestKey), "The key was placed in the request body");
        return FakeApi.Json(new { text = "What's your name?" });
    });
    using var api = new OpenAiGateway(handler);
    Assert(await api.TranscribeAsync(TestKey, "gpt-transcribe", WaveAudio.Parse(AudioFixture.Create()), CancellationToken.None) == "What's your name?", "Wrong transcript");
});
await Run("Silence does not result in a paid transcription request", async () =>
{
    var handler = new FakeApi(_ => throw new Exception("Silent recording was uploaded")); using var api = new OpenAiGateway(handler);
    await Reject<InvalidDataException>(() => api.TranscribeAsync(TestKey, "gpt-transcribe", WaveAudio.Parse(AudioFixture.Create(amplitude: 0)), CancellationToken.None));
    Assert(handler.Count == 0, "An API request was made for silence");
});
await Run("An empty transcript is a normal result so listening can resume", async () =>
{
    var handler = new FakeApi(_ => Task.FromResult(FakeApi.Json(new { text = "  " })));
    using var api = new OpenAiGateway(handler);
    var result = await api.TranscribeAsync(TestKey, "gpt-transcribe", WaveAudio.Parse(AudioFixture.Create()), CancellationToken.None);
    Assert(result == "" && handler.Count == 1, "An empty transcript should return normally without retrying the API request");
});
await Run("Responses parser skips reasoning and aggregates spoken text", async () =>
{
    using var api = new OpenAiGateway(new FakeApi(async request =>
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        Assert(!body.RootElement.GetProperty("store").GetBoolean() && body.RootElement.GetProperty("input")[0].GetProperty("role").GetString() == "user", "Wrong conversation payload");
        return FakeApi.Json(new { status = "completed", output = new object[] { new { type = "reasoning" }, new { type = "message", content = new[] { new { type = "output_text", text = "I'm Elias." }, new { type = "output_text", text = "What's your name?" } } } } });
    }));
    var answer = await api.ReplyAsync(TestKey, "gpt-6-luna", [new("user", "Hello")], CancellationToken.None);
    Assert(answer == "I'm Elias.\nWhat's your name?", "Wrong text extraction");
});
foreach (var code in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests, HttpStatusCode.NotFound, HttpStatusCode.BadRequest, HttpStatusCode.InternalServerError })
    await Run("API " + (int)code + " errors never expose a key", async () =>
    {
        using var api = new OpenAiGateway(new FakeApi(_ => Task.FromResult(FakeApi.Json(new { error = new { code = "insufficient_quota", message = "Secret was " + TestKey } }, code))));
        try { await api.ReplyAsync(TestKey, "gpt-6-luna", [new("user", "Hello")], CancellationToken.None); throw new Exception("Error was ignored"); }
        catch (InvalidOperationException error) { Assert(!error.Message.Contains(TestKey) && !error.Message.Contains("sk-"), "API error leaked a credential"); }
    });
await Run("Incomplete API replies do not enter conversation memory", async () =>
{
    using var api = new OpenAiGateway(new FakeApi(_ => Task.FromResult(FakeApi.Json(new { status = "incomplete", output = Array.Empty<object>() }))));
    var session = new ChatSession(api);
    await Reject<InvalidOperationException>(() => session.ReplyAsync(TestKey, "gpt-6-luna", "Hi", CancellationToken.None));
    Assert(session.History.Count == 0, "Failed turn was committed");
});
await Run("Conversation memory survives turns and New chat clears it", async () =>
{
    using var api = new OpenAiGateway(new FakeApi(_ => Task.FromResult(FakeApi.Answer("Hello there."))));
    var session = new ChatSession(api);
    for (var i = 0; i < 15; i++) await session.ReplyAsync(TestKey, "gpt-6-luna", "Turn " + i, CancellationToken.None);
    Assert(session.History.Count == 24 && session.History[0].Text == "Turn 3", "Conversation history is not bounded correctly");
    session.Reset(); Assert(session.History.Count == 0, "Reset did not clear history");
});
await Run("Cancellation stops a pending OpenAI request", async () =>
{
    using var api = new OpenAiGateway(new FakeApi(async request => { await Task.Delay(10000, request.Options.GetToken()); return FakeApi.Answer("Unused"); }));
    using var cancellation = new CancellationTokenSource(100);
    await Reject<OperationCanceledException>(() => api.ReplyAsync(TestKey, "gpt-6-luna", [new("user", "Hi")], cancellation.Token));
});

foreach (var scenario in new[] { Scenario.Busy, Scenario.NoEnglish, Scenario.ClosedInput })
    await Run(scenario + " blocks robot use without changing it", async () =>
    {
        await using var fake = new FakeNao(scenario);
        await Reject<InvalidOperationException>(async () => { await using var robot = await RobotSession.ConnectAsync("127.0.0.1", ClientId, CancellationToken.None, fake.Port); });
        Assert(fake.Mutations.IsEmpty, "Robot changed during a rejected connection");
    });
await Run("A stopped lesson's dormant topics do not block the new app", async () =>
{
    await using var fake = new FakeNao(Scenario.DormantTopics); await using var robot = await RobotSession.ConnectAsync("127.0.0.1", ClientId, CancellationToken.None, fake.Port);
    await robot.EnsureIdleAsync(CancellationToken.None);
    Assert(!fake.Mutations.Any(m => m.StartsWith("UNEXPECTED:")), "Dormant lesson check changed another app");
});
await Run("Cancelling a recording stops only the app's own recorder", async () =>
{
    await using var fake = new FakeNao(); await using var robot = await RobotSession.ConnectAsync("127.0.0.1", ClientId, CancellationToken.None, fake.Port);
    using var cancellation = new CancellationTokenSource();
    var capture = robot.CaptureTurnAsync(false, 100, new TaskCompletionSource().Task, null, cancellation.Token);
    await fake.RecordingStarted.Task; cancellation.Cancel();
    await Reject<OperationCanceledException>(async () => await capture);
    Assert(!fake.Recording && fake.Mutations.Count(m => m == "stop-record") == 1, "Recording survived cancellation");
});
await Run("A refused recording never stops another app's recorder", async () =>
{
    await using var fake = new FakeNao(Scenario.StartRefused); await using var robot = await RobotSession.ConnectAsync("127.0.0.1", ClientId, CancellationToken.None, fake.Port);
    await Reject<RobotRpcException>(() => robot.CaptureTurnAsync(false, 100, Task.CompletedTask, null, CancellationToken.None));
    Assert(!fake.Mutations.Contains("stop-record"), "A foreign recording was stopped");
});
await Run("Recorder ownership is checked before stopping", async () =>
{
    await using var fake = new FakeNao(Scenario.StolenRecorder); await using var robot = await RobotSession.ConnectAsync("127.0.0.1", ClientId, CancellationToken.None, fake.Port);
    await robot.CaptureTurnAsync(false, 100, Task.CompletedTask, null, CancellationToken.None);
    Assert(!fake.OtherRecordingStopped, "A replacement recording was stopped");
});
await Run("NAO speech cannot interpret generated control tags", async () =>
{
    await using var fake = new FakeNao(); await using var robot = await RobotSession.ConnectAsync("127.0.0.1", ClientId, CancellationToken.None, fake.Port);
    await robot.SayAsync("Hello \\vol=100\\ ^start(animation) there", CancellationToken.None);
    Assert(fake.Spoken is not null && !fake.Spoken.Contains('\\') && !fake.Spoken.Contains('^'), "Control syntax reached NAO");
});
await Run("Failed robot speech is stopped before another turn", async () =>
{
    await using var fake = new FakeNao(Scenario.SpeechFailure); await using var robot = await RobotSession.ConnectAsync("127.0.0.1", ClientId, CancellationToken.None, fake.Port);
    await Reject<RobotRpcException>(() => robot.SayAsync("Hi", CancellationToken.None));
    Assert(fake.Mutations.Contains("stop-speech"), "Failed speech was left running");
});
await Run("The previous speaking language returns on disconnect", async () =>
{
    await using var fake = new FakeNao(Scenario.Finnish);
    await using (var robot = await RobotSession.ConnectAsync("127.0.0.1", ClientId, CancellationToken.None, fake.Port)) { }
    Assert(fake.Mutations.Where(m => m.StartsWith("language:")).SequenceEqual(new[] { "language:English", "language:Finnish" }), "Original language did not return");
});
await Run("Recorded speech passes through transcription, memory and NAO output", async () =>
{
    await using var fake = new FakeNao(); await using var robot = await RobotSession.ConnectAsync("127.0.0.1", ClientId, CancellationToken.None, fake.Port);
    using var api = new OpenAiGateway(new FakeApi(request => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("transcriptions") ? FakeApi.Json(new { text = "What's your name?" }) : FakeApi.Answer("I'm Elias, a NAO robot."))));
    var clip = await robot.CaptureTurnAsync(false, 100, Task.CompletedTask, null, CancellationToken.None);
    var heard = await api.TranscribeAsync(TestKey, "gpt-transcribe", clip!, CancellationToken.None);
    var session = new ChatSession(api); var answer = await session.ReplyAsync(TestKey, "gpt-6-luna", heard, CancellationToken.None);
    await robot.SayAsync(answer, CancellationToken.None);
    Assert(fake.Spoken == "I'm Elias, a NAO robot." && session.History.Count == 2 && !fake.Recording, "The end-to-end turn failed");
    Assert(!fake.Mutations.Any(m => m.StartsWith("UNEXPECTED:")), "Unexpected robot method was invoked");
});

if (args.Length == 2 && args[0] == "--live")
    await Run("Live NAO microphone capture and English speaker", async () =>
    {
        await using var robot = await RobotSession.ConnectAsync(args[1], ClientId, CancellationToken.None);
        var clip = await robot.TestMicrophoneAsync(CancellationToken.None);
        Console.WriteLine($"Live microphone: {clip.DurationSeconds:F2} s, {clip.SampleRate} Hz, {clip.Channels} channel, RMS {clip.Rms:F0}");
        Assert(clip.DurationSeconds > 1.5, "NAO's recording was incomplete");
        await robot.SayAsync("The English chat application can now hear through my microphone and speak through my speakers.", CancellationToken.None);
    });
Console.WriteLine($"{passed} checks passed.");

internal sealed class FakeApi(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public int Count { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    { Count++; request.Options.Set(new HttpRequestOptionsKey<CancellationToken>("TestCancellation"), token); return respond(request); }
    public static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    public static HttpResponseMessage Answer(string text) => Json(new { status = "completed", output = new[] { new { type = "message", content = new[] { new { type = "output_text", text } } } } });
}
internal static class TestOptions
{
    public static CancellationToken GetToken(this HttpRequestOptions options) => options.TryGetValue(new HttpRequestOptionsKey<CancellationToken>("TestCancellation"), out var token) ? token : CancellationToken.None;
}
