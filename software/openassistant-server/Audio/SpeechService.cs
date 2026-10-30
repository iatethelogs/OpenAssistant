using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAssistant.Server.Configuration;

namespace OpenAssistant.Server.Audio;

/// <summary>общий интерфейс локальных gpu-сервисов gigaam и qwen tts.</summary>
public sealed class SpeechService(IHttpClientFactory clients, IOptionsMonitor<ServerOptions> settings, VoiceProfileStore voices)
{
    /// <summary>отдаёт русскую речь gigaam через совместимый endpoint распознавания.</summary>
    public async Task<string> TranscribeAsync(byte[] pcm, CancellationToken ct, string? modelOverride = null, int? inputSampleRate = null)
    {
        if (pcm.Length == 0) return "";
        using var form = new MultipartFormDataContent();
        using var wave = new ByteArrayContent(WaveWriter.Wrap(pcm, inputSampleRate ?? settings.CurrentValue.InputSampleRate));
        wave.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
        form.Add(wave, "file", "speech.wav");
        form.Add(new StringContent(string.IsNullOrWhiteSpace(modelOverride) ? settings.CurrentValue.SttModel : modelOverride), "model");
        using var response = await clients.CreateClient("stt").PostAsync(
            settings.CurrentValue.SttUrl.TrimEnd('/') + "/v1/audio/transcriptions", form, ct);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return json.RootElement.GetProperty("text").GetString()?.Trim() ?? "";
    }

    /// <summary>запрашивает у qwen синтез фразы выбранным голосом и возвращает pcm16 mono.</summary>
    public async Task<byte[]> SynthesizeAsync(string phrase, string? voice, int outputRate, CancellationToken ct, string? modelOverride = null)
    {
        var cfg = settings.CurrentValue;
        var voiceProfile = voices.Read();
        // контракт локального tts-адаптера: POST /synthesize, json -> audio/wav pcm16 mono.
        // JsonContent из PostAsJsonAsync может отправляться chunked без Content-Length.
        // Наш локальный Python HTTP-сервер принимает запросы с известной длиной тела.
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new {
            text = phrase,
            model = string.IsNullOrWhiteSpace(modelOverride) ? cfg.TtsModel : modelOverride,
            voice = voiceProfile.Mode == "design" ? "" :
                (string.IsNullOrWhiteSpace(voice) ? voiceProfile.Speaker : voice),
            instruct = voiceProfile.Mode == "design" ? voiceProfile.Description :
                string.IsNullOrWhiteSpace(voiceProfile.Style) ? null : voiceProfile.Style,
            language = cfg.TtsLanguage,
            sample_rate = outputRate,
            generation_mode = voiceProfile.GenerationMode,
            seed = voiceProfile.Seed,
            temperature = voiceProfile.Temperature,
            top_p = voiceProfile.TopP,
            top_k = voiceProfile.TopK,
            repetition_penalty = voiceProfile.RepetitionPenalty
        });
        using var content = new ByteArrayContent(json);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        content.Headers.ContentLength = json.Length;
        using var response = await clients.CreateClient("tts").PostAsync(
            cfg.TtsUrl.TrimEnd('/') + "/synthesize", content, ct);
        if (!response.IsSuccessStatusCode)
        {
            string error = await response.Content.ReadAsStringAsync(ct);
            try
            {
                using var detail = JsonDocument.Parse(error);
                if (detail.RootElement.TryGetProperty("error", out var message) &&
                    message.ValueKind == JsonValueKind.String)
                    error = message.GetString() ?? error;
            }
            catch (JsonException) { }
            if (error.Length > 300) error = error[..300];
            throw new HttpRequestException(
                $"Ошибка Qwen3-TTS (HTTP {(int)response.StatusCode}): {error}",
                null, response.StatusCode);
        }
        byte[] wav = await response.Content.ReadAsByteArrayAsync(ct);
        if (wav.Length > 30 * 1024 * 1024) throw new InvalidDataException("tts вернул слишком много аудио");
        return WavePcmReader.ReadPcm16(wav, outputRate);
    }
}
