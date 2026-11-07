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
        if (Path.GetExtension(path).Equals(".json",StringComparison.OrdinalIgnoreCase))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root=doc.RootElement;
            if (!root.TryGetProperty("model",out var node) || node.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Не найдено поле model в конфигурации MicroWakeWord");
            string reference = node.GetString() ?? "";
            if (Uri.TryCreate(reference,UriKind.Absolute,out _))
                throw new InvalidDataException("Нужна локальная модель, а не URL");
            path=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!,reference));
            if (!File.Exists(path)) throw new FileNotFoundException("В конфигурации нет файла весов",path);
            if (root.TryGetProperty("wake_word",out var wordNode))
                fallbackName=wordNode.GetString()??fallbackName;
            if (root.TryGetProperty("micro",out var mic))
            {
                if (mic.TryGetProperty("probability_cutoff",out var t) && t.TryGetDouble(out var threshold))
                    fallbackThreshold=threshold;
                if (mic.TryGetProperty("sliding_window_size",out var w) && w.TryGetInt32(out var count))
                    windowSize=count;
                if (mic.TryGetProperty("feature_step_size",out var step) && step.GetInt32()!=10)
                    throw new InvalidDataException("Эта модель требует другой частоты спектральных признаков");
            }
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
        if (_interpreter.Inputs[0].Type != DataType.Int8)
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
                    Array result=output.GetData(false);
                    if(result.Length<1)throw new InvalidDataException("Пустой результат TFLite");
                    double prob=Convert.ToDouble(result.GetValue(0),System.Globalization.CultureInfo.InvariantCulture);
                    if (result is byte[] || result is sbyte[])
                    {
                        var q=output.QuantizationParams;
                        if (q.Scale>0) prob=(prob-q.ZeroPoint)*q.Scale;
                        else prob=(prob+(result is sbyte[]?128:0))/255;
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
