using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace OpenAssistant.Simulator.Display;

public enum DisplayMode { Offline, Idle, Listening, Thinking, Speaking, Music, Error }

public sealed class DisplayEffect
{
    public string Preset { get; set; } = "off";
    public string Color { get; set; } = "#89ABD8";
    public double Speed { get; set; } = 1;
}
public sealed class DisplayProfile
{
    public int Width { get; set; } = 32;
    public int Height { get; set; } = 10;
    public int Fps { get; set; } = 25;
    public int Brightness { get; set; } = 65;
    public bool Serpentine { get; set; }
    public Dictionary<string,DisplayEffect> States { get; set; } = new()
    {
        ["offline"] = new() { Preset = "off" },
        ["idle"] = new() { Preset = "breath", Color="#789BC1",Speed=.6 },
        ["listening"] = new() { Preset = "bars",Color="#69ACE5",Speed=1.2 },
        ["thinking"] = new() { Preset="orbit",Color="#99AACE" },
        ["speaking"] = new() { Preset="wave",Color="#B0BFE1" },
        ["music"] = new() { Preset="bars",Color="#82A8DD",Speed=1.5 },
        ["error"] = new() { Preset="pulse",Color="#C78282",Speed=.7 }
    };
    public static DisplayProfile Parse(JsonElement json)
    {
        var p=JsonSerializer.Deserialize<DisplayProfile>(json.GetRawText(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive=true })
            ?? throw new JsonException("Нет конфигурации матрицы");
        if (p.Width is < 1 or > 256 || p.Height is < 1 or > 128 ||
            (long)p.Width*p.Height>8192 || p.Brightness is < 1 or > 100 ||
            p.Fps is < 1 or > 60 || p.States is null) throw new JsonException("Некорректный размер матрицы");
        foreach(var state in Enum.GetNames<DisplayMode>())
        {
            if (!p.States.TryGetValue(state.ToLowerInvariant(),out var effect) ||
                effect is null || !System.Text.RegularExpressions.Regex.IsMatch(
                    effect.Color??"", "^#[0-9a-fA-F]{6}$") ||
                effect.Speed is < .1 or > 5 || !double.IsFinite(effect.Speed))
                throw new JsonException("Некорректный эффект дисплея");
        }
        return p;
    }
}

/// <summary>Renderer for a configurable RGB matrix; default 32×10, 320 RGB pixels.</summary>
public sealed class RgbMatrixView : FrameworkElement, IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly Brush _pixelOff=new SolidColorBrush(Color.FromRgb(41,45,53));
    private readonly Pen _border=new(new SolidColorBrush(Color.FromRgb(61,66,76)),1);
    private DisplayProfile _profile=new();
    private byte[] _frame=new byte[32*10*3];
    private byte[]? _animation;
    private int _frameIndex;
    private long _tick;
    private double _micLevel,_speechLevel;
    private DisplayMode _mode=DisplayMode.Offline;
    public int Columns=>_profile.Width;
    public int Rows=>_profile.Height;
    public int FrameBytes=>Columns*Rows*3;
    public string Dimensions=>$"{Columns} × {Rows} · {Columns*Rows} RGB";
    public DisplayMode Mode
    {
        get=>_mode;
        set {if(_mode==value)return;_mode=value;_tick=0;BuildFrame();InvalidateVisual();}
    }
    public RgbMatrixView()
    {
        _timer=new DispatcherTimer(DispatcherPriority.Background) { Interval=TimeSpan.FromMilliseconds(40) };
        _timer.Tick+=(_,_)=>{_tick++;BuildFrame();InvalidateVisual();};
        Loaded+=(_,_)=>_timer.Start();
        Unloaded+=(_,_)=>_timer.Stop();
        BuildFrame();
    }
    public void Configure(DisplayProfile profile)
    {
        _profile=profile;
        _timer.Interval=TimeSpan.FromSeconds(1.0/profile.Fps);
        _frame=new byte[profile.Width*profile.Height*3];
        if(_animation is not null&&_animation.Length%_frame.Length!=0)_animation=null;
        _frameIndex=0;BuildFrame();InvalidateVisual();
    }
    public void SetMicrophoneLevel(double level) => _micLevel=Math.Clamp(level,0,1);
    public void SetSpeechLevel(double level) => _speechLevel=Math.Clamp(level,0,1);
    public byte[] SnapshotRgb888(bool? serpentine=null)
    {
        var copy=_frame.ToArray();
        if(!(serpentine??_profile.Serpentine))return copy;
        for(int y=1;y<Rows;y+=2)for(int x=0;x<Columns/2;x++)
            for(int c=0;c<3;c++)
            {
                int a=(y*Columns+x)*3+c,b=(y*Columns+Columns-x-1)*3+c;
                (copy[a],copy[b])=(copy[b],copy[a]);
            }
        return copy;
    }
    public void LoadAnimation(string path)
    {
        var data=File.ReadAllBytes(path);
        if(data.Length==0||data.Length%FrameBytes!=0)
            throw new InvalidDataException($"Файл RGB888 должен содержать кадры по {FrameBytes} байт ({Columns}×{Rows}×3)");
        if(data.Length>FrameBytes*12000L)throw new InvalidDataException("Слишком длинная анимация");
        _animation=data;_frameIndex=0;BuildFrame();InvalidateVisual();
    }
    public void ClearAnimation(){_animation=null;BuildFrame();InvalidateVisual();}

    private static string Key(DisplayMode mode)=>mode.ToString().ToLowerInvariant();
    private void Pixel(int x,int y,byte r,byte g,byte b)
    {
        if((uint)x>=(uint)Columns||(uint)y>=(uint)Rows)return;
        int i=(y*Columns+x)*3;_frame[i]=r;_frame[i+1]=g;_frame[i+2]=b;
    }
    private static double Clamp(double n)=>Math.Clamp(n,0,1);
    private void BuildFrame()
    {
        Array.Clear(_frame);
        if(_animation is not null&&Mode==DisplayMode.Idle)
        {
            int count=_animation.Length/FrameBytes;
            Buffer.BlockCopy(_animation,(_frameIndex++%count)*FrameBytes,_frame,0,FrameBytes);
            return;
        }
        var effect=_profile.States.GetValueOrDefault(Key(_mode))??new();
        int w=Columns,h=Rows;
        var hex=effect.Color;
        if(hex is null||hex.Length!=7)hex="#89ABD8";
        byte[] rgb=[Convert.ToByte(hex[1..3],16),Convert.ToByte(hex[3..5],16),Convert.ToByte(hex[5..7],16)];
        double time=_tick/(double)_profile.Fps*Math.Clamp(effect.Speed,.1,5);
        double volume=_mode switch
        {
            DisplayMode.Listening=>_micLevel,
            DisplayMode.Speaking=>_speechLevel,
            _=>.42
        };
        for(int y=0;y<h;y++)for(int x=0;x<w;x++)
        {
            double xx=(x+.5)/w,yy=(y+.5)/h;
            double nx=(x-(w-1)*.5)/Math.Max(w*.5,1),ny=(y-(h-1)*.5)/Math.Max(h*.5,1);
            double p=0;
            switch(effect.Preset)
            {
                case "solid":p=1;break;
                case "breath":p=.10+.33*(1+Math.Sin(time*2*Math.PI*.35));break;
                case "pulse":p=.15+.8*Math.Pow((1+Math.Sin(time*2*Math.PI*.7))/2,5);break;
                case "wave":p=Math.Max(0,1-Math.Abs(ny-.65*Math.Sin(xx*12-time*3))/.32)*(.38+.62*volume);break;
                case "bars":
                    double bar=.12+.88*Clamp(volume*.75+.4*Math.Abs(Math.Sin(x*.49+time*3)*Math.Cos(x*.16-time*1.3)));
                    p=yy>=1-bar?.35+.65*(1-(yy-(1-bar))/bar):0;break;
                case "orbit":p=Math.Max(0,1-Math.Abs(x-((time*5)%(w+7)-3))/4);break;
                case "sparkles":
                    int n=(x*733+y*1399+(int)Math.Floor(time*8)*451)%997;
                    if(n<0)n+=997;
                    double noise=((n*151+173)%997)/997.0;
                    p=noise>.97?1:noise>.94?.35:0;break;
                case "eyes":
                    double center=x<w/2?w*.32:w*.68;
                    double distance=Math.Sqrt(Math.Pow((x-center)/(w*.12),2)+Math.Pow((y-h*.5)/(h*.3),2));
                    p=distance<=1?.25+.6*Math.Pow(Math.Max(0,1-distance),.8):0;break;
                case "scan":p=Math.Max(0,1-Math.Abs(x-(time*9%Math.Max(w,1)))/4);break;
            }
            p=Clamp(p)*(_profile.Brightness/100.0);
            Pixel(x,y,(byte)Math.Round(rgb[0]*p),(byte)Math.Round(rgb[1]*p),(byte)Math.Round(rgb[2]*p));
        }
    }

    protected override Size MeasureOverride(Size available) =>
        new(Math.Min(available.Width,770),Math.Min(available.Height,235));
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var pitch=Math.Max(1.5,Math.Min((ActualWidth-18)/Columns,(ActualHeight-18)/Rows));
        double radius=Math.Max(.6,pitch*.31);
        double ox=(ActualWidth-pitch*Columns)/2,oy=(ActualHeight-pitch*Rows)/2;
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(21,24,30)),_border,
            new Rect(0,0,ActualWidth,ActualHeight),12,12);
        for(int y=0;y<Rows;y++)for(int x=0;x<Columns;x++)
        {
            int i=(y*Columns+x)*3;
            byte r=_frame[i],g=_frame[i+1],b=_frame[i+2];
            Brush brush=r+g+b==0?_pixelOff:new SolidColorBrush(Color.FromRgb(r,g,b));
            dc.DrawEllipse(brush,null,new Point(ox+(x+.5)*pitch,oy+(y+.5)*pitch),radius,radius);
        }
    }
    public void Dispose()=>_timer.Stop();
}
