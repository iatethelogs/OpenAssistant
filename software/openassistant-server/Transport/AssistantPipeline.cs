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
    SpeechService speech, LlmService llm, ToolRegistry tools,
    RadioBrowserService radioBrowser, ConversationLog conversationLog,
    IOptionsMonitor<ServerOptions> settings, ILogger<AssistantPipeline> logger)
{
    /// <summary>принимает аудио и команды клиента до отключения websocket.</summary>
    public async Task RunAsync(WebSocket socket, CancellationToken disconnected)
    {
        using var lifecycle = CancellationTokenSource.CreateLinkedTokenSource(disconnected);
        using var sendLock = new SemaphoreSlim(1, 1);
        using var input = new MemoryStream();
        bool rejectingOversizedAudio = false;
        string? pendingWakeWord = null;
        var history = new List<ChatMessage> { new("system", settings.CurrentValue.SystemPrompt) };
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

        /// <summary>последовательно отправляет одну готовую аудиофразу.</summary>
        async Task AudioAsync(byte[] pcm, CancellationToken ct)
        {
            // audio_start, бинарные фреймы и audio_end должны быть одним атомарным блоком.
            // иначе llm-токены могут вклиниться между пакетами звука.
            await sendLock.WaitAsync(ct);
            try
            {
                await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { type = "audio_start", sampleRate = settings.CurrentValue.OutputSampleRate }).AsMemory(), WebSocketMessageType.Text, true, ct);
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
        async Task RespondAsync(string text, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                await JsonAsync(new { type = "done" }, ct);
                return;
            }
            text = text.Trim();
            await JsonAsync(new { type = "transcript", text }, ct);
            conversationLog.Add("stt", text);

            // для радио клиенту нужен URL, а не гигабайты музыки через сервер.
            var radio = System.Text.RegularExpressions.Regex.Match(text,
                @"^(?:включи|поставь|запусти)\s+радио\s+(.+)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (radio.Success)
            {
                string station = radio.Groups[1].Value.Trim();
                var stations = JsonSerializer.SerializeToElement(await radioBrowser.SearchAsync(station, ct));
                if (stations.GetArrayLength() > 0)
                {
                    var first = stations[0];
                    string url = first.GetProperty("url").GetString() ?? "";
                    string title = first.GetProperty("name").GetString() ?? station;
                    await JsonAsync(new { type = "play_media", url, title, source = "radio_browser" }, ct);
                    string answer = "Включаю радио " + title;
                    await JsonAsync(new { type = "text", delta = answer }, ct);
                    conversationLog.Add("llm", answer);
                    await JsonAsync(new { type = "done" }, ct);
                    return;
                }
            }

            var messages = history.Append(new ChatMessage("user", text)).ToArray();
            var segmenter = new SentenceSegmenter();
            var complete = new StringBuilder();
            using var speechStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var speechToken = speechStop.Token;
            var phrases = Channel.CreateBounded<string>(new BoundedChannelOptions(5)
            {
                SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait
            });

            /// <summary>отдельно синтезирует фразы и передаёт pcm, пока llm генерирует продолжение.</summary>
            async Task SpeakAsync()
            {
                await foreach (string phrase in phrases.Reader.ReadAllAsync(speechToken))
                {
                    byte[] pcm = await speech.SynthesizeAsync(phrase, voice, speechToken);
                    await AudioAsync(pcm, speechToken);
                }
            }

            
            Task speaker = SpeakAsync();
            // если синтезатор упал, разбудим ожидающего запись в заполненную очередь.
            _ = speaker.ContinueWith(task =>
            {
                if (task.IsFaulted) phrases.Writer.TryComplete(task.Exception);
                else if (task.IsCanceled) phrases.Writer.TryComplete(new OperationCanceledException());
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            try
            {
                await foreach (string token in llm.GenerateAsync(messages, ct))
                {
                    if (speaker.IsCompleted && !speaker.IsCompletedSuccessfully) await speaker;
                    complete.Append(token);
                    await JsonAsync(new { type = "text", delta = token }, ct);
                    string? sentence = segmenter.Push(token);
                    if (sentence is not null) await phrases.Writer.WriteAsync(sentence, ct);
                }
                string? tail = segmenter.Flush();
                if (tail is not null) await phrases.Writer.WriteAsync(tail, ct);
                phrases.Writer.TryComplete();
                await speaker;
                ct.ThrowIfCancellationRequested();
                history.Add(new ChatMessage("user", text));
                history.Add(new ChatMessage("assistant", complete.ToString()));
                conversationLog.Add("llm", complete.ToString());
                await JsonAsync(new { type = "done" }, ct);
            }
            finally
            {
                phrases.Writer.TryComplete();
                speechStop.Cancel();
                try { await speaker; }
                catch (OperationCanceledException) when (speechToken.IsCancellationRequested) { }
                catch (Exception ex) { logger.LogDebug(ex, "tts task finished with error"); }
            }
        }

        /// <summary>оборачивает обработку запроса, чтобы ошибка gpu-сервиса не убила websocket.</summary>
        async Task RunRequestAsync(Func<CancellationToken, Task> action, CancellationToken ct)
        {
            try { await action(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "assistant request failed");
                try { if (socket.State == WebSocketState.Open) await JsonAsync(new { type = "error", message = ex.Message }, disconnected); }
                catch { /* клиент мог отключиться */ }
            }
        }

        try
        {
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
                    if (kind == "wake")
                    {
                        string activation = root.GetProperty("word").GetString() ?? "";
                        if (activation.Length is < 1 or > 64 ||
                            !System.Text.RegularExpressions.Regex.IsMatch(activation, @"^[\p{L}\p{N} _-]+$"))
                            throw new JsonException("некорректное слово активации");
                        await CancelAsync();
                        input.SetLength(0);
                        rejectingOversizedAudio = false;
                        pendingWakeWord = activation.Trim();
                        await JsonAsync(new { type = "listening" }, disconnected);
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
                        byte[] pcm = input.ToArray();
                        string? activation = pendingWakeWord;
                        pendingWakeWord = null;
                        input.SetLength(0);
                        current = RunRequestAsync(async inner =>
                            await RespondAsync(RemoveActivationWord(await speech.TranscribeAsync(pcm, inner), activation), inner), token);
                    }
                    else if (kind == "text") { input.SetLength(0); rejectingOversizedAudio = false; pendingWakeWord = null; current = RunRequestAsync(inner => RespondAsync(text ?? "", inner), token); }
                    else current = RunRequestAsync(async inner =>
                    {
                        object result = await tools.InvokeAsync(toolName ?? "", arguments, inner);
                        await JsonAsync(new { type = "tool_result", name = toolName, result }, inner);
                    }, token);
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
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
            lifecycle.Cancel();
            await CancelAsync();
            if (socket.State == WebSocketState.Open)
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
        }
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
