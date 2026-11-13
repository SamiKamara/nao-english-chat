namespace NaoOpenAIChat;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var singleton = new Mutex(true, @"Local\NAOEnglishChat", out var first);
        ApplicationConfiguration.Initialize();
        if (!first) { MessageBox.Show("NAO English Chat is already open.", "NAO English Chat"); return; }
        Application.Run(new ChatForm());
    }
}
