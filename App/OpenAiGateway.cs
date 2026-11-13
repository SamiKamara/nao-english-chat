using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NaoOpenAIChat;

public sealed record ChatMessage(string Role, string Text);

public sealed class OpenAiGateway : IDisposable
{
    private readonly HttpClient http;
    public OpenAiGateway(HttpMessageHandler? handler = null)
    {
        http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        { BaseAddress = new Uri("https://api.openai.com/v1/"), Timeout = TimeSpan.FromSeconds(75) };
    }
    public static void ValidateModel(string model)
    {
        if (!Regex.IsMatch(model, @"^[a-zA-Z0-9][a-zA-Z0-9._:-]{1,100}$"))
            throw new ArgumentException("Enter a valid OpenAI model ID in the Models settings.");
    }
    public async Task<string> TranscribeAsync(string key, string model, WaveAudio audio, CancellationToken token)
    {
        ValidateModel(model);
        if (audio.DurationSeconds is < 0.25 or > 33 || audio.Rms < 15)
            throw new InvalidDataException("The recording is too short or quiet. Please speak toward NAO and try again.");
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(model), "model");
        form.Add(new StringContent("en"), model == "gpt-transcribe" ? "languages[]" : "language");
        form.Add(new StringContent("An English speaker talking to Elias, a NAO robot. Transcribe only the actual spoken words."), "prompt");
        var file = new ByteArrayContent(audio.Bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "speech.wav");
        using var request = Request("audio/transcriptions", key, form);
        using var response = await http.SendAsync(request, token);
        using var json = await ReadAsync(response, token);
        var text = json.RootElement.TryGetProperty("text", out var value) ? value.GetString()?.Trim() ?? "" : "";
        return text.Length <= 4000 ? text : text[..4000];
    }
    public async Task<string> ReplyAsync(string key, string model, IReadOnlyList<ChatMessage> messages, CancellationToken token)
    {
        ValidateModel(model);
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["store"] = false,
            ["max_output_tokens"] = 800,
            ["instructions"] = "You are Elias, a friendly NAO humanoid robot and English conversation partner. " +
                "Use natural, clear English suitable for a language learner. Answer the person's question directly, in one to three short sentences. " +
                "Ask at most one simple follow-up question when appropriate. Gently model correct English without interrupting conversation to grade every mistake. " +
                "If input is unclear, ask a brief clarification rather than guessing. Your name is Elias and your robot hardware is NAO 6. " +
                "You can only speak in this application: do not claim to move, see the user, access live information, or perform physical actions. " +
                "Use plain spoken text, without markdown, sound-effect tags, stage directions or robot control commands.",
            ["input"] = messages.Select(message => new { role = message.Role, content = message.Text }).ToArray()
        };
        if (model.StartsWith("gpt-6", StringComparison.Ordinal)) body["reasoning"] = new { effort = "low" };
        using var request = Request("responses", key, new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
        using var response = await http.SendAsync(request, token);
        using var json = await ReadAsync(response, token);
        var root = json.RootElement;
        if (root.TryGetProperty("status", out var status) && status.GetString() != "completed")
            throw new InvalidOperationException("OpenAI did not finish its reply. Try again, or choose another conversation model.");
        var chunks = new List<string>();
        if (root.TryGetProperty("output", out var output))
            foreach (var item in output.EnumerateArray())
                if (item.TryGetProperty("type", out var kind) && kind.GetString() == "message" && item.TryGetProperty("content", out var content))
                    foreach (var part in content.EnumerateArray())
                        if (part.TryGetProperty("type", out var type) && type.GetString() == "output_text" && part.TryGetProperty("text", out var text))
                            chunks.Add(text.GetString() ?? "");
        var answer = string.Join("\n", chunks).Trim();
        if (answer.Length == 0) throw new InvalidOperationException("OpenAI returned no spoken reply. Please try again.");
        return answer.Length <= 1600 ? answer : answer[..1600];
    }
    private static HttpRequestMessage Request(string path, string key, HttpContent content)
    {
        SecretStore.Validate(key);
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return request;
    }
    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, CancellationToken token)
    {
        // Never include a remote error body: authentication errors can echo part of a key.
        var text = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode)
        {
            var code = "";
            try { using var error = JsonDocument.Parse(text); code = error.RootElement.GetProperty("error").GetProperty("code").GetString() ?? ""; } catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { }
            throw new InvalidOperationException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "OpenAI did not accept the API key. Save a valid project API key and try again.",
                HttpStatusCode.Forbidden => "This API key does not have permission for that OpenAI service or model.",
                HttpStatusCode.NotFound => "That model is unavailable to this API project. Check the model IDs in Models settings.",
                HttpStatusCode.TooManyRequests when code == "insufficient_quota" => "The OpenAI project has no available API quota. Check its billing or credit balance.",
                HttpStatusCode.TooManyRequests => "OpenAI's rate limit was reached. Wait a moment and try again.",
                HttpStatusCode.BadRequest => "OpenAI rejected the request. Check the model IDs and that those models support this API.",
                _ => $"OpenAI could not complete the request (HTTP {(int)response.StatusCode}). Please try again."
            });
        }
        try { return JsonDocument.Parse(text); }
        catch (JsonException) { throw new InvalidDataException("OpenAI returned an unreadable response. Please try again."); }
    }
    public void Dispose() => http.Dispose();
}

public sealed class ChatSession(OpenAiGateway api)
{
    private readonly List<ChatMessage> history = [];
    public IReadOnlyList<ChatMessage> History => history.AsReadOnly();
    public void Reset() => history.Clear();
    public async Task<string> ReplyAsync(string key, string model, string input, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(input)) throw new ArgumentException("Say or type a message first.");
        if (input.Length > 4000) throw new ArgumentException("Please keep each message under 4,000 characters.");
        var pending = history.TakeLast(24).Append(new ChatMessage("user", input)).ToArray();
        var answer = await api.ReplyAsync(key, model, pending, token);
        token.ThrowIfCancellationRequested();
        history.Add(new("user", input)); history.Add(new("assistant", answer));
        if (history.Count > 24) history.RemoveRange(0, history.Count - 24);
        return answer;
    }
}
