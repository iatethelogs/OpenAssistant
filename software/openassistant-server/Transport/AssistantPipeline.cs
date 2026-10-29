using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAssistant.Server.Audio;
using OpenAssistant.Server.Chat;
using OpenAssistant.Server.Configuration;
using OpenAssistant.Server.Integrations;

namespace OpenAssistant.Server.Transport;

/// <summary>одна сессия колонки: входящее аудио, диалог, tts и поток обратно.</summary>
public sealed class AssistantPipeline(SpeechService speech, LlmService llm,
    ToolRegistry tools, IOptions<ServerOptions> settings, ILogger<AssistantPipeline> logger)
{
    /// <summary>обслуживает websocket до отключения; новый запрос отменяет предыдущий.</summary>
    public async Task RunAsync(WebSocket socket, CancellationToken disconnected)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(disconnected);
        using var writeLock = new SemaphoreSlim(1, 1);
        using var input = new MemoryStream();
        string selectedVoice = settings.Value.TtsVoice;
        var history = new List<ChatMessage> { new("system", settings.Value.SystemPrompt) };
        CancellationTokenSource? active = null;
        Task? running = null;
        var buffer = new byte[16384];

        /// <summary>отправляет json, сохраняя порядок сообщений в одном сокете.</summary>
        async Task SendJsonAsync(object value, CancellationToken ct)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
            await writeLock.WaitAsync(ct);
            try { await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, ct); }
            finally { writeLock.Release(); }
        }

        /// <summary>отправляет куски готового аудио, не смешивая фреймы разных задач.</summary>
        async Task SendAudioAsync(byte[] pcm, CancellationToken ct)
        {
            await SendJsonAsync(new { type = "audio_start", sampleRate = settings.Value.OutputSampleRate }, ct);
            foreach (var chunk in AudioChunker.Split(pcm))
            {
                await writeLock.WaitAsync(ct);
                try { await socket.SendAsync(chunk, WebSocketMessageType.Binary, true, ct); }
                finally { writeLock.Release(); }
            }
            await SendJsonAsync(new { type = "audio_end" }, ct);
        }

        /// <summary>останавливает синтез и генерацию; старый ответ больше не должен звучать.</summary>
        async Task CancelAsync()
        {
            if (active is null) return;
            active.Cancel();
            if (running is not null)
                try { await running; } catch (OperationCanceledException) { }
                  catch (Exception ex) { logger.LogWarning(ex, "previous request failed"); }
            active.Dispose();
            active = null;
            running = null;
        }

        /// <summary>запускает ответ по тексту и произносит его предложение за предложением.</summary>
        async Task RespondAsync(string text, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(text)) { await SendJsonAsync(new { type = "done" }, ct); return; }
            history.Add(new ChatMessage("user", text));
            await SendJsonAsync(new { type = "transcript", text }, ct);
            var segmenter = new SentenceSegmenter();
            var complete = new StringBuilder();
            await foreach (var token in llm.GenerateAsync(history, ct))
            {
                complete.Append(token);
                await SendJsonAsync(new { type = "text", delta = token }, ct);
                var phrase = segmenter.Push(token);
                if (phrase is not null) await SendAudioAsync(await speech.SynthesizeAsync(phrase, selectedVoice, ct), ct);
            }
            var tail = segmenter.Flush();
            if (tail is not null) await SendAudioAsync(await speech.SynthesizeAsync(tail, selectedVoice, ct), ct);
            history.Add(new ChatMessage("assistant", complete.ToString()));
            await SendJsonAsync(new { type = "done" }, ct);
        }

        /// <summary>обрабатывает команду; ошибки возвращает клиенту, а не роняет весь сервер.</summary>
        async Task HandleRequestAsync(Func<CancellationToken, Task> handler, CancellationToken ct)
        {
            try { await handler(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "request failed");
                if (socket.State == WebSocketState.Open)
                    try { await SendJsonAsync(new { type = "error", message = ex.Message }, disconnected); }
                    catch { /* соединение могло уже умереть */ }
            }
        }

        try
        {
            while (socket.State == WebSocketState.Open && !disconnected.IsCancellationRequested)
            {
                using var frame = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), disconnected);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    frame.Write(buffer, 0, result.Count);
                    if (frame.Length > 16 * 1024 * 1024) throw new InvalidOperationException("слишком большое websocket-сообщение");
                } while (!result.EndOfMessage);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    if (input.Length + frame.Length > settings.Value.InputSampleRate * 2 * 60)
                        throw new InvalidOperationException("аудиозапись длиннее 60 секунд");
                    frame.Position = 0;
                    await frame.CopyToAsync(input, disconnected);
                    continue;
                }
                using var command = JsonDocument.Parse(frame.ToArray());
                var root = command.RootElement;
                var kind = root.GetProperty("type").GetString();
                if (kind == "voice")
                {
                    string voice = root.GetProperty("voice").GetString() ?? "";
                    if (voice.Length is < 1 or > 80 || !System.Text.RegularExpressions.Regex.IsMatch(voice, "^[\\w.-]+$"))
                        await SendJsonAsync(new { type = "error", message = "некорректный голос" }, disconnected);
                    else { selectedVoice = voice; await SendJsonAsync(new { type = "voice", voice }, disconnected); }
                    continue;
                }
                if (kind == "cancel")
                {
                    await CancelAsync();
                    input.SetLength(0);
                    await SendJsonAsync(new { type = "cancelled" }, disconnected);
                    continue;
                }
                if (kind is not ("end" or "text" or "tool"))
                {
                    await SendJsonAsync(new { type = "error", message = "неизвестная команда" }, disconnected);
                    continue;
                }
                await CancelAsync();
                active = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                var token = active.Token;
                if (kind == "end")
                {
                    var pcm = input.ToArray();
                    input.SetLength(0);
                    running = HandleRequestAsync(async ct =>
                    {
                        string transcription = await speech.TranscribeAsync(pcm, ct);
                        await RespondAsync(transcription, ct);
                    }, token);
                }
                else if (kind == "text")
                {
                    string text = root.GetProperty("text").GetString() ?? "";
                    running = HandleRequestAsync(ct => RespondAsync(text, ct), token);
                }
                else
                {
                    string name = root.GetProperty("name").GetString() ?? "";
                    var args = root.GetProperty("args").Clone();
                    running = HandleRequestAsync(async ct =>
                    {
                        object answer = await tools.InvokeAsync(name, args, ct);
                        await SendJsonAsync(new { type = "tool_result", name, result = answer }, ct);
                    }, token);
                }
            }
        }
        finally
        {
            lifetime.Cancel();
            await CancelAsync();
            if (socket.State == WebSocketState.Open)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
    }
}
