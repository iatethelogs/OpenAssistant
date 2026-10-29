using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAssistant.Server.Configuration;

namespace OpenAssistant.Server.Audio;

/// <summary>общий интерфейс локальных gpu-сервисов gigaam и qwen tts.</summary>
public sealed class SpeechService(IHttpClientFactory clients, IOptionsMonitor<ServerOptions> settings)
{
    /// <summary>отдаёт русскую речь gigaam через совместимый endpoint распознавания.</summary>
    public async Task<string> TranscribeAsync(byte[] pcm, CancellationToken ct)
    {
        if (pcm.Length == 0) return "";
        using var form = new MultipartFormDataContent();
        using var wave = new ByteArrayContent(WaveWriter.Wrap(pcm, settings.CurrentValue.InputSampleRate));
        wave.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
        form.Add(wave, "file", "speech.wav");
        form.Add(new StringContent(settings.CurrentValue.SttModel), "model");
        using var response = await clients.CreateClient("stt").PostAsync(
            settings.CurrentValue.SttUrl.TrimEnd('/') + "/v1/audio/transcriptions", form, ct);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return json.RootElement.GetProperty("text").GetString()?.Trim() ?? "";
    }

    /// <summary>запрашивает у qwen синтез фразы выбранным голосом и возвращает pcm16 mono.</summary>
    public async Task<byte[]> SynthesizeAsync(string phrase, string? voice, CancellationToken ct)
    {
        var cfg = settings.CurrentValue;
        // контракт локального tts-адаптера: POST /synthesize, json -> audio/wav pcm16 mono.
        using var response = await clients.CreateClient("tts").PostAsJsonAsync(
            cfg.TtsUrl.TrimEnd('/') + "/synthesize",
            new { text = phrase, model = cfg.TtsModel, voice = string.IsNullOrWhiteSpace(voice) ? cfg.TtsVoice : voice, language = cfg.TtsLanguage,
                sample_rate = cfg.OutputSampleRate }, ct);
        response.EnsureSuccessStatusCode();
        byte[] wav = await response.Content.ReadAsByteArrayAsync(ct);
        if (wav.Length > 30 * 1024 * 1024) throw new InvalidDataException("tts вернул слишком много аудио");
        return WavePcmReader.ReadPcm16(wav, cfg.OutputSampleRate);
    }
}
