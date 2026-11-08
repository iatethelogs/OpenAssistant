using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using OpenAssistant.Simulator.Audio;
using OpenAssistant.Simulator.Display;
using OpenAssistant.Simulator.Transport;
using OpenAssistant.Simulator.Wake;

namespace OpenAssistant.Simulator;

/// <summary>Desktop implementation of the physical column protocol.</summary>
public partial class MainWindow : Window
{
    private readonly DeviceSocket _socket = new();
    private LanDiscoveryClient? _lanDiscovery;
    private readonly MicrophoneCapture _microphone = new();
    private readonly NoiseReducer _noise = new();
    private readonly SpeakerPlayer _speaker = new();
    private WakeWordDetector? _wake;
    private readonly VoiceActivityGate _gate = new();
    private readonly AudioUploadQueue _upload;
    private readonly StringBuilder _llmAnswer = new();
    private volatile bool _running;
    private volatile bool _noiseEnabled = true;
    private volatile bool _bargeInEnabled;
    private volatile bool _ignoreReplyAudio;
    private bool _closing;
    private bool _resumeMusic;
    private int _micTicks;
    private long _chimeUntil;
    private string _musicTitle = "";

    public MainWindow()
    {
        InitializeComponent();
        _upload = new AudioUploadQueue(_socket);
        ApplyPreferences(ClientPreferences.Load());
        try { _lanDiscovery = new LanDiscoveryClient(() => _socket.Connected); }
        catch (SocketException ex)
        {
            Log("UDP 5057 недоступен для поиска симулятора сервером: " + ex.Message);
        }
        SetMode(DisplayMode.Offline, "Не подключено");

        _socket.State += message => Ui(() =>
        {
            if (_closing) return;
            if (message == "Подключено") return;
            StateText.Text = message;
            if (message.Contains("закрыто", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("ошибка", StringComparison.OrdinalIgnoreCase))
            {
                _running = false;
                SetMode(DisplayMode.Error, message);
            }
        });
        _socket.AudioReceived += pcm => Ui(() =>
        {
            if (_ignoreReplyAudio) return; // old answer may still be in the socket queue
            _speaker.AddSpeech(pcm);
            Matrix.SetSpeechLevel(CalculateRms(pcm));
        });
        _socket.ControlReceived += OnControl;
        _upload.Error += message => Ui(() =>
        {
            _gate.Stop();
            SetMode(DisplayMode.Error, "Ошибка передачи");
            Log("Аудио: " + message);
        });
        _microphone.Data += OnMicrophone;
        _gate.SendAudio += pcm => _upload.QueueAudio(pcm);
        _gate.Finished += () =>
        {
            _upload.EndPhrase();
            Ui(() =>
            {
                SetMode(DisplayMode.Thinking, "Распознавание на сервере");
                _ = PlayActivationChimeAsync(completed:true);
            });
        };
        _gate.TimedOut += () =>
        {
            _upload.Cancel();
            Ui(() =>
            {
                SetMode(DisplayMode.Idle, "Речь не обнаружена");
                ResumeMusicIfNeeded();
            });
        };
        // The in-process C# wake detector is created after a model is selected.
        Closed += async (_, _) =>
        {
            try { await ShutdownAsync(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        };
    }

    private void Ui(Action action)
    {
        if (!_closing && !Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(action);
    }
    private void Log(string message)
    {
        if (_closing) return;
        if (LogText.Text.Length > 55000) LogText.Text = LogText.Text[^27000..];
        LogText.AppendText($"{DateTime.Now:HH:mm:ss}  {message}\n");
        LogText.ScrollToEnd();
    }
    private void SetMode(DisplayMode mode, string text)
    {
        Matrix.Mode = mode;
        StateText.Text = text;
        MatrixModeText.Text = mode switch
        {
            DisplayMode.Idle => "Ожидание слова",
            DisplayMode.Listening => "Запись голоса",
            DisplayMode.Thinking => "Обработка запроса",
            DisplayMode.Speaking => "Синтезированный голос",
            DisplayMode.Music => "Воспроизведение",
            DisplayMode.Error => "Ошибка",
            _ => "Отключено"
        };
    }

    private static double CalculateRms(byte[] pcm)
    {
        if (pcm.Length < 2) return 0;
        double energy = 0;
        int count = pcm.Length / 2;
        for (int i = 0; i < count; i++)
        {
            double v = BitConverter.ToInt16(pcm, i*2) / 32768.0;
            energy += v*v;
        }
        return Math.Sqrt(energy/count);
    }
    private async Task PlayActivationChimeAsync(bool completed = false)
    {
        try { await ActivationChime.PlayAsync(completed); }
        catch (Exception ex) { Log("Звуковое подтверждение: " + ex.Message); }
    }

    private void BeginCapture(string? wake = null)
    {
        if (!_socket.Connected) { Log("Нет соединения с сервером"); return; }
        if (_gate.Capturing) return;
        _resumeMusic = _speaker.MusicActive;
        if (_resumeMusic) _speaker.DuckMusic();
        _ignoreReplyAudio = true;
        _speaker.EndSpeech();
        _llmAnswer.Clear();
        AnswerText.Text = "—";
        TranscriptText.Text = "—";
        _upload.Reset();
        if (!string.IsNullOrWhiteSpace(wake)) _upload.QueueWake(wake);
        _gate.Activate();
        SetMode(DisplayMode.Listening, "Слушаю команду");
        // Do not feed the cue coming from our own speaker into STT/VAD.
        Interlocked.Exchange(ref _chimeUntil, Environment.TickCount64 + 260);
        _ = PlayActivationChimeAsync();
    }
    private void ResumeMusicIfNeeded()
    {
        if (!_resumeMusic) return;
        _resumeMusic = false;
        _speaker.ResumeMusic();
        if (_speaker.MusicActive) SetMode(DisplayMode.Music, "Музыка: " + _musicTitle);
    }

    private void OnMicrophone(byte[] pcm)
    {
        if (!_running) return;
        try
        {
            _noise.Enabled = _noiseEnabled;
            var processed = _noise.Process(pcm);
            // A separate detector receives the real PCM from the same microphone,
            // never a second capture device with drifting timestamps.
            // The user can say the wake-word while Martin is speaking; music
            // interruption remains separately configurable.
            if (!_gate.Capturing && (!_speaker.MusicActive || _bargeInEnabled))
                _wake?.Feed(pcm);
            if (_gate.Capturing && Environment.TickCount64 < Interlocked.Read(ref _chimeUntil))
                _gate.Push(new byte[processed.Length]);
            else
                _gate.Push(processed);
            if (Interlocked.Increment(ref _micTicks) % 3 == 0)
            {
                var rms = _noise.LastRms;
                Ui(() =>
                {
                    Matrix.SetMicrophoneLevel(Math.Clamp(rms*9,0,1));
                    MicLevelText.Text = "Микрофон: " + (20*Math.Log10(Math.Max(rms,1e-6))).ToString("0",CultureInfo.InvariantCulture) + " dBFS";
                });
            }
        }
        catch (Exception ex) { Ui(() => Log("Микрофон: " + ex.Message)); }
    }

    private void OnControl(JsonElement packet) => Ui(() =>
    {
        try
        {
            if (!packet.TryGetProperty("type", out var typeNode)) return;
            string? type=typeNode.GetString();
            switch(type)
            {
                case "display_config":
                    if(packet.TryGetProperty("config",out var display))
                    {
                        Matrix.Configure(DisplayProfile.Parse(display));
                        MatrixDimensionsText.Text=Matrix.Dimensions;
                        Log("Матрица: "+Matrix.Dimensions);
                    }
                    break;
                case "display_state":
                    if(packet.TryGetProperty("state",out var stateProperty))
                    {
                        if(Enum.TryParse<DisplayMode>(stateProperty.GetString(),true,out var mode))
                        {
                            // Playback and recording can continue locally after a server
                            // "done"; the client's actual audio state takes precedence.
                            if (mode!=DisplayMode.Idle || (!_speaker.MusicActive && !_gate.Capturing &&
                                !_speaker.SpeechActive))
                                Matrix.Mode=mode;
                        }
                    }
                    break;
                case "profile":
                    Log("Профиль: " + packet.GetProperty("name").GetString());
                    break;
                case "transcript":
                    _ignoreReplyAudio = false;
                    TranscriptText.Text=packet.TryGetProperty("text",out var tr)?tr.GetString()??"": "";
                    _llmAnswer.Clear();
                    SetMode(DisplayMode.Thinking,"LLM формирует ответ");
                    Log("Вы: "+TranscriptText.Text);
                    break;
                case "text":
                    if (_ignoreReplyAudio) break; // late text from the cancelled turn
                    if(packet.TryGetProperty("delta",out var part)&&part.ValueKind==JsonValueKind.String)
                    {
                        _llmAnswer.Append(part.GetString());
                        AnswerText.Text=_llmAnswer.ToString();
                    }
                    break;
                case "audio_start":
                    if (_ignoreReplyAudio) break;
                    int rate=packet.GetProperty("sampleRate").GetInt32();
                    _speaker.BeginSpeech(rate);
                    SetMode(DisplayMode.Speaking,"Голосовой ответ");
                    break;
                case "audio_end":
                    // Keep PCM in the output buffer until it has been played.
                    break;
                case "done":
                    if (_ignoreReplyAudio || _gate.Capturing) break;
                    if (_llmAnswer.Length>0) Log("Ассистент: "+_llmAnswer);
                    _llmAnswer.Clear();
                    _=FinishPlayingSpeechAsync();
                    break;
                case "error":
                    string error=packet.TryGetProperty("message",out var err)?err.GetString()??"Неизвестная ошибка":"Неизвестная ошибка";
                    Log("Сервер: "+error);
                    SetMode(DisplayMode.Error,"Ошибка обработки");
                    ResumeMusicIfNeeded();
                    break;
                case "play_media":
                    if(packet.TryGetProperty("url",out var url))
                    {
                        string target=url.GetString()??"";
                        string title=packet.TryGetProperty("title",out var cap)?cap.GetString()??target:target;
                        _=StartMusicAsync(target,title);
                    }
                    break;
                case "stop_media":
                    _speaker.StopMusic();_resumeMusic=false;
                    MusicText.Text="Музыка не воспроизводится";
                    if(!_gate.Capturing&&!_speaker.SpeechActive)SetMode(DisplayMode.Idle,"Ожидаю слово");
                    break;
                case "interrupted":
                    _llmAnswer.Clear();_speaker.EndSpeech();
                    break;
                case "cancelled":
                    _ignoreReplyAudio = false;
                    _llmAnswer.Clear();_speaker.EndSpeech();
                    ResumeMusicIfNeeded();
                    if(!_speaker.MusicActive)SetMode(DisplayMode.Idle,"Ожидаю слово");
                    break;
                case "listening":
                    SetMode(DisplayMode.Listening,"Слушаю команду");break;
            }
        }
        catch(Exception ex){Log("Событие сервера: "+ex.Message);}
    });

    private async Task StartMusicAsync(string url,string title)
    {
        try
        {
            MusicStreamInfo? media=await _speaker.PlayUrlAsync(url);
            if(media is null||_closing)return;
            _musicTitle=title;_resumeMusic=false;
            MusicText.Text=$"{title} · {media.SampleRate} Hz · {media.Channels} ch · {media.BitsPerSample} bit";
            if(!_gate.Capturing&&!_speaker.SpeechActive)SetMode(DisplayMode.Music,"Музыка: "+title);
            Log("Музыка: "+title);
        }
        catch(Exception ex){if(!_closing)Log("Воспроизведение: "+ex.Message);}
    }
    private async Task FinishPlayingSpeechAsync()
    {
        try
        {
            await _speaker.FinishSpeechAsync();
            if (_closing || _gate.Capturing) return;
            ResumeMusicIfNeeded();
            if (!_speaker.MusicActive) SetMode(DisplayMode.Idle,"Ожидаю слово");
        }
        catch(Exception ex){if(!_closing)Log("Аудиовыход: "+ex.Message);}
    }

    private async Task StopWakeAsync()
    {
        if (_wake is null) return;
        var old=_wake;
        _wake=null;
        await old.DisposeAsync();
    }
    private void StartWake()
    {
        if (WakeEnabled.IsChecked!=true) return;
        string path=WakeModelPath.Text.Trim();
        if (path.Length==0)
        {
            Log("WakeForge-модель не выбрана. Ручное и тестовое пробуждение доступны.");
            return;
        }
        if(!double.TryParse(WakeThreshold.Text.Replace(',','.'),
              NumberStyles.Float,CultureInfo.InvariantCulture,out var threshold) ||
              threshold is < 0.05 or > 0.999)
            throw new ArgumentException("Порог wake-word должен быть от 0.05 до 0.999.");
        var detector=new WakeWordDetector(path,WakeName.Text.Trim(),threshold);
        detector.Detected+=(word,score)=>Ui(()=>
        {
            if (!_socket.Connected || !_running || _gate.Capturing ||
                WakeEnabled.IsChecked!=true ||
                (_speaker.MusicActive && !_bargeInEnabled))return;
            Log($"Wake-word: {word} · {score:0.00}");
            BeginCapture(word);
        });
        detector.Error+=message=>Ui(()=>Log("Wake-word: "+message));
        _wake=detector;
        Log("Локальный TFLite-детектор готов: "+detector.WakeWord);
    }

    private async void FindServer_Click(object sender, RoutedEventArgs e)
    {
        if (FindServerButton.IsEnabled == false) return;
        FindServerButton.IsEnabled = false;
        DiscoverStatus.Text = "Поиск в локальной сети…";
        try
        {
            var servers = await LanDiscoveryClient.FindServersAsync();
            if (servers.Length == 0)
            {
                DiscoverStatus.Text = "Сервер не найден";
                Log("Поиск: сервер не отвечает по UDP 5056. Проверьте одну подсеть и брандмауэр.");
                return;
            }
            string previous = ServerAddress.Text.Trim();
            var selected = servers.FirstOrDefault(x => x.Url == previous) ?? servers[0];
            ServerAddress.Text = selected.Url;
            DiscoverStatus.Text = servers.Length == 1
                ? "Найден: " + selected.Address
                : "Найдено серверов: " + servers.Length + " · выбран " + selected.Address;
            Log("Поиск: " + DiscoverStatus.Text);
            SavePreferences();
            // A found server is selected by its responding IP, then an ordinary
            // WebSocket session is established using the existing client code.
            Connect_Click(sender, e);
        }
        catch (Exception ex)
        {
            DiscoverStatus.Text = "Ошибка поиска";
            Log("Поиск сервера: " + ex.Message);
        }
        finally { FindServerButton.IsEnabled = true; }
    }

    private async void Connect_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            _running=false;_microphone.Stop();await StopWakeAsync();_gate.Stop();_noise.Reset();_upload.Reset();
            var profile=ProfileName.Text.Trim();
            if(profile.Length is < 1 or > 48||!Regex.IsMatch(profile,@"^[\p{L}\p{N} _.-]+$"))
                throw new ArgumentException("Имя профиля должно содержать от 1 до 48 допустимых символов.");
            await _socket.ConnectAsync(ServerAddress.Text.Trim(),DeviceToken.Password.Trim());
            await _socket.SendControlAsync(new {type="profile",name=profile});
            try { StartWake(); }
            catch (Exception ex) { Log("Wake-word недоступен: "+ex.Message); }
            _noiseEnabled=NoiseSuppressionEnabled.IsChecked==true;
            _bargeInEnabled=BargeInOnMusic.IsChecked==true;
            _microphone.Start(CommunicationsEnabled.IsChecked==true);
            CaptureModeText.Text=_microphone.ProcessingMode;
            _running=true;
            SavePreferences();
            SetMode(DisplayMode.Idle,"Ожидаю слово");
            Log("Подключено · PCM16 16 кГц · "+_microphone.ProcessingMode);
        }
        catch(Exception ex)
        {
            _running=false;
            _microphone.Stop();await StopWakeAsync();
            await _socket.DisconnectAsync();
            SetMode(DisplayMode.Error,"Ошибка подключения");
            Log("Подключение: "+ex.Message);
        }
    }
    private async void Disconnect_Click(object sender,RoutedEventArgs e)
    {
        _running=false;_microphone.Stop();_gate.Stop();await StopWakeAsync();_upload.Reset();
        _speaker.EndSpeech();_speaker.StopMusic();_resumeMusic=false;
        await _socket.DisconnectAsync();
        SetMode(DisplayMode.Offline,"Не подключено");
    }
    private void Talk_Click(object sender,RoutedEventArgs e)
    {
        if(!_socket.Connected){Log("Сначала подключитесь к серверу");return;}
        BeginCapture();
    }
    private void SimulateWake_Click(object sender,RoutedEventArgs e)
    {
        if(!_socket.Connected){Log("Сначала подключитесь к серверу");return;}
        if(_gate.Capturing)return;
        string name=WakeName.Text.Trim();
        if(name.Length==0)name="Мартин";
        Log("Эмуляция wake-word: "+name);
        BeginCapture(name);
    }
    private async void ReloadDisplay_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            if(!_socket.Connected)throw new InvalidOperationException("Нет соединения с сервером");
            await _socket.SendControlAsync(new {type="display_get"});
        }
        catch(Exception ex){Log("Дисплей: "+ex.Message);}
    }
    private void End_Click(object sender,RoutedEventArgs e)
    {
        if(!_gate.Capturing)return;
        _gate.Stop();_upload.EndPhrase();
        SetMode(DisplayMode.Thinking,"Распознавание на сервере");
        _ = PlayActivationChimeAsync(completed:true);
    }
    private void Cancel_Click(object sender,RoutedEventArgs e)
    {
        _gate.Stop();_speaker.EndSpeech();_upload.Cancel();
        ResumeMusicIfNeeded();
        if(!_speaker.MusicActive)SetMode(DisplayMode.Idle,"Ожидаю слово");
    }
    private void Text_Click(object sender,RoutedEventArgs e)
    {
        string text=RequestText.Text.Trim();
        if(text.Length==0||!_socket.Connected)return;
        _gate.Stop();_upload.Reset();
        RequestText.Clear();TranscriptText.Text=text;AnswerText.Text="—";_llmAnswer.Clear();
        _upload.QueueText(text);
        SetMode(DisplayMode.Thinking,"LLM формирует ответ");
    }
    private void RequestText_KeyDown(object sender,KeyEventArgs e)
    {
        if(e.Key!=Key.Enter)return;
        e.Handled=true;Text_Click(sender,e);
    }
    private void StopMusic_Click(object sender,RoutedEventArgs e)
    {
        _speaker.StopMusic();_resumeMusic=false;_musicTitle="";
        MusicText.Text="Музыка не воспроизводится";
        if(!_gate.Capturing&&!_speaker.SpeechActive)SetMode(DisplayMode.Idle,"Ожидаю слово");
    }
    private void BrowseWakeModel_Click(object sender,RoutedEventArgs e)
    {
        var dialog=new OpenFileDialog {Filter="WakeForge (*.tflite;*.json)|*.tflite;*.json|Все файлы|*.*"};
        if(dialog.ShowDialog()==true)WakeModelPath.Text=dialog.FileName;
    }
    private void BrowseRgb_Click(object sender,RoutedEventArgs e)
    {
        var dialog=new OpenFileDialog{Filter="RGB888 (32×10)|*.rgb"};
        if(dialog.ShowDialog()!=true)return;
        try{Matrix.LoadAnimation(dialog.FileName);Log("RGB-анимация: "+Path.GetFileName(dialog.FileName));}
        catch(Exception ex){Log("Матрица: "+ex.Message);}
    }
    private void ClearRgb_Click(object sender,RoutedEventArgs e)
    {
        Matrix.ClearAnimation();
    }
    private void ApplyPreferences(ClientPreferences c)
    {
        ServerAddress.Text=c.Server;ProfileName.Text=c.Profile;
        WakeModelPath.Text=c.WakeModel;WakeName.Text=c.WakeWord;
        WakeThreshold.Text=c.WakeThreshold.ToString("0.###",CultureInfo.InvariantCulture);
        WakeEnabled.IsChecked=c.WakeEnabled;
        CommunicationsEnabled.IsChecked=c.CommunicationsDsp;
        NoiseSuppressionEnabled.IsChecked=c.NoiseReduction;
        BargeInOnMusic.IsChecked=c.BargeInOnMusic;
    }
    private void SavePreferences()
    {
        double threshold=double.TryParse(WakeThreshold.Text.Replace(',','.'),NumberStyles.Float,
            CultureInfo.InvariantCulture,out var cutoff)?cutoff:0.87;
        new ClientPreferences {
            Server=ServerAddress.Text.Trim(),Profile=ProfileName.Text.Trim(),
            WakeModel=WakeModelPath.Text.Trim(),WakeWord=WakeName.Text.Trim(),
            WakeThreshold=threshold,WakeEnabled=WakeEnabled.IsChecked==true,
            CommunicationsDsp=CommunicationsEnabled.IsChecked==true,
            NoiseReduction=NoiseSuppressionEnabled.IsChecked==true,
            BargeInOnMusic=BargeInOnMusic.IsChecked==true
        }.Save();
    }
    private void SavePreferences_Click(object sender,RoutedEventArgs e)
    {
        try{SavePreferences();Log("Параметры сохранены");}
        catch(Exception ex){Log("Сохранение: "+ex.Message);}
    }
    private async Task ShutdownAsync()
    {
        if(_closing)return;
        _closing=true;_running=false;
        _microphone.Dispose();_gate.Stop();await StopWakeAsync();
        await _socket.DisconnectAsync();
        await _upload.DisposeAsync();
        await _socket.DisposeAsync();
        if (_lanDiscovery is not null) await _lanDiscovery.DisposeAsync();
        _speaker.Dispose();Matrix.Dispose();
    }
}
