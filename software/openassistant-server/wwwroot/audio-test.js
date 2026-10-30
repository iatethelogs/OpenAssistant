/* Shared 5-second microphone test for setup and diagnostics. PCM16, mono, 16 kHz. */
(function () {
  "use strict";
  const sampleRate = 16000;
  function wave(pcm) {
    const bytes = new Uint8Array(44 + pcm.byteLength), data = new DataView(bytes.buffer);
    function ascii(offset, value) {
      for (let i = 0; i < value.length; i++) data.setUint8(offset + i, value.charCodeAt(i));
    }
    ascii(0, "RIFF"); data.setUint32(4, bytes.length - 8, true);
    ascii(8, "WAVE"); ascii(12, "fmt "); data.setUint32(16, 16, true);
    data.setUint16(20, 1, true); data.setUint16(22, 1, true);
    data.setUint32(24, sampleRate, true); data.setUint32(28, sampleRate * 2, true);
    data.setUint16(32, 2, true); data.setUint16(34, 16, true);
    ascii(36, "data"); data.setUint32(40, pcm.byteLength, true);
    bytes.set(pcm, 44);
    return new Blob([bytes], { type: "audio/wav" });
  }
  function stats(pcm) {
    const view = new DataView(pcm.buffer, pcm.byteOffset, pcm.byteLength);
    let energy = 0, peak = 0, clipped = 0;
    const count = pcm.byteLength / 2;
    for (let i = 0; i < count; i++) {
      const v = Math.abs(view.getInt16(i * 2, true));
      energy += v * v;
      peak = Math.max(peak, v);
      if (v >= 32700) clipped++;
    }
    const rms = count ? Math.sqrt(energy / count) / 32768 : 0;
    return {
      duration: count / sampleRate,
      rmsPercent: 100 * rms,
      peakPercent: peak * 100 / 32768,
      clippedPercent: count ? 100 * clipped / count : 0,
      signalStatus: rms < 0.001 || peak < 197 ? "silent" :
        clipped * 100 / count > 2 ? "clipped" : rms < 0.006 ? "quiet" : "normal"
    };
  }
  async function record({ onProgress, seconds = 5 } = {}) {
    if (!navigator.mediaDevices?.getUserMedia || !window.AudioContext && !window.webkitAudioContext) {
      throw Error("Для записи микрофона откройте интерфейс через localhost или HTTPS");
    }
    const stream = await navigator.mediaDevices.getUserMedia({
      audio: { channelCount: { ideal: 1 }, echoCancellation: true, noiseSuppression: true,
        autoGainControl: true }
    });
    let context, source, processor, raf = 0;
    const blocks = [];
    let samples = 0, elapsed = 0;
    const end = () => {
      cancelAnimationFrame(raf);
      if (processor) processor.disconnect();
      if (source) source.disconnect();
      for (const track of stream.getTracks()) track.stop();
    };
    try {
      context = new (window.AudioContext || window.webkitAudioContext)();
      source = context.createMediaStreamSource(stream);
      processor = context.createScriptProcessor(2048, 1, 1);
      processor.onaudioprocess = (event) => {
        const block = new Float32Array(event.inputBuffer.getChannelData(0));
        blocks.push(block);
        samples += block.length;
        event.outputBuffer.getChannelData(0).fill(0);
      };
      source.connect(processor);
      processor.connect(context.destination);
      await context.resume();
      if (context.state !== "running") throw Error("Браузер приостановил захват звука");
      const started = performance.now();
      const report = () => {
        elapsed = Math.min(seconds, (performance.now() - started) / 1000);
        onProgress?.({ elapsed, progress: Math.min(1, elapsed / seconds), sampleSeconds: samples / context.sampleRate });
        if (elapsed < seconds) raf = requestAnimationFrame(report);
      };
      report();
      await new Promise(resolve => setTimeout(resolve, seconds * 1000));
      onProgress?.({ elapsed: seconds, progress: 1, sampleSeconds: samples / context.sampleRate });
      if (samples < context.sampleRate * (seconds - 1)) {
        throw Error("Микрофон не передал достаточное количество данных. Проверьте разрешения и активное устройство записи.");
      }
      const joined = new Float32Array(samples);
      let offset = 0;
      for (const block of blocks) { joined.set(block, offset); offset += block.length; }
      const ratio = context.sampleRate / sampleRate, count = Math.floor(samples / ratio);
      const pcm = new Uint8Array(count * 2), output = new DataView(pcm.buffer);
      for (let i = 0; i < count; i++) {
        const at = i * ratio, lo = Math.floor(at), hi = Math.min(samples - 1, lo + 1);
        const fraction = at - lo;
        const f = Math.max(-1, Math.min(1, joined[lo] * (1 - fraction) + joined[hi] * fraction));
        output.setInt16(i * 2, Math.round(f * 32767), true);
      }
      return { pcm, wav: wave(pcm), ...stats(pcm) };
    } finally {
      end();
      if (context) await context.close().catch(() => {});
    }
  }
  async function transcribe(pcm) {
    if (!pcm?.byteLength) throw Error("Сначала запишите аудио");
    const response = await fetch("/api/control/test/stt", {
      method: "POST",
      headers: { "Content-Type": "application/octet-stream" },
      body: pcm
    });
    const data = await response.json().catch(() => ({}));
    if (!response.ok) throw Error(data.error || "Ошибка распознавания");
    return data;
  }
  function description(result) {
    const status = result.signalStatus;
    let quality = status === "silent" ? "Почти тишина: проверьте микрофон и запись." :
      status === "quiet" ? "Очень тихий сигнал: приблизьтесь к микрофону." :
      status === "clipped" ? "Есть перегрузка микрофона, уменьшите усиление." : "Уровень сигнала нормальный.";
    if (result.empty && status === "normal")
      quality += " При нормальном сигнале GigaAM вернула пустой текст: проверьте язык, шумы и модель.";
    const text = result.text?.trim() || "Речь не распознана";
    return { text, quality,
      metrics: "Время: " + result.totalMs + " мс · RTF: " + result.realtimeFactor +
        " · Аудио: " + result.audioSeconds + " с · RMS: " + result.audioRmsPercent +
        "% · Пик: " + result.audioPeakPercent + "% · Перегрузка: " + result.audioClippedPercent + "%"
    };
  }
  window.OpenAssistantAudioTest = { record, transcribe, wave, stats, description };
})();
