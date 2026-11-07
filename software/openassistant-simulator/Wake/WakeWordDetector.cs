using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using Emgu.TF.Lite;

namespace OpenAssistant.Simulator.Wake;

/// <summary>
/// In-process streaming MicroWakeWord detector. TFLite execution is supplied by
/// Emgu's Windows NuGet runtime, not by a Rust/Python/external executable.
/// </summary>
public sealed class WakeWordDetector : IAsyncDisposable
{
    private readonly Channel<byte[]> _samples = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(32) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _stop = new();
    private readonly WakeAudioFrontend _frontend = new();
    private readonly List<sbyte> _features = new(400);
    private readonly Queue<double> _probabilities = new();
    private readonly FlatBufferModel _model;
    private readonly Interpreter _interpreter;
    private readonly int _stride, _windowSize;
    private readonly string _word;
    private readonly double _threshold;
    private DateTime _lastDetection = DateTime.MinValue;
    private readonly Task _worker;
    public event Action<string,double>? Detected;
    public event Action<string>? Error;
    public string WakeWord => _word;

    public WakeWordDetector(string path,string fallbackName,double fallbackThreshold)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Файл модели не найден",path);
        if (!double.IsFinite(fallbackThreshold) || fallbackThreshold is <= 0 or > 1)
            throw new ArgumentException("Порог должен быть в диапазоне 0–1");
        int windowSize = 5;
        if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            // Accept both a simple project-specific config and the standard
            // micro_wake_word format used by MicroWakeWord/Home Assistant.
            JsonElement modelConfig = root;
            JsonElement frontendConfig = root;
            if (root.TryGetProperty("micro_wake_word", out var micro))
            {
                frontendConfig = micro;
                if (!micro.TryGetProperty("models", out var models) ||
                    models.ValueKind != JsonValueKind.Array || models.GetArrayLength() == 0)
                    throw new InvalidDataException("В micro_wake_word.models отсутствует модель");
                modelConfig = models[0];
            }
            else if (root.TryGetProperty("micro", out var oldConfig))
            {
                frontendConfig = oldConfig;
            }

            string? reference = null;
            if (modelConfig.TryGetProperty("model_path", out var modelPath) &&
                modelPath.ValueKind == JsonValueKind.String)
                reference = modelPath.GetString();
            else if (modelConfig.TryGetProperty("model", out var simpleModel) &&
                     simpleModel.ValueKind == JsonValueKind.String)
                reference = simpleModel.GetString();
            if (string.IsNullOrWhiteSpace(reference))
                throw new InvalidDataException("В конфигурации нет model_path или model");
            if (Uri.TryCreate(reference, UriKind.Absolute, out _))
                throw new InvalidDataException("Нужен локальный файл модели, а не URL");

            path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, reference));
            if (!File.Exists(path)) throw new FileNotFoundException("Файл модели из конфигурации не найден", path);

            if (modelConfig.TryGetProperty("wake_word", out var word) &&
                word.ValueKind == JsonValueKind.String)
                fallbackName = word.GetString() ?? fallbackName;
            else if (root.TryGetProperty("wake_word", out var rootWord) &&
                     rootWord.ValueKind == JsonValueKind.String)
                fallbackName = rootWord.GetString() ?? fallbackName;

            if (modelConfig.TryGetProperty("probability_cutoff", out var cutoff) &&
                cutoff.TryGetDouble(out var parsedThreshold))
                fallbackThreshold = parsedThreshold;
            else if (frontendConfig.TryGetProperty("probability_cutoff", out cutoff) &&
                     cutoff.TryGetDouble(out parsedThreshold))
                fallbackThreshold = parsedThreshold;

            if (modelConfig.TryGetProperty("sliding_window_size", out var window) &&
                window.TryGetInt32(out var parsedWindow))
                windowSize = parsedWindow;
            else if (frontendConfig.TryGetProperty("sliding_window_size", out window) &&
                     window.TryGetInt32(out parsedWindow))
                windowSize = parsedWindow;

            if (frontendConfig.TryGetProperty("features_step_size", out var step) &&
                step.TryGetInt32(out var stepSize) && stepSize != 10)
                throw new InvalidDataException("Сейчас поддерживается только features_step_size=10 мс");
            if (frontendConfig.TryGetProperty("feature_step_size", out step) &&
                step.TryGetInt32(out stepSize) && stepSize != 10)
                throw new InvalidDataException("Сейчас поддерживается только feature_step_size=10 мс");
        }
        if (fallbackName.Length is < 1 or > 64 || fallbackThreshold is <= 0 or > 1 ||
            windowSize is < 1 or > 30)
            throw new ArgumentException("Некорректная конфигурация слова активации");
        _word=fallbackName;
        _threshold=fallbackThreshold;
        _windowSize=windowSize;
        _model=new FlatBufferModel(path);
        if (!_model.Initialized) throw new InvalidDataException("Не удалось загрузить TFLite");
        _interpreter=new Interpreter(_model);
        if (_interpreter.AllocateTensors()!=Status.Ok) throw new InvalidDataException("TFLite не смог выделить память");
        if (_interpreter.Inputs.Length!=1 || _interpreter.Outputs.Length!=1)
            throw new InvalidDataException("Требуется MicroWakeWord с одним входом и выходом");
        var shape=_interpreter.Inputs[0].Dims;
        if (shape.Length!=3 || shape[0]!=1 || shape[2]!=40 || shape[1] is < 1 or > 400)
            throw new InvalidDataException("Ожидается вход [1, шаги, 40] для MicroWakeWord");
        if ((int)_interpreter.Inputs[0].Type != 9)
            throw new InvalidDataException("Нужна INT8-модель");
        _stride=shape[1];
        _worker=Task.Run(ConsumeAsync);
    }

    public void Feed(byte[] pcm)
    {
        if (!_stop.IsCancellationRequested && pcm.Length>0)
            _samples.Writer.TryWrite(pcm);
    }
    private async Task ConsumeAsync()
    {
        try
        {
            await foreach(var pcm in _samples.Reader.ReadAllAsync(_stop.Token))
            {
                foreach(var slice in _frontend.Accept(pcm))
                {
                    _features.AddRange(slice);
                    if (_features.Count < _stride*40) continue;
                    var tensor=_interpreter.Inputs[0];
                    var data=_features.Take(_stride*40).Select(x=>unchecked((byte)x)).ToArray();
                    Marshal.Copy(data,0,tensor.DataPointer,data.Length);
                    _features.RemoveRange(0,_stride*40);
                    if (_interpreter.Invoke()!=Status.Ok)
                        throw new InvalidOperationException("Ошибка вычисления TensorFlow Lite");
                    var output=_interpreter.Outputs[0];
                    int typeId=(int)output.Type;
                    double prob=typeId switch
                    {
                        1 => Marshal.PtrToStructure<float>(output.DataPointer), // float32
                        3 => Marshal.ReadByte(output.DataPointer), // uint8
                        9 => unchecked((sbyte)Marshal.ReadByte(output.DataPointer)), // int8
                        _ => throw new InvalidDataException("Неподдерживаемый тип выходного тензора TFLite")
                    };
                    if (typeId is 3 or 9)
                    {
                        var quant=output.QuantizationParams;
                        prob=quant.Scale>0
                            ? (prob-quant.ZeroPoint)*quant.Scale
                            : (prob+(typeId==9?128:0))/255;
                    }
                    prob=Math.Clamp(prob,0,1);
                    _probabilities.Enqueue(prob);
                    while (_probabilities.Count>_windowSize)_probabilities.Dequeue();
                    if (_probabilities.Count==_windowSize &&
                        _probabilities.Average()>=_threshold &&
                        DateTime.UtcNow-_lastDetection>TimeSpan.FromSeconds(1.5))
                    {
                        _lastDetection=DateTime.UtcNow;
                        Detected?.Invoke(_word,_probabilities.Average());
                        _probabilities.Clear();
                    }
                }
            }
        }
        catch(OperationCanceledException) when(_stop.IsCancellationRequested){}
        catch(Exception ex){Error?.Invoke(ex.Message);}
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _samples.Writer.TryComplete();
        try{await _worker;}catch(OperationCanceledException){}
        _interpreter.Dispose();
        _model.Dispose();
        _stop.Dispose();
    }
}
