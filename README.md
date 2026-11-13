# NAO English Chat

A Windows app for English conversation with NAO 6. It records through NAO's front microphone, sends spoken turns to OpenAI for transcription, and plays the replies in NAO's English voice. The app shows the transcript and supports typed messages.

## Download and run

Download the Windows x64 ZIP from [Releases](https://github.com/SamiKamara/nao-english-chat/releases/latest), extract it, and run **NAO English Chat.exe**. The package includes the .NET runtime. The PC needs internet access and a local network connection to NAO. NAO needs an installed English voice, and the OpenAI API project needs quota and access to transcription and the Responses API.

1. Stop any active ELIAS lesson or other robot conversation.
2. Enter NAO's IPv4 address. Press the chest button once to hear the address if needed.
3. Paste your OpenAI API key into the masked field and click **Save key**. You only need to do this once on this Windows account.
4. Click **Start conversation**. Stay quiet during calibration, then speak toward NAO when **Listening** appears.
5. Pause for a reply, then wait until **Listening** returns before speaking again. Click **Stop** to end the session.

Click **Send speech** to finish a recording early. Turn off **Hands-free** if you prefer to send each recording manually. A recording is limited to 25 seconds. Hands-free mode continues listening if OpenAI returns an empty transcript.

For typed input, enter a message and click **Send** or press **Ctrl+Enter**. Use **New chat** to reset the conversation. **Test microphone** and **Test voice** exercise the robot connection locally. Submitted conversation turns use your OpenAI API billing.

## Saved key and recordings

Windows Data Protection API encrypts the key for the current Windows account. The app loads it automatically at startup. Use **Forget key** to remove the local copy, or **Save key** to replace it. The encrypted key and preferences live under `%LOCALAPPDATA%\NAOEnglishChat`.

The key goes only to the OpenAI API over HTTPS. When you submit a voice turn, the app sends the recorded audio for transcription. It sends your transcript or typed message with recent conversation history to generate a reply. Conversation history stays in memory until you close the app or click **New chat**.

NAO reuses one WAV file for this app on each PC. The most recent clip may remain on the robot until the next recording overwrites it. Audio and transcripts are kept in memory on the PC.

## Troubleshooting

- If NAO is unreachable, confirm its announced address and that both devices are on a network that allows them to reach each other.
- If the robot is busy, stop the other app's conversation and try **Connect** again.
- If you get an authentication or quota error, check your OpenAI project key, permissions, and billing. **Models...** lets you change the default `gpt-transcribe` and `gpt-6-luna` model IDs.
- If room noise interferes with turn detection, turn off **Hands-free** and use **Send speech**. **Test microphone** checks whether the app can receive audio from NAO.

## Build and test

Run these commands from the repository root on Windows with the .NET 9 SDK.

```powershell
dotnet publish App/NaoOpenAIChat.csproj -c Release -o publish
dotnet run --project Tests/Tests.csproj -c Release
```

The executable is `publish/NAO English Chat.exe`. The tests use a fake robot and a fake OpenAI endpoint. They cover recording, turn detection, key storage, conversation history, and API errors.

For a live hardware check, pass your robot's IPv4 address after `--live`. The test records a short clip on NAO and plays an English sentence over its speaker. The test uses the local network only.

```powershell
dotnet run --project Tests/Tests.csproj -c Release -- --live YOUR_NAO_IPV4_ADDRESS
```
