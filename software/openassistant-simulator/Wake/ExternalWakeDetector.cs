using System.Diagnostics;
using System.Text.Json;
namespace OpenAssistant.Simulator.Wake;
/// <summary>адаптер детектора wake-word: совместимый процесс читает pcm16 stdin и пишет события jsonl stdout.</summary>
public sealed class ExternalWakeDetector : IDisposable
{
    private Process? _process;
    private readonly SemaphoreSlim _write = new(1,1);
    public event Action<string,double>? Detected;
    public event Action<string>? Diagnostic;
    /// <summary>запускает локальный tflite runtime по указанному пользователем пути.</summary>
    public void Start(string executable,string arguments)
    {
        Stop();
        if(string.IsNullOrWhiteSpace(executable)) throw new ArgumentException("укажите исполняемый файл детектора");
        var info=new ProcessStartInfo(executable){UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};
        foreach(var part in ParseArguments(arguments)) info.ArgumentList.Add(part);
        _process=Process.Start(info)??throw new InvalidOperationException("детектор не запустился");
        _=Task.Run(async()=>{
            try {
                while(_process is {HasExited:false} p && await p.StandardOutput.ReadLineAsync() is { } line) {
                    try {
                        using var json=JsonDocument.Parse(line);var root=json.RootElement;
                        if(root.TryGetProperty("type",out var type)&&type.GetString()=="wake") {
                            var word=root.GetProperty("word").GetString()??"";
                            var score=root.TryGetProperty("score",out var s)?s.GetDouble():0;
                            Detected?.Invoke(word,score);
                        }
                    }catch(JsonException){Diagnostic?.Invoke(line);}
                }
            }catch(Exception e){Diagnostic?.Invoke(e.Message);}
        });
        _=Task.Run(async()=>{try{var err=await _process.StandardError.ReadToEndAsync();if(!string.IsNullOrWhiteSpace(err))Diagnostic?.Invoke(err);}catch{}});
    }
    /// <summary>отправляет pcm16 в детектор; правильный mel frontend остаётся ответственностью tflite runtime.</summary>
    public async Task FeedAsync(byte[] pcm)
    {
        if(_process is null||_process.HasExited)return;
        await _write.WaitAsync();
        try{await _process.StandardInput.BaseStream.WriteAsync(pcm);await _process.StandardInput.BaseStream.FlushAsync();}
        catch(Exception e){Diagnostic?.Invoke(e.Message);}
        finally{_write.Release();}
    }
    /// <summary>завершает только запущенный процесс детектора.</summary>
    public void Stop(){if(_process is null)return;try{if(!_process.HasExited)_process.Kill(true);}catch{} _process.Dispose();_process=null;}
    /// <summary>разбивает аргументы пути; кавычки допускаются для путей с пробелами.</summary>
    private static IEnumerable<string> ParseArguments(string input)
    {
        var matches=System.Text.RegularExpressions.Regex.Matches(input,"(?:[^\\s\\\"]+|\\\"[^\\\"]*\\\")+");
        foreach(System.Text.RegularExpressions.Match m in matches) yield return m.Value.Trim('"');
    }
    /// <summary>освобождает процесс.</summary>
    public void Dispose(){Stop();_write.Dispose();}
}