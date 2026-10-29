using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using OpenAssistant.Simulator.Audio;
using OpenAssistant.Simulator.Transport;
using OpenAssistant.Simulator.Wake;

namespace OpenAssistant.Simulator;

/// <summary>интерфейс эмулятора: локальное слово активации, передача речи, серверная расшифровка и воспроизведение.</summary>
public partial class MainWindow : Window
{
    private readonly DeviceSocket _socket = new();
    private readonly MicrophoneCapture _microphone = new();
    private readonly SpeakerPlayer _speaker = new();
    private readonly ExternalWakeDetector _wake = new();
    private readonly VoiceActivityGate _gate = new();
    private readonly SemaphoreSlim _audioQueueLock = new(1, 1);
    private bool _running;
    private bool _audioEnding;

    /// <summary>подключает события микрофона, сервера и детектора к элементам управления.</summary>
    public MainWindow()
    {
        InitializeComponent();
        _socket.State += state => Ui(() => StateText.Text = state);
        _socket.AudioReceived += pcm => Ui(() => _speaker.AddSpeech(pcm));
        _socket.ControlReceived += OnControl;
        _microphone.Data += OnMicrophone;
        _gate.SendAudio += pcm => _ = ForwardAudioAsync(pcm);
        _gate.Finished += () => _ = EndSpeechAsync();
        _wake.Detected += (word, score) =>
        {
            Ui(() => { Log($"кодовое слово: {word}, уверенность {score:F2}"); StateText.Text = "Слушаю команду"; });
            if (WakeEnabled.IsChecked == true && _socket.Connected && !_gate.Capturing) _gate.Activate();
        };
        _wake.Diagnostic += message => Ui(() => Log("детектор: " + message.Trim()));
        Closed += async (_, _) =>
        {
            _running = false;
            _microphone.Dispose(); _wake.Dispose(); _speaker.Dispose();
            await _socket.DisposeAsync();
        };
    }
    /// <summary>передаёт фреймы PCM в правильном порядке; никакой расшифровки на клиенте нет.</summary>
    private async Task ForwardAudioAsync(byte[] data)
    {
        await _audioQueueLock.WaitAsync();
        try { if (_socket.Connected) await _socket.SendPcmAsync(data); }
        catch (Exception e) { Ui(() => Log(e.Message)); }
        finally { _audioQueueLock.Release(); }
    }
    /// <summary>по окончании паузы завершает аудиозапрос после всех бинарных фреймов.</summary>
    private async Task EndSpeechAsync()
    {
        if (_audioEnding) return;
        _audioEnding = true;
        await _audioQueueLock.WaitAsync();
        try {
            if (_socket.Connected) await _socket.SendControlAsync(new { type = "end" });
            Ui(() => StateText.Text = "Распознавание на сервере");
        } catch (Exception e) { Ui(() => Log(e.Message)); }
        finally { _audioQueueLock.Release(); _audioEnding = false; }
    }
    /// <summary>получает события серверной расшифровки, ответа LLM, звука и медиакоманд.</summary>
    private void OnControl(JsonElement message) => Ui(() =>
    {
        if (!message.TryGetProperty("type", out var property)) return;
        string type = property.GetString() ?? "";
        switch (type)
        {
            case "transcript":
                TranscriptText.Text = message.GetProperty("text").GetString() ?? "";
                Log("распознано: " + TranscriptText.Text);
                StateText.Text = "Отвечает LLM"; break;
            case "text": Log("LLM: " + message.GetProperty("delta").GetString()); break;
            case "audio_start": _speaker.BeginSpeech(message.GetProperty("sampleRate").GetInt32()); StateText.Text = "Произносит ответ"; break;
            case "audio_end": break; // очередь воспроизведения не останавливаем: в ней ещё есть звук
            case "done": StateText.Text = "Ожидаю кодовое слово"; break;
            case "error": Log("ошибка: " + message.GetProperty("message").GetString()); break;
            case "play_media":
                try {
                    string url = message.GetProperty("url").GetString() ?? "";
                    _speaker.PlayUrl(url);
                    string title = message.TryGetProperty("title", out var t) ? t.GetString() ?? url : url;
                    MusicText.Text = "Плеер: " + title;
                    Log("медиа: " + title + " | " + url);
                } catch (Exception e) { Log("плеер: " + e.Message); }
                break;
            case "stop_media": _speaker.StopMusic(); MusicText.Text = "Плеер: остановлен"; break;
            case "cancelled": StateText.Text = "Ожидаю кодовое слово"; break;
        }
    });
    /// <summary>всегда передаёт звук детектору, а после активации ещё и серверу.</summary>
    private void OnMicrophone(byte[] pcm)
    {
        if (!_running) return;
        _ = _wake.FeedAsync(pcm);
        _gate.Push(pcm);
    }
    /// <summary>соединяется с linux-сервером и включает микрофон.</summary>
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _socket.ConnectAsync(ServerAddress.Text);
            _microphone.Start(); _running = true;
            if (!string.IsNullOrWhiteSpace(DetectorExecutable.Text))
                _wake.Start(DetectorExecutable.Text, DetectorArguments.Text);
            StateText.Text = "Ожидаю кодовое слово";
            Log("микрофон 16000 Гц PCM16; серверный STT");
        } catch (Exception ex) { Log("подключение: " + ex.Message); }
    }
    /// <summary>отключает микрофон, детектор и websocket.</summary>
    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        _running = false; _gate.Stop(); _microphone.Stop(); _wake.Stop();
        await _socket.DisconnectAsync();
    }
    /// <summary>вручную включает передачу голоса даже без wake-word.</summary>
    private void Talk_Click(object sender, RoutedEventArgs e)
    {
        if (!_socket.Connected) return;
        _gate.Activate(); StateText.Text = "Слушаю команду";
    }
    /// <summary>принудительно завершает фразу вместо ожидания паузы.</summary>
    private async void End_Click(object sender, RoutedEventArgs e)
    {
        if (!_gate.Capturing) return;
        _gate.Stop(); await EndSpeechAsync();
    }
    /// <summary>отправляет команду отмены генерации и останавливает голос.</summary>
    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _gate.Stop();_speaker.EndSpeech();if (_socket.Connected) await _socket.SendControlAsync(new { type="cancel" });
    }
    /// <summary>текстовый тест отправляется непосредственно в LLM без микрофона.</summary>
    private async void Text_Click(object sender, RoutedEventArgs e)
    {
        string value = RequestText.Text.Trim();
        if (value.Length == 0 || !_socket.Connected) return;
        RequestText.Clear();
        await _socket.SendControlAsync(new { type = "text", text = value });
    }
    /// <summary>отправляет тестовую строку по enter.</summary>
    private void RequestText_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) Text_Click(sender, e); }
    /// <summary>останавливает воспроизведение музыки в клиентском декодере.</summary>
    private void StopMusic_Click(object sender, RoutedEventArgs e) { _speaker.StopMusic(); MusicText.Text="Плеер: остановлен"; }
    /// <summary>выполняет обновление интерфейса из фоновых потоков.</summary>
    private void Ui(Action callback) { if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(callback); }
    /// <summary>выводит сообщение в окно журнала и прокручивает к последней строке.</summary>
    private void Log(string message) { LogText.AppendText($"{DateTime.Now:HH:mm:ss} {message}\n"); LogText.ScrollToEnd(); }
}
