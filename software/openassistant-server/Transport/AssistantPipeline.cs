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
    ConversationMemoryStore memory, VerifiedHomeAssistantService homeAssistant,
    AgentToolService skills, ConversationLog conversationLog, DeviceRegistry devices,
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

        // Playback belongs to the connected column, not the global LLM or
        // another user's column. The playbackId makes completion idempotent.
        var player = new MusicPlaybackSession(music, JsonAsync);

        async Task HandleMediaAsync(object media, CancellationToken ct)
        {
            var value = JsonSerializer.SerializeToElement(media);
            if (!value.TryGetProperty("type", out var typeNode)) return;
            string? eventType = typeNode.GetString();
            if (eventType == "music_queue")
            {
                var tracks = value.GetProperty("tracks").Deserialize<YandexTrack[]>() ?? [];
                await player.PlayQueueAsync(tracks, ct);
            }
            else if (eventType == "music_control")
            {
                await player.ControlAsync(value.GetProperty("action").GetString() ?? "", ct);
            }
            else
            {
                // A radio stream replaces the Yandex queue. Never auto-resume
                // old songs when a radio stream ends.
                if (eventType == "play_media") await player.ForgetAsync(ct);
                await JsonAsync(media, ct);
            }
        }

        // Deterministic handling makes short Russian music commands work even
        // with LLMs that don't support OpenAI tool_calls.
        async Task<string?> HandleDirectMusicAsync(string utterance, SkillDefinition[] allowed, CancellationToken ct)
        {
            string phrase = utterance.Trim().TrimEnd('.', '!', '?').ToLowerInvariant();
            bool canControl = allowed.Any(x => x.Name == "music_control" && x.Enabled);
            if (canControl)
            {
                string? action = null;
                if (System.Text.RegularExpressions.Regex.IsMatch(phrase,
                        @"^(?:(?:поставь|поставить|ставь) (?:музыку |трек )?на паузу|пауза|приостанови (?:музыку|трек))$"))
                    action = "pause";
                else if (System.Text.RegularExpressions.Regex.IsMatch(phrase,
                         @"^(?:продолжи|возобнови|сними с паузы|играй дальше)(?: (?:музыку|трек|воспроизведение))?$"))
                    action = "resume";
                else if (System.Text.RegularExpressions.Regex.IsMatch(phrase,
                         @"^(?:следующ(?:ий|ую)(?: трек| песню)?|дальше|переключи(?: на)? следующ(?:ий|ую)(?: трек| песню)?|пропусти(?: трек| песню)?)$"))
                    action = "next";
                else if (System.Text.RegularExpressions.Regex.IsMatch(phrase,
                         @"^(?:предыдущ(?:ий|ую)(?: трек| песню)?|назад|верни предыдущ(?:ий|ую)(?: трек| песню)?)$"))
                    action = "previous";
                else if (System.Text.RegularExpressions.Regex.IsMatch(phrase,
                         @"^(?:останови|выключи|стоп)(?: (?:музыку|трек|воспроизведение))$"))
                    action = "stop";
                if (action is not null)
                {
                    await player.ControlAsync(action, ct);
                    return action switch
                    {
                        "pause" => "Музыка на паузе.",
                        "resume" => "Продолжаю воспроизведение.",
                        "next" => "Следующий трек.",
                        "previous" => "Предыдущий трек.",
                        _ => "Музыка остановлена."
                    };
                }
            }
            bool canPlay = allowed.Any(x => x.Name == "music_play_query" && x.Enabled);
            if (canPlay)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(phrase,
                        @"^(?:включи|поставь|проиграй)(?: мне)? (?:мою музыку|музыку|любимые треки|понравившиеся треки|плейлист мне нравится)$"))
                {
                    var liked = await music.LikedTracksAsync(ct);
                    if (liked.Length == 0) return "В избранном пока нет треков.";
                    await player.PlayQueueAsync(liked, ct);
                    return "Включаю понравившиеся треки.";
                }
                var playlistCommand = System.Text.RegularExpressions.Regex.Match(phrase,
                    @"^(?:включи|поставь|проиграй)(?: мне)? плейлист (.+)$");
                if (playlistCommand.Success)
                {
                    string name = playlistCommand.Groups[1].Value.Trim(' ', '"', '«', '»');
                    var playlists = await music.PlaylistsAsync(ct);
                    var selected = playlists.FirstOrDefault(p =>
                        p.Title.Equals(name, StringComparison.OrdinalIgnoreCase)) ??
                        playlists.FirstOrDefault(p =>
                            p.Title.Contains(name, StringComparison.OrdinalIgnoreCase));
                    if (selected is null) return "Плейлист не найден.";
                    var queue = await music.PlaylistTracksAsync(selected.Kind, ct);
                    if (queue.Length == 0) return "Плейлист пуст.";
                    await player.PlayQueueAsync(queue, ct);
                    return "Включаю плейлист " + selected.Title + ".";
                }
                var match = System.Text.RegularExpressions.Regex.Match(phrase,
                    @"^(?:включи|поставь|проиграй|запусти)(?: мне)? (?:песню|трек|музыку|композицию) (.+)$");
                if (match.Success)
                {
                    var found = await music.SearchAsync(match.Groups[1].Value, ct);
                    if (found.Length == 0) return "Не удалось найти музыку.";
                    await player.PlayQueueAsync(found, ct);
                    return "Включаю " + found[0].Title + ".";
                }
            }
            return null;
        }

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

            var permissions = await skills.ListAsync(ct);
            string? musicResponse = await HandleDirectMusicAsync(text, permissions, ct);
            if (musicResponse is not null)
            {
                await JsonAsync(new { type = "text", delta = musicResponse }, ct);
                conversationLog.Add("action", "music: " + musicResponse);
                await JsonAsync(new { type = "done" }, ct);
                return;
            }
            bool readHa = permissions.Any(s => s.Name == "ha_get_state" && s.Enabled);
            bool writeHa = permissions.Any(s => s.Name == "ha_call_service" && s.Enabled);
            string? verified = await homeAssistant.TryHandleAsync(text, readHa, writeHa, ct);
            if (verified is not null)
            {
                verified = NoEmoji.Strip(verified);
                await JsonAsync(new { type = "text", delta = verified }, ct);
                conversationLog.Add("llm", verified);
                await memory.SaveExchangeAsync(profile, text, verified, ct);
                if (synthesize)
                {
                    byte[] audio = await speech.SynthesizeAsync(verified, voice,
                        settings.CurrentValue.OutputSampleRate, ct);
                    await AudioAsync(audio, settings.CurrentValue.OutputSampleRate, ct);
                }
                await JsonAsync(new { type = "done" }, ct);
                return;
            }
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
                    if (delta.Media is not null) await HandleMediaAsync(delta.Media, ct);
                    if (delta.Tool is not null)
                    {
                        conversationLog.Add("action", "tool: " + delta.Tool);
                        await JsonAsync(new { type = "tool_result", name = delta.Tool, result = delta.Result }, ct);
                    }
                    if (string.IsNullOrEmpty(delta.Text)) continue;
                    complete.Append(delta.Text);
                    await JsonAsync(new { type = "text", delta = delta.Text }, ct);
                    if (phrases is not null)
                        foreach (var phrase in segmenter!.PushAll(delta.Text))
                            phrases.Writer.TryWrite(phrase);
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
                        await player.StartedAsync(root.GetProperty("playbackId").GetInt64(), disconnected);
                        continue;
                    }
                    if (kind == "media_failed")
                    {
                        await player.FailedAsync(root.GetProperty("playbackId").GetInt64(), disconnected);
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
