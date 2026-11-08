using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using OpenAssistant.Server.Audio;
using OpenAssistant.Server.Chat;
using OpenAssistant.Server.Configuration;
using OpenAssistant.Server.Integrations;
using OpenAssistant.Server.Monitoring;

namespace OpenAssistant.Server.Transport;

/// <summary>независимый сеанс устройства: речь, llm, tts и музыкальные команды.</summary>
public sealed class AssistantPipeline(
    SpeechService speech, AgentRunner agent, ToolRegistry tools,
    ConversationMemoryStore memory, ConversationLog conversationLog, DeviceRegistry devices,
    YandexMusicService music, DisplaySettingsStore displaySettings, IOptionsMonitor<ServerOptions> settings, ILogger<AssistantPipeline> logger)
{
    /// <summary>принимает аудио и команды клиента до отключения websocket.</summary>
    public async Task RunAsync(WebSocket socket, CancellationToken disconnected, string? remoteAddress = null)
    {
        string sessionId = devices.Connect(remoteAddress);
        using var lifecycle = CancellationTokenSource.CreateLinkedTokenSource(disconnected);
        using var sendLock = new SemaphoreSlim(1, 1);
        using var input = new MemoryStream();
        bool rejectingOversizedAudio = false;
        string? pendingWakeWord = null;
        string profile = "Общий";
        string voice = settings.CurrentValue.TtsVoice;
        CancellationTokenSource? active = null;
        Task? current = null;
        var receiveBuffer = new byte[16384];

        /// <summary>посылает целое сообщение под общим замком; текст и PCM не перемешиваются.</summary>
        async Task SendAsync(ReadOnlyMemory<byte> data, WebSocketMessageType type, CancellationToken ct)
        {
            await sendLock.WaitAsync(ct);
            try
            {
                if (socket.State != WebSocketState.Open) throw new WebSocketException("соединение закрыто");
                await socket.SendAsync(data, type, true, ct);
            }
            finally { sendLock.Release(); }
        }

        /// <summary>отправляет событие протокола в json.</summary>
        Task JsonAsync(object payload, CancellationToken ct) =>
            SendAsync(JsonSerializer.SerializeToUtf8Bytes(payload), WebSocketMessageType.Text, ct);

        // Playback belongs to this connected device. A command is not
        // successful until the client reports media_started for its playbackId.
        var playbackAck = new PlaybackAcknowledgement();

        async Task PlaybackEventAsync(object payload, CancellationToken ct)
        {
            var evt = JsonSerializer.SerializeToElement(payload);
            if (evt.ValueKind == JsonValueKind.Object &&
                evt.TryGetProperty("type", out var kind) &&
                kind.ValueKind == JsonValueKind.String)
            {
                if (kind.GetString() == "play_media" &&
                    evt.TryGetProperty("playbackId", out var idNode) &&
                    idNode.TryGetInt64(out long playbackId))
                {
                    string title = evt.TryGetProperty("title", out var titleNode)
                        ? titleNode.GetString() ?? "Аудио" : "Аудио";
                    playbackAck.Issued(playbackId, title);
                    logger.LogInformation("Playback sent to device {Session}: {PlaybackId} {Title}",
                        sessionId, playbackId, title);
                }
                if (kind.GetString() == "stop_media") playbackAck.Stopped();
            }
            await JsonAsync(payload, ct);
        }
        var player = new MusicPlaybackSession(music, PlaybackEventAsync);

        async Task<PlaybackAcknowledgement.Outcome?> HandleMediaAsync(
            object media, CancellationToken ct)
        {
            var value = JsonSerializer.SerializeToElement(media);
            if (!value.TryGetProperty("type", out var typeNode)) return null;
            string? eventType = typeNode.GetString();
            if (eventType == "music_queue")
            {
                playbackAck.Begin();
                var tracks = value.GetProperty("tracks").Deserialize<YandexTrack[]>() ?? [];
                await player.PlayQueueAsync(tracks, ct);
                return await playbackAck.WaitAsync(ct);
            }
            if (eventType == "music_control")
            {
                string action = value.GetProperty("action").GetString() ?? "";
                bool switchesTrack = action is "next" or "previous";
                if (switchesTrack) playbackAck.Begin();
                await player.ControlAsync(action, ct);
                return switchesTrack ? await playbackAck.WaitAsync(ct) : null;
            }
            if (eventType == "play_media")
            {
                // Radio replaces the Yandex playlist. A separate positive ID
                // makes client acknowledgements work for radio as well.
                await player.ForgetAsync(ct);
                playbackAck.Begin();
                long playbackId = playbackAck.NextRadioId();
                string title = value.TryGetProperty("title", out var t)
                    ? t.GetString() ?? "Радио" : "Радио";
                string url = value.GetProperty("url").GetString() ?? "";
                string codec = value.TryGetProperty("codec", out var codecNode) &&
                    codecNode.ValueKind == JsonValueKind.String ? codecNode.GetString() ?? "" : "";
                await PlaybackEventAsync(new {
                    type = "play_media", source = "radio_browser",
                    title, url, playbackId, codec
                }, ct);
                return await playbackAck.WaitAsync(ct);
            }
            await PlaybackEventAsync(media, ct);
            return null;
        }

        // The LLM only chooses intent; the executor enforces permissions,
        // and the connected audio client confirms effects.
        Task DisplayStateAsync(string state, CancellationToken ct) =>
            JsonAsync(new { type = "display_state", state }, ct);

        Task DisplayConfigAsync(CancellationToken ct) =>
            JsonAsync(new { type = "display_config", config = displaySettings.Current }, ct);

        /// <summary>последовательно отправляет одну готовую аудиофразу.</summary>
        async Task AudioAsync(byte[] pcm, int rate, CancellationToken ct)
        {
            // audio_start, бинарные фреймы и audio_end должны быть одним атомарным блоком.
            // иначе llm-токены могут вклиниться между пакетами звука.
            await sendLock.WaitAsync(ct);
            try
            {
                await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { type = "display_state", state = "speaking" }).AsMemory(), WebSocketMessageType.Text, true, ct);
                await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { type = "audio_start", sampleRate = rate }).AsMemory(), WebSocketMessageType.Text, true, ct);
                foreach (var chunk in AudioChunker.Split(pcm))
                    await socket.SendAsync(chunk, WebSocketMessageType.Binary, true, ct);
                await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { type = "audio_end" }).AsMemory(), WebSocketMessageType.Text, true, ct);
            }
            finally { sendLock.Release(); }
        }

        /// <summary>отменяет текущий ответ, дожидается окончания и освобождает ресурсы.</summary>
        async Task CancelAsync()
        {
            if (active is null) return;
            active.Cancel();
            if (current is not null)
            {
                try { await current; }
                catch (OperationCanceledException) { }
                catch (Exception ex) { logger.LogWarning(ex, "previous operation failed"); }
            }
            active.Dispose();
            active = null;
            current = null;
        }

        /// <summary>формирует ответ; генерация токенов идёт параллельно синтезу фраз.</summary>
        async Task RespondAsync(string text, bool fromAudio, bool synthesize, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                // Every completed voice turn gets a transcript, even when STT
                // hears silence; the client must be able to leave listening mode.
                await JsonAsync(new { type = "transcript", text = "" }, ct);
                await JsonAsync(new { type = "done" }, ct);
                return;
            }
            text = text.Trim();
            await DisplayStateAsync("thinking", ct);
            await JsonAsync(new { type = "transcript", text }, ct);
            conversationLog.Add(fromAudio ? "stt" : "user", text);

            // Intent is selected by the LLM through advertised tools.
            // No keyword dispatch for music or Home Assistant is performed here.
            var recalled = await memory.RecallAsync(profile, text, ct);
            var context = new List<ChatMessage> {
                new("system", settings.CurrentValue.SystemPrompt),
                new("system", profile == "Общий" ?
                    "Профиль собеседника не выбран. Не делай выводов о личности по голосу." :
                    "Выбранный пользователем профиль: " + profile +
                    ". Это имя указано клиентом, а не распознано по голосу.")
            };
            context.AddRange(recalled);
            context.Add(new ChatMessage("user", text));
            var messages = context.ToArray();
            // Synthesizing each sentence independently causes Qwen3-TTS to reset
            // its prosody and often switch intonation between sentences. Collect
            // the whole reply, then synthesize it once whenever it fits.
            // Text still streams immediately; the first audio starts later.
            var complete = new StringBuilder();
            string? lastTool = null;
            object? lastResult = null;
            PlaybackAcknowledgement.Outcome? lastPlayback = null;
            int outputRate = settings.CurrentValue.OutputSampleRate;
            bool fastSpeech = synthesize && settings.CurrentValue.TtsSpeechMode == "fast";
            // Fast mode starts speaking completed phrases while the LLM is still
            // generating. A single consumer preserves phrase playback order.
            var phrases = fastSpeech ? Channel.CreateUnbounded<string>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = true }) : null;
            using var speechStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task? speechWorker = phrases is null ? null : Task.Run(async () =>
            {
                await foreach (var phrase in phrases.Reader.ReadAllAsync(speechStop.Token))
                {
                    foreach (var chunk in SpeechChunks(phrase))
                    {
                        speechStop.Token.ThrowIfCancellationRequested();
                        byte[] pcm = await speech.SynthesizeAsync(chunk, voice, outputRate, speechStop.Token);
                        await AudioAsync(pcm, outputRate, speechStop.Token);
                    }
                }
            }, speechStop.Token);
            var segmenter = fastSpeech ? new SentenceSegmenter() : null;
            try
            {
                await foreach (var delta in agent.GenerateAsync(messages, ct))
                {
                    if (delta.Tool is not null)
                    {
                        lastTool = delta.Tool;
                        lastResult = delta.Result;
                        lastPlayback = null;
                        if (delta.Media is not null)
                        {
                            try { lastPlayback = await HandleMediaAsync(delta.Media, ct); }
                            catch (Exception ex) when (!ct.IsCancellationRequested &&
                                ex is ArgumentException or HttpRequestException or InvalidOperationException or
                                    InvalidDataException or NotSupportedException or TaskCanceledException)
                            {
                                logger.LogWarning(ex, "Unable to initiate playback on {Session}", sessionId);
                                lastPlayback = new PlaybackAcknowledgement.Outcome(false, "", ex.Message);
                            }
                        }
                        logger.LogInformation("Tool {Name} completed for {Session}; media confirmed: {Confirmed}",
                            delta.Tool, sessionId, lastPlayback?.Started);
                        conversationLog.Add("action", "tool: " + delta.Tool +
                            (lastPlayback is null ? "" : lastPlayback.Started
                                ? " (playback started)" : " (playback NOT started)"));
                        await JsonAsync(new { type = "tool_result", name = delta.Tool,
                            result = lastPlayback is null ? delta.Result : new {
                                playbackStarted = lastPlayback.Started,
                                title = lastPlayback.Title, reason = lastPlayback.Reason
                            } }, ct);
                        continue;
                    }
                    if (lastTool is not null || string.IsNullOrEmpty(delta.Text)) continue;
                    complete.Append(delta.Text);
                    await JsonAsync(new { type = "text", delta = delta.Text }, ct);
                    if (phrases is not null)
                        foreach (var phrase in segmenter!.PushAll(delta.Text))
                            phrases.Writer.TryWrite(phrase);
                }

                // The final message for actions is generated from actual service
                // and device results, never from model speculation.
                if (lastTool is not null)
                {
                    string confirmed = ActionResponseFormatter.Format(lastTool,
                        lastResult, lastPlayback);
                    complete.Append(confirmed);
                    await JsonAsync(new { type = "text", delta = confirmed }, ct);
                    if (phrases is not null) phrases.Writer.TryWrite(confirmed);
                }
                if (phrases is not null && segmenter!.Flush() is { } remainder)
                    phrases.Writer.TryWrite(remainder);
            }
            catch
            {
                speechStop.Cancel();
                throw;
            }
            finally
            {
                phrases?.Writer.TryComplete();
                if (speechWorker is not null)
                {
                    try { await speechWorker; }
                    catch (OperationCanceledException) when (speechStop.IsCancellationRequested) { }
                }
            }
            string reply = complete.ToString();
            if (synthesize && !fastSpeech && !string.IsNullOrWhiteSpace(reply))
            {
                // Quality mode retains the same voice contour for the entire
                // reply whenever it fits the backend's per-call text limit.
                foreach (string chunk in SpeechChunks(reply))
                {
                    ct.ThrowIfCancellationRequested();
                    byte[] pcm = await speech.SynthesizeAsync(chunk, voice, outputRate, ct);
                    await AudioAsync(pcm, outputRate, ct);
                }
            }
            ct.ThrowIfCancellationRequested();
            await memory.SaveExchangeAsync(profile, text, reply, ct);
            conversationLog.Add("llm", reply);
            await JsonAsync(new { type = "done" }, ct);
        }

        /// <summary>оборачивает обработку запроса, чтобы ошибка gpu-сервиса не убила websocket.</summary>
        async Task RunRequestAsync(Func<CancellationToken, Task> action, CancellationToken ct)
        {
            try { await action(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "assistant request failed");
                try
                {
                    if (socket.State == WebSocketState.Open)
                    {
                        await DisplayStateAsync("error", disconnected);
                        await JsonAsync(new { type = "error", message = ex.Message }, disconnected);
                        await JsonAsync(new { type = "done" }, disconnected);
                    }
                }
                catch { /* клиент мог отключиться */ }
            }
        }

        try
        {
            await DisplayConfigAsync(disconnected);
            await DisplayStateAsync("idle", disconnected);
            while (socket.State == WebSocketState.Open && !disconnected.IsCancellationRequested)
            {
                using var frame = new MemoryStream();
                WebSocketReceiveResult message;
                do
                {
                    message = await socket.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), disconnected);
                    if (message.MessageType == WebSocketMessageType.Close) break;
                    if (frame.Length + message.Count > 16 * 1024 * 1024)
                        throw new InvalidDataException("websocket пакет больше 16 МБ");
                    frame.Write(receiveBuffer, 0, message.Count);
                } while (!message.EndOfMessage);
                if (message.MessageType == WebSocketMessageType.Close) break;
                devices.Activity(sessionId);

                if (message.MessageType == WebSocketMessageType.Binary)
                {
                    if (rejectingOversizedAudio) continue;
                    if (frame.Length % 2 != 0) { await JsonAsync(new { type = "error", message = "pcm16 требует чётного числа байт" }, disconnected); continue; }
                    if (input.Length + frame.Length > settings.CurrentValue.InputSampleRate * 2L * 60)
                    {
                        input.SetLength(0);
                        rejectingOversizedAudio = true;
                        await JsonAsync(new { type = "error", message = "запись длиннее 60 секунд" }, disconnected);
                        continue;
                    }
                    frame.Position = 0;
                    await frame.CopyToAsync(input, disconnected);
                    continue;
                }

                JsonDocument? document = null;
                try
                {
                    document = JsonDocument.Parse(frame.ToArray());
                    var root = document.RootElement;
                    if (!root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
                        throw new JsonException("не указана команда type");
                    string? kind = typeElement.GetString();
                    if (kind == "display_get")
                    {
                        await DisplayConfigAsync(disconnected);
                        continue;
                    }
                    if (kind == "media_ended")
                    {
                        long playbackId = root.GetProperty("playbackId").GetInt64();
                        await player.EndedAsync(playbackId, disconnected);
                        continue;
                    }
                    if (kind == "media_started")
                    {
                        long playbackId = root.GetProperty("playbackId").GetInt64();
                        playbackAck.Started(playbackId);
                        await player.StartedAsync(playbackId, disconnected);
                        logger.LogInformation("Device {Session} started playback {PlaybackId}",
                            sessionId, playbackId);
                        continue;
                    }
                    if (kind == "media_failed")
                    {
                        long playbackId = root.GetProperty("playbackId").GetInt64();
                        string? reason = root.TryGetProperty("reason", out var reasonNode) &&
                            reasonNode.ValueKind == JsonValueKind.String
                            ? reasonNode.GetString() : null;
                        bool radio = playbackId >= 1_000_000_000_000;
                        playbackAck.Failed(playbackId, reason, terminal: radio ||
                            reason == "autoplay_blocked");
                        if (!radio && reason != "autoplay_blocked")
                            await player.FailedAsync(playbackId, disconnected);
                        logger.LogWarning("Device {Session} failed playback {PlaybackId}: {Reason}",
                            sessionId, playbackId, reason);
                        continue;
                    }
                    if (kind == "media_control")
                    {
                        await player.ControlAsync(root.GetProperty("action").GetString() ?? "", disconnected);
                        continue;
                    }
                    if (kind == "wake")
                    {
                        string activation = root.GetProperty("word").GetString() ?? "";
                        if (activation.Length is < 1 or > 64 ||
                            !System.Text.RegularExpressions.Regex.IsMatch(activation, @"^[\p{L}\p{N} _-]+$"))
                            throw new JsonException("некорректное слово активации");
                        bool interrupted = current is { IsCompleted: false };
                        await CancelAsync();
                        if (interrupted)
                            await JsonAsync(new { type = "interrupted" }, disconnected);
                        input.SetLength(0);
                        rejectingOversizedAudio = false;
                        pendingWakeWord = activation.Trim();
                        await DisplayStateAsync("listening", disconnected);
                        await JsonAsync(new { type = "listening" }, disconnected);
                        continue;
                    }
                    if (kind == "profile")
                    {
                        string selected = ConversationMemoryStore.NormalizeProfile(
                            root.GetProperty("name").GetString());
                        await CancelAsync();
                        profile = selected;
                        await JsonAsync(new { type = "profile", name = profile }, disconnected);
                        continue;
                    }
                    if (kind == "voice")
                    {
                        string chosen = root.GetProperty("voice").GetString() ?? "";
                        if (chosen.Length is < 1 or > 80 || !System.Text.RegularExpressions.Regex.IsMatch(chosen, @"^[\w.-]+$"))
                            throw new JsonException("некорректный голос");
                        voice = chosen;
                        await JsonAsync(new { type = "voice", voice }, disconnected);
                        continue;
                    }
                    if (kind == "cancel")
                    {
                        await CancelAsync();
                        input.SetLength(0);
                        rejectingOversizedAudio = false;
                        pendingWakeWord = null;
                        await DisplayStateAsync("idle", disconnected);
                        await JsonAsync(new { type = "cancelled" }, disconnected);
                        continue;
                    }
                    if (kind is not ("end" or "text" or "tool"))
                        throw new JsonException("неизвестная команда");
                    string? text = kind == "text" ? root.GetProperty("text").GetString() : null;
                    string? toolName = kind == "tool" ? root.GetProperty("name").GetString() : null;
                    JsonElement arguments = kind == "tool" ? root.GetProperty("args").Clone() : default;
                    await CancelAsync();
                    active = CancellationTokenSource.CreateLinkedTokenSource(lifecycle.Token);
                    var token = active.Token;
                    if (kind == "end")
                    {
                        if (rejectingOversizedAudio) pendingWakeWord = null;
                        if (rejectingOversizedAudio)
                        {
                            rejectingOversizedAudio = false;
                            input.SetLength(0);
                            await JsonAsync(new { type = "error", message = "запись отклонена, произнесите запрос заново" }, disconnected);
                            continue;
                        }
                        await DisplayStateAsync("thinking", disconnected);
                        byte[] pcm = input.ToArray();
                        string? activation = pendingWakeWord;
                        pendingWakeWord = null;
                        input.SetLength(0);
                        current = RunRequestAsync(async inner =>
                            await RespondAsync(RemoveActivationWord(await speech.TranscribeAsync(pcm, inner), activation), true, true, inner), token);
                    }
                    else if (kind == "text")
                    {
                        input.SetLength(0);
                        rejectingOversizedAudio = false;
                        pendingWakeWord = null;
                        // Existing voice clients keep speech; web chat requests text-only explicitly.
                        bool audio = !root.TryGetProperty("audio", out var audioOption) ||
                            audioOption.ValueKind != JsonValueKind.False;
                        current = RunRequestAsync(inner => RespondAsync(text ?? "", false, audio, inner), token);
                    }
                    else current = RunRequestAsync(async inner =>
                    {
                        if (string.Equals(toolName, "home_assistant", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("Управление Home Assistant выполняется только через проверенные запросы ассистента");
                        object result = await tools.InvokeAsync(toolName ?? "", arguments, inner);
                        await JsonAsync(new { type = "tool_result", name = toolName, result }, inner);
                    }, token);
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
                {
                    await JsonAsync(new { type = "error", message = ex.Message }, disconnected);
                }
                finally { document?.Dispose(); }
            }
        }
        catch (WebSocketException ex) { logger.LogInformation(ex, "device disconnected"); }
        catch (OperationCanceledException) when (disconnected.IsCancellationRequested) { }
        finally
        {
            devices.Disconnect(sessionId);
            lifecycle.Cancel();
            await CancelAsync();
            if (socket.State == WebSocketState.Open)
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
        }
    }
    /// <summary>
    /// Most replies fit in a single TTS request, preserving one speech contour.
    /// Responses longer than the adapter's limit use the fewest possible chunks.
    /// </summary>
    private static IEnumerable<string> SpeechChunks(string text)
    {
        const int maxLength = 1100; // adapter accepts up to 1200 characters
        string remaining = text.Trim();
        while (remaining.Length > maxLength)
        {
            // Prefer ending a chunk at sentence boundaries, then whitespace.
            int split = remaining.LastIndexOfAny(new[] { '.', '!', '?', '\n' }, maxLength - 1, 600);
            if (split >= 500) split++;
            else
            {
                split = remaining.LastIndexOf(' ', maxLength - 1, 600);
                if (split < 500) split = maxLength;
            }
            string part = remaining[..split].Trim();
            if (part.Length > 0) yield return part;
            remaining = remaining[split..].TrimStart();
        }
        if (remaining.Length > 0) yield return remaining;
    }

    /// <summary>убирает из серверной расшифровки только явно переданное слово активации.</summary>
    private static string RemoveActivationWord(string transcription, string? word)
    {
        if (string.IsNullOrWhiteSpace(word)) return transcription;
        string pattern = @"^\s*" + System.Text.RegularExpressions.Regex.Escape(word.Trim()) +
            @"(?:\s*[,!?.;:]\s*|\s+|$)";
        return System.Text.RegularExpressions.Regex.Replace(transcription, pattern, "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100)).TrimStart();
    }
}
