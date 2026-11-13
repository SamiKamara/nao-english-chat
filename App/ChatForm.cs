using System.Drawing;
using System.Net.WebSockets;
using System.Text.RegularExpressions;

namespace NaoOpenAIChat;

public sealed class ChatForm : Form
{
    private static readonly Color Ink = Color.FromArgb(24, 42, 57);
    private static readonly Color Teal = Color.FromArgb(0, 112, 121);
    private readonly SettingsStore settingsStore = new(SettingsStore.DefaultFolder);
    private readonly SecretStore secrets = new(SettingsStore.DefaultFolder);
    private readonly OpenAiGateway api = new();
    private readonly ChatSession conversation;
    private ChatSettings settings;
    private RobotSession? robot;
    private string? connectedAddress;
    private string? apiKey;
    private CancellationTokenSource? operation;
    private TaskCompletionSource? finishTurn;
    private bool closing;
    private readonly TextBox address = new() { Width = 185, PlaceholderText = "Enter NAO's IPv4 address", Font = new Font("Segoe UI", 11), AccessibleName = "NAO IP address" };
    private readonly TextBox keyInput = new() { Width = 285, UseSystemPasswordChar = true, PlaceholderText = "Paste your API key once", AccessibleName = "OpenAI API key (hidden)" };
    private readonly Label keyState = new() { AutoSize = true, Text = "No API key saved", ForeColor = Ink };
    private readonly Label connectionState = new() { AutoSize = true, Text = "Not connected", ForeColor = Ink };
    private readonly Label status = new() { AutoSize = true, Text = "Ready to set up", Font = new Font("Segoe UI Semibold", 15), ForeColor = Ink };
    private readonly Label detail = new() { AutoSize = true, MaximumSize = new Size(900, 0), Text = "Save your API key, then start a conversation. Stop any ELIAS lesson first.", ForeColor = Ink };
    private readonly ProgressBar level = new() { Width = 105, Height = 12, Maximum = 100, AccessibleName = "NAO microphone level" };
    private readonly RichTextBox transcript = new() { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle, DetectUrls = false, Font = new Font("Segoe UI", 11), AccessibleName = "Conversation transcript" };
    private readonly TextBox typed = new() { Dock = DockStyle.Fill, Multiline = true, MaxLength = 4000, Font = new Font("Segoe UI", 11), PlaceholderText = "Or type a message here (Ctrl+Enter to send)", AccessibleName = "Type a message" };
    private readonly Button connect = SmallButton("Connect");
    private readonly Button voice = SmallButton("Test voice");
    private readonly Button microphone = SmallButton("Test microphone");
    private readonly Button saveKey = SmallButton("Save key");
    private readonly Button forgetKey = SmallButton("Forget key");
    private readonly Button models = SmallButton("Models...");
    private readonly Button start = new() { Text = "Start conversation", Width = 185, Height = 44 };
    private readonly Button sendSpeech = new() { Text = "Send speech", Width = 125, Height = 44, Enabled = false };
    private readonly Button stop = new() { Text = "Stop", Width = 85, Height = 44, Enabled = false };
    private readonly Button newChat = new() { Text = "New chat", Width = 95, Height = 44 };
    private readonly Button sendTyped = new() { Text = "Send", Width = 90, Dock = DockStyle.Fill };
    private readonly CheckBox handsFree = new() { Text = "Hands-free", Checked = true, AutoSize = true, Padding = new Padding(8, 12, 0, 0) };

    public ChatForm()
    {
        conversation = new(api);
        settings = settingsStore.Load();
        Text = "NAO English Chat";
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(242, 247, 248);
        ForeColor = Ink;
        ClientSize = new Size(1040, 790);
        MinimumSize = new Size(960, 750);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
        address.Text = settings.Address;
        handsFree.Checked = settings.HandsFree;

        var outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(24) };
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new float[] { 76, 170, 76 }) outer.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        foreach (var height in new float[] { 64, 62, 25 }) outer.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        var header = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        header.Controls.Add(new Label { Text = "NAO English Chat", AutoSize = true, Font = new Font("Segoe UI Semibold", 24), ForeColor = Ink });
        header.Controls.Add(new Label { Text = "Speak naturally with Elias · OpenAI recognition and conversation · version 1.0.1", AutoSize = true, ForeColor = Color.FromArgb(74, 93, 105) });
        outer.Controls.Add(header, 0, 0);

        var setup = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
        setup.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 49));
        setup.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 51));
        var robotCard = Card("NAO connection");
        robotCard.Controls.Add(Row(new Label { Text = "IP address", AutoSize = true, Padding = new Padding(0, 5, 7, 0) }, address, connect));
        robotCard.Controls.Add(connectionState);
        robotCard.Controls.Add(Row(voice, microphone));
        setup.Controls.Add(robotCard, 0, 0);
        var keyCard = Card("OpenAI API key");
        keyCard.Controls.Add(Row(keyInput, saveKey));
        keyCard.Controls.Add(keyState);
        keyCard.Controls.Add(Row(forgetKey, models));
        keyCard.Controls.Add(new Label { Text = "Saved encrypted for this Windows account. Sent only to OpenAI.", AutoSize = true, Font = new Font("Segoe UI", 8.5f), ForeColor = Color.FromArgb(74, 93, 105) });
        setup.Controls.Add(keyCard, 1, 0);
        outer.Controls.Add(setup, 0, 1);

        var statusBox = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        statusBox.Controls.Add(Row(status, new Label { Text = "  MIC", AutoSize = true, Padding = new Padding(10, 7, 0, 0), ForeColor = Color.FromArgb(74, 93, 105) }, level));
        statusBox.Controls.Add(detail);
        outer.Controls.Add(statusBox, 0, 2);
        outer.Controls.Add(transcript, 0, 3);
        var compose = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(0, 8, 0, 0) };
        compose.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); compose.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 98));
        compose.Controls.Add(typed, 0, 0); compose.Controls.Add(sendTyped, 1, 0);
        outer.Controls.Add(compose, 0, 4);
        var actions = Row(start, sendSpeech, stop, newChat, handsFree); actions.Dock = DockStyle.Fill; actions.Padding = new Padding(0, 8, 0, 0);
        start.FlatStyle = FlatStyle.Flat; start.FlatAppearance.BorderSize = 0; start.BackColor = Teal; start.ForeColor = Color.White;
        outer.Controls.Add(actions, 0, 5);
        outer.Controls.Add(new Label { Text = "Uses your OpenAI API billing. Audio is captured only during Listening or a microphone test.", AutoSize = true, Font = new Font("Segoe UI", 9), ForeColor = Color.FromArgb(74, 93, 105) }, 0, 6);
        Controls.Add(outer);

        connect.Click += async (_, _) => await RunOperationAsync(async token =>
        {
            if (robot is not null) { await robot.DisposeAsync(); robot = null; connectedAddress = null; }
            await ConnectAsync(token);
        });
        voice.Click += async (_, _) => await RunOperationAsync(async token => { await ConnectAsync(token); SetStatus("Testing voice", "NAO will say a short English greeting."); await robot!.SayAsync("Hello, I'm Elias. I'm ready for our English conversation.", token); });
        microphone.Click += async (_, _) => await RunOperationAsync(async token =>
        {
            await ConnectAsync(token); SetStatus("Testing microphone", "Speak toward NAO for two seconds. This test stays on your local network.");
            var clip = await robot!.TestMicrophoneAsync(token);
            AddSystem($"Microphone test received {clip.DurationSeconds:F1} seconds of audio from NAO. " + (clip.Rms >= 15 ? "Sound is present. No audio was sent to OpenAI." : "The recording is very quiet. No audio was sent to OpenAI."));
        });
        saveKey.Click += (_, _) => SaveKey();
        forgetKey.Click += (_, _) => ForgetKey();
        models.Click += (_, _) => ShowModels();
        start.Click += async (_, _) => await RunOperationAsync(ConversationAsync);
        sendSpeech.Click += (_, _) => finishTurn?.TrySetResult();
        stop.Click += (_, _) => operation?.Cancel();
        newChat.Click += (_, _) => { conversation.Reset(); transcript.Clear(); AddSystem("New English conversation. Say anything you like, or type below."); };
        sendTyped.Click += async (_, _) => await SendTypedAsync();
        typed.KeyDown += async (_, e) => { if (e.Control && e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await SendTypedAsync(); } };
        FormClosing += OnClosing;
        LoadKey();
        if (apiKey is not null) SetStatus("Ready", "Your saved API key is loaded. Start a conversation when NAO is free of other active lessons.");
        AddSystem((apiKey is null ? "Welcome! Save your API key once, then click Start conversation. " : "Welcome back! Your saved API key is ready. Click Start conversation. ") +
            "Speak toward NAO after Listening appears. In hands-free mode, pause for a reply; Stop ends listening. Turn hands-free off to send each recording yourself.");
    }
    private static Button SmallButton(string text) => new() { Text = text, AutoSize = true, Height = 30, Margin = new Padding(0, 0, 6, 0) };
    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 5) };
        row.Controls.AddRange(controls); return row;
    }
    private static FlowLayoutPanel Card(string title)
    {
        var card = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(12), Margin = new Padding(0, 0, 10, 8), FlowDirection = FlowDirection.TopDown, WrapContents = false };
        card.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font("Segoe UI Semibold", 11), Margin = new Padding(0, 0, 0, 8) });
        return card;
    }
    private void LoadKey()
    {
        try { apiKey = secrets.Load(); keyState.Text = apiKey is null ? "No API key saved" : "API key saved on this PC"; keyState.ForeColor = apiKey is null ? Ink : Teal; }
        catch (Exception error) { apiKey = null; keyState.Text = "Saved key could not be unlocked. Save it again."; AddSystem(SafeError(error)); }
    }
    private void SaveKey()
    {
        try
        {
            var key = keyInput.Text.Trim(); secrets.Save(key); apiKey = key; keyInput.Clear();
            keyState.Text = "API key saved on this PC"; keyState.ForeColor = Teal; SaveSettings();
            SetStatus("API key saved", "It will be loaded automatically next time. Start a conversation when ready.");
        }
        catch (Exception error) { SetStatus("Could not save the key", SafeError(error), true); }
    }
    private void ForgetKey()
    {
        try { secrets.Forget(); apiKey = null; keyInput.Clear(); keyState.Text = "No API key saved"; keyState.ForeColor = Ink; SetStatus("Saved key removed", "Paste and save a key before the next conversation."); }
        catch (Exception error) { SetStatus("Could not remove the key", SafeError(error), true); }
    }
    private void SaveSettings()
    {
        settings = settings with { Address = address.Text.Trim(), HandsFree = handsFree.Checked };
        settingsStore.Save(settings);
    }
    private async Task ConnectAsync(CancellationToken token)
    {
        var host = address.Text.Trim();
        if (robot is not null && host == connectedAddress) { await robot.EnsureIdleAsync(token); return; }
        if (robot is not null) { await robot.DisposeAsync(); robot = null; }
        connectionState.Text = "Connecting...";
        SetStatus("Connecting to NAO", "Checking the microphone and English voice. Stop any ELIAS conversation first.");
        robot = await RobotSession.ConnectAsync(host, settings.ClientId, token);
        connectedAddress = host;
        settings = settings with { Address = host };
        SaveSettings();
        connectionState.Text = "Connected · NAO microphone and English voice"; connectionState.ForeColor = Teal;
    }
    private void RequireKey()
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("Paste your OpenAI API key into the masked field and click Save key first.");
        OpenAiGateway.ValidateModel(settings.ConversationModel); OpenAiGateway.ValidateModel(settings.TranscriptionModel);
    }
    private async Task ConversationAsync(CancellationToken token)
    {
        RequireKey(); await ConnectAsync(token); SaveSettings();
        SetStatus("Getting ready", "Please stay quiet for a moment while the microphone adjusts to the room.");
        var noise = await robot!.CalibrateAsync(token);
        do
        {
            token.ThrowIfCancellationRequested();
            finishTurn = new(TaskCreationOptions.RunContinuationsAsynchronously);
            sendSpeech.Enabled = true;
            SetStatus("Listening", handsFree.Checked ? "Speak toward NAO. Pause to send your turn, or click Send speech. Stop ends the conversation." : "Speak toward NAO, then click Send speech. Each recording is limited to 25 seconds.");
            var clip = await robot.CaptureTurnAsync(handsFree.Checked, noise, finishTurn.Task, value => { if (!IsDisposed) level.Value = (int)(value * 100); }, token);
            sendSpeech.Enabled = false; finishTurn = null;
            if (clip is null)
            {
                if (handsFree.Checked) continue;
                AddSystem("No usable speech was captured. Click Start conversation to try again."); break;
            }
            SetStatus("Understanding your speech", "Sending this spoken turn to OpenAI. The transcript will appear below.");
            var heard = await api.TranscribeAsync(apiKey!, settings.TranscriptionModel, clip, token);
            token.ThrowIfCancellationRequested();
            if (heard.Length == 0)
            {
                AddSystem(handsFree.Checked
                    ? "No words detected. Listening again."
                    : "No words detected. Start another recording or type your message.");
                continue;
            }
            AddMessage("YOU", heard);
            await AnswerAsync(heard, token);
            if (handsFree.Checked) await Task.Delay(700, token);
        } while (handsFree.Checked);
    }
    private async Task AnswerAsync(string text, CancellationToken token)
    {
        SetStatus("Thinking", "Elias is preparing an English reply.");
        var answer = await conversation.ReplyAsync(apiKey!, settings.ConversationModel, text, token);
        AddMessage("ELIAS", answer);
        SetStatus("Speaking", "Wait for NAO to finish. Stop interrupts the current conversation.");
        await robot!.SayAsync(answer, token);
    }
    private async Task SendTypedAsync()
    {
        if (operation is not null || string.IsNullOrWhiteSpace(typed.Text)) return;
        var text = typed.Text.Trim();
        await RunOperationAsync(async token =>
        {
            RequireKey(); await ConnectAsync(token); typed.Clear(); AddMessage("YOU", text); await AnswerAsync(text, token);
        });
    }
    private async Task RunOperationAsync(Func<CancellationToken, Task> action)
    {
        if (operation is not null) return;
        operation = new CancellationTokenSource(); SetBusy(true);
        try { await action(operation.Token); SetStatus("Ready", "Start a conversation, or type a message below."); }
        catch (OperationCanceledException) { SetStatus("Stopped", "NAO is no longer recording. Start again whenever you are ready."); }
        catch (Exception error)
        {
            var message = SafeError(error); SetStatus("Could not complete that turn", message, true); AddSystem(message);
        }
        finally
        {
            finishTurn = null; operation.Dispose(); operation = null;
            if (!IsDisposed) { level.Value = 0; SetBusy(false); }
        }
    }
    private void SetBusy(bool busy)
    {
        foreach (var control in new Control[] { connect, voice, microphone, address, keyInput, saveKey, forgetKey, models, start, newChat, handsFree, sendTyped, typed }) control.Enabled = !busy;
        stop.Enabled = busy; sendSpeech.Enabled = false;
    }
    private void SetStatus(string title, string text, bool error = false)
    {
        if (IsDisposed) return;
        status.Text = title; status.ForeColor = error ? Color.FromArgb(157, 42, 42) : Teal; detail.Text = text;
    }
    private string SafeError(Exception error)
    {
        var message = error is HttpRequestException ? "Could not reach OpenAI. Check this PC's internet connection and try again."
            : error is WebSocketException or IOException && error is not InvalidDataException ? "The robot connection was interrupted. Check NAO's IP and network, then reconnect."
            : error is TimeoutException ? "A service took too long to respond. Stop, check the connection, and try again."
            : error.Message;
        if (!string.IsNullOrEmpty(apiKey)) message = message.Replace(apiKey, "[hidden key]", StringComparison.Ordinal);
        return Regex.Replace(message, @"sk-[A-Za-z0-9_-]{6,}", "[hidden key]");
    }
    private void AddSystem(string text) => AddMessage("APP", text);
    private void AddMessage(string who, string text)
    {
        if (IsDisposed) return;
        transcript.SelectionStart = transcript.TextLength;
        transcript.SelectionFont = new Font("Segoe UI Semibold", 10);
        transcript.SelectionColor = who == "ELIAS" ? Teal : who == "YOU" ? Color.FromArgb(87, 73, 159) : Color.FromArgb(90, 106, 116);
        transcript.AppendText(who + "\n"); transcript.SelectionFont = new Font("Segoe UI", 11); transcript.SelectionColor = Ink;
        transcript.AppendText(text + "\n\n"); transcript.ScrollToCaret();
    }
    private void ShowModels()
    {
        using var dialog = new Form { Text = "OpenAI models", ClientSize = new Size(470, 235), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, Font = Font };
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(20) };
        var chatModel = new TextBox { Width = 420, Text = settings.ConversationModel };
        var transcribeModel = new TextBox { Width = 420, Text = settings.TranscriptionModel };
        layout.Controls.Add(new Label { Text = "Conversation model", AutoSize = true }); layout.Controls.Add(chatModel);
        layout.Controls.Add(new Label { Text = "Speech recognition model", AutoSize = true, Margin = new Padding(0, 12, 0, 0) }); layout.Controls.Add(transcribeModel);
        var save = SmallButton("Save models"); save.Margin = new Padding(0, 18, 0, 0); layout.Controls.Add(save);
        save.Click += (_, _) =>
        {
            try { OpenAiGateway.ValidateModel(chatModel.Text.Trim()); OpenAiGateway.ValidateModel(transcribeModel.Text.Trim()); settings = settings with { ConversationModel = chatModel.Text.Trim(), TranscriptionModel = transcribeModel.Text.Trim() }; SaveSettings(); dialog.DialogResult = DialogResult.OK; }
            catch (Exception error) { MessageBox.Show(dialog, error.Message, "Check model IDs"); }
        };
        dialog.Controls.Add(layout); dialog.AcceptButton = save; dialog.ShowDialog(this);
    }
    private async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (closing) return;
        e.Cancel = true; closing = true; operation?.Cancel(); Enabled = false;
        while (operation is not null) await Task.Delay(80);
        try { SaveSettings(); if (robot is not null) await robot.DisposeAsync(); }
        catch { }
        api.Dispose(); Close();
    }
}
