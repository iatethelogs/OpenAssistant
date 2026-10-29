using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using OpenAssistant.Simulator.Audio;
using OpenAssistant.Simulator.Transport;
using OpenAssistant.Simulator.Wake;

namespace OpenAssistant.Simulator;

/// <summary>интерфейс колонки: локальный wake-word, серверная stt и клиентский плеер.</summary>
public partial class MainWindow : Window
{
    private readonly DeviceSocket _socket = new();
    private readonly MicrophoneCapture _microphone = new();
    private readonly SpeakerPlayer _speaker = new();
    private readonly ExternalWakeDetector _wake = new();
    private readonly VoiceActivityGate _gate = new();
    private readonly AudioUploadQueue _upload;
    private readonly StringBuilder _llmAnswer = new();
    private volatile bool _running;
    private bool _closing;

    /// <summary>связывает микрофон, детектор, очередь сети и состояние интерфейса.</summary>
    public MainWindow()
    {
        InitializeComponent();
        _upload = new AudioUploadQueue(_socket);
        _socket.State += state => Ui(() => StateText.Text = state);
        _socket.AudioReceived += pcm => Ui(() => _speaker.AddSpeech(pcm));
        _socket.ControlReceived += OnControl;
        _upload.Error += message => Ui(() => { _gate.Stop(); Log("передача: " + message); StateText.Text = "Ошибка передачи"; });
        _microphone.Data += OnMicrophone;
        _gate.SendAudio += pcm => _upload.QueueAudio(pcm);
        _gate.Finished += () =>
        {
            _upload.EndPhrase();
            Ui(() => StateText.Text = "Распознавание на сервере");
        };
        _gate.TimedOut += () =>
        {
            _upload.Cancel();
            Ui(() => StateText.Text = "Речь не обнаружена");
        };
        _wake.Detected += (word, score) => Ui(() =>
        {
            Log($"кодовое слово: {word}, уверенность {score:F2}");
            if (WakeEnabled.IsChecked == true && _socket.Connected && !_gate.Capturing)
            {
                _llmAnswer.Clear();
                _speaker.EndSpeech();
                _upload.QueueWake(word);
                _gate.Activate();
                StateText.Text = "Слушаю команду";
            }
        });
        _wake.Diagnostic += message => Ui(() => Log("детектор: " + message.Trim()));
        Closed += async (_, _) => await ShutdownAsync();
    }

    /// <summary>обновляет окно на потоке WPF без блокировки фоновых событий.</summary>
    private void Ui(Action action)
    {
        if (!_closing && !Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(action);
    }

    /// <summary>выводит важные события с ограничением длины журнала.</summary>
    private void Log(string message)
    {
        if (LogText.Text.Length > 64000) LogText.Text = LogText.Text[^32000..];
        LogText.AppendText($"{DateTime.Now:HH:mm:ss} {message}\n");
        LogText.ScrollToEnd();
    }

    /// <summary>обрабатывает результаты сервера и управляющие пакеты медиаплеера.</summary>
    private void OnControl(JsonElement packet) => Ui(() =>
    {
        try
        {
        if (!packet.TryGetProperty("type", out var typeNode)) return;
        var type = typeNode.GetString();
        switch (type)
        {
            case "transcript":
                TranscriptText.Text = packet.TryGetProperty("text", out var transcription)
                    ? transcription.GetString() ?? "" : "";
                _llmAnswer.Clear();
                Log("распознано: " + TranscriptText.Text);
                StateText.Text = "LLM формирует ответ";
                break;
            case "text":
                if (packet.TryGetProperty("delta", out var delta))
                    _llmAnswer.Append(delta.GetString());
                break;
            case "audio_start":
                if (packet.TryGetProperty("sampleRate", out var rate))
                {
                    _speaker.BeginSpeech(rate.GetInt32());
                    StateText.Text = "Произносит ответ";
                }
                break;
            case "audio_end":
                // не сбрасываем буфер: последние pcm-фреймы должны доиграть.
                break;
            case "done":
                if (_llmAnswer.Length > 0) Log("ответ: " + _llmAnswer.ToString());
                _llmAnswer.Clear();
                StateText.Text = "Ожидаю кодовое слово";
                break;
            case "error":
                Log("ошибка сервера: " + (packet.TryGetProperty("message", out var error) ? error.GetString() : "неизвестно"));
                StateText.Text = "Ошибка обработки";
                break;
            case "play_media":
                try
                {
                    var url = packet.GetProperty("url").GetString() ?? "";
                    _speaker.PlayUrl(url);
                    var title = packet.TryGetProperty("title", out var caption) ? caption.GetString() ?? url : url;
                    MusicText.Text = "Плеер: " + title;
                    Log("воспроизведение: " + title);
                }
                catch (Exception ex) { Log("музыкальный поток: " + ex.Message); }
                break;
            case "stop_media":
                _speaker.StopMusic();
                MusicText.Text = "Плеер: остановлен";
                break;
            case "cancelled":
                _upload.Reset();
                _llmAnswer.Clear();
                _speaker.EndSpeech();
                StateText.Text = "Ожидаю кодовое слово";
                break;
            case "listening":
                StateText.Text = "Слушаю команду";
                break;
        }
        }
        catch (Exception ex) { Log("обработка события сервера: " + ex.Message); }
    });

    /// <summary>передаёт PCM локальному детектору и шлюзу записи в правильном порядке.</summary>
    private void OnMicrophone(byte[] pcm)
    {
        if (!_running) return;
        try
        {
            _wake.Feed(pcm);
            _gate.Push(pcm);
        }
        catch (Exception ex) { Ui(() => Log("микрофон: " + ex.Message)); }
    }

    /// <summary>подключается к linux-серверу и запускает локальный захват звука.</summary>
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _running = false;
            _microphone.Stop();
            _wake.Stop();
            _gate.Stop();
            _upload.Reset();
            await _socket.ConnectAsync(ServerAddress.Text.Trim(), DeviceToken.Text.Trim());
            if (!string.IsNullOrWhiteSpace(DetectorExecutable.Text))
                _wake.Start(DetectorExecutable.Text.Trim(), DetectorArguments.Text);
            else if (WakeEnabled.IsChecked == true)
                Log("детектор не задан: доступна ручная запись");
            _microphone.Start();
            _running = true;
            StateText.Text = "Ожидаю кодовое слово";
            Log("микрофон: 16 кГц / PCM16; распознавание: сервер GigaAM");
        }
        catch (Exception ex)
        {
            Log("подключение: " + ex.Message);
            _running = false;
            _microphone.Stop();
            _wake.Stop();
            await _socket.DisconnectAsync();
        }
    }

    /// <summary>останавливает ввод и разрывает соединение с сервером.</summary>
    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        _running = false;
        _microphone.Stop();
        _gate.Stop();
        _wake.Stop();
        _upload.Reset();
        _speaker.EndSpeech();
        await _socket.DisconnectAsync();
        StateText.Text = "Не подключено";
    }

    /// <summary>позволяет тестировать речь без модели слова активации.</summary>
    private void Talk_Click(object sender, RoutedEventArgs e)
    {
        if (!_socket.Connected) { Log("сначала подключитесь к серверу"); return; }
        _speaker.EndSpeech();
        _llmAnswer.Clear();
        _gate.Activate();
        StateText.Text = "Слушаю команду";
    }

    /// <summary>завершает передачу текущей фразы, сохраняя порядок аудиофреймов.</summary>
    private void End_Click(object sender, RoutedEventArgs e)
    {
        if (!_gate.Capturing) return;
        _gate.Stop();
        _upload.EndPhrase();
        StateText.Text = "Распознавание на сервере";
    }

    /// <summary>отменяет серверный ответ и очищает локальное воспроизведение.</summary>
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _gate.Stop();
        _speaker.EndSpeech();
        _upload.Cancel();
        StateText.Text = "Прерываю ответ";
    }

    /// <summary>передаёт текстовый запрос без локального распознавания.</summary>
    private void Text_Click(object sender, RoutedEventArgs e)
    {
        var text = RequestText.Text.Trim();
        if (text.Length == 0 || !_socket.Connected) return;
        RequestText.Clear();
        _gate.Stop();
        _upload.QueueText(text);
        StateText.Text = "LLM формирует ответ";
    }

    /// <summary>enter отправляет текст, не вставляя перенос строки.</summary>
    private void RequestText_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Text_Click(sender, e);
    }

    /// <summary>останавливает только музыкальный плеер локального компьютера.</summary>
    private void StopMusic_Click(object sender, RoutedEventArgs e)
    {
        _speaker.StopMusic();
        MusicText.Text = "Плеер: остановлен";
    }

    /// <summary>управляемо освобождает микрофон, очередь, детектор и сеть.</summary>
    private async Task ShutdownAsync()
    {
        if (_closing) return;
        _closing = true;
        _running = false;
        _microphone.Dispose();
        _gate.Stop();
        _wake.Dispose();
        await _socket.DisposeAsync();
        await _upload.DisposeAsync();
        _speaker.Dispose();
    }
}
