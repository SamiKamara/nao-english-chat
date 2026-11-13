using System.Text.Json;

namespace NaoOpenAIChat;

public sealed record ChatSettings
{
    public string Address { get; init; } = "";
    public string ConversationModel { get; init; } = "gpt-6-luna";
    public string TranscriptionModel { get; init; } = "gpt-transcribe";
    public bool HandsFree { get; init; } = true;
    public string ClientId { get; init; } = Guid.NewGuid().ToString("N");
}

public sealed class SettingsStore(string folder)
{
    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NAOEnglishChat");
    public ChatSettings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<ChatSettings>(File.ReadAllText(Path.Combine(folder, "settings.json"))) ?? new();
            return Guid.TryParseExact(settings.ClientId, "N", out _) ? settings : settings with { ClientId = Guid.NewGuid().ToString("N") };
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save(ChatSettings settings)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "settings.json"), JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }
}
