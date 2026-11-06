using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace OpenAssistant.Simulator.Display;

public enum DisplayMode { Offline, Idle, Listening, Thinking, Speaking, Music, Error }

/// <summary>32×10 RGB888 matrix. Frame data is row-major RGB; odd physical rows may be reversed separately.</summary>
public sealed class RgbMatrixView : FrameworkElement, IDisposable
{
    public const int Columns = 32, Rows = 10, FrameBytes = Columns * Rows * 3;
    private readonly DispatcherTimer _timer;
    private readonly byte[] _frame = new byte[FrameBytes];
    private byte[]? _animation;
    private int _frameIndex, _tick;
    private double _micLevel, _speechLevel;
    private DisplayMode _mode = DisplayMode.Offline;
    private readonly Brush _pixelOff = new SolidColorBrush(Color.FromRgb(41, 45, 53));
    private readonly Pen _border = new(new SolidColorBrush(Color.FromRgb(61, 66, 76)), 1);

    public RgbMatrixView()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(66) };
        _timer.Tick += (_, _) => { _tick++; BuildFrame(); InvalidateVisual(); };
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
        BuildFrame();
    }
    public DisplayMode Mode
    {
        get => _mode;
        set { if (_mode == value) return; _mode = value; _tick = 0; BuildFrame(); InvalidateVisual(); }
    }
    public void SetMicrophoneLevel(double level) => _micLevel = Math.Clamp(level, 0, 1);
    public void SetSpeechLevel(double level) => _speechLevel = Math.Clamp(level, 0, 1);
    public byte[] SnapshotRgb888(bool serpentine = false)
    {
        var snapshot = _frame.ToArray();
        if (!serpentine) return snapshot;
        for (int y = 1; y < Rows; y += 2)
            for (int x = 0; x < Columns/2; x++)
                for (int c = 0; c < 3; c++)
                {
                    int a = (y*Columns+x)*3+c, b = (y*Columns+Columns-1-x)*3+c;
                    (snapshot[a],snapshot[b]) = (snapshot[b],snapshot[a]);
                }
        return snapshot;
    }
    public void LoadAnimation(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length == 0 || bytes.Length % FrameBytes != 0)
            throw new InvalidDataException($"Ожидается RGB888: {FrameBytes} байт на кадр (32×10×3).");
        if (bytes.Length > 12000 * FrameBytes)
            throw new InvalidDataException("Не более 12000 кадров.");
        _animation = bytes;_frameIndex = 0;
        Mode = DisplayMode.Idle;
    }
    public void ClearAnimation() { _animation = null; BuildFrame(); InvalidateVisual(); }
    private void Pixel(int x, int y, byte r, byte g, byte b)
    {
        if ((uint)x >= Columns || (uint)y >= Rows) return;
        int i = (y*Columns+x)*3;
        _frame[i]=r;_frame[i+1]=g;_frame[i+2]=b;
    }
    private void Bar(int x,int length, byte r, byte g, byte b)
    {
        for(int i=0;i<length && i<Rows;i++)Pixel(x, Rows-1-i, r, g, b);
    }
    private void BuildFrame()
    {
        Array.Clear(_frame);
        if (_animation is not null && _mode == DisplayMode.Idle)
        {
            int frames = _animation.Length / FrameBytes;
            Buffer.BlockCopy(_animation,(_frameIndex%frames)*FrameBytes,_frame,0,FrameBytes);
            _frameIndex++;return;
        }
        switch (_mode)
        {
            case DisplayMode.Offline:
                for (int x=13;x<19;x++) Pixel(x,4,54,58,65);
                break;
            case DisplayMode.Idle:
                {
                    int intensity = (int)(55+40*(1+Math.Sin(_tick*0.095))/2);
                    for(int x=0;x<Columns;x++)
                    {
                        double distance = Math.Abs(x-15.5);
                        int height = Math.Max(1, (int)(3.2*Math.Exp(-distance*distance/52)));
                        for(int y=0;y<height;y++)Pixel(x,4+y, (byte)(intensity/2), (byte)(intensity*0.77), (byte)intensity);
                    }
                    break;
                }
            case DisplayMode.Listening:
                {
                    int height = Math.Clamp((int)Math.Round(1+Math.Log10(1+_micLevel*100)*4),1,Rows);
                    for(int x=0;x<Columns;x++)
                    {
                        int h=Math.Max(1,height-((x*19+7)%7)/3);
                        Bar(x,h,81,174,225);
                    }
                    break;
                }
            case DisplayMode.Thinking:
                {
                    int center=(_tick/2)%Columns;
                    for(int x=0;x<Columns;x++)
                    {
                        int distance=Math.Min((x-center+Columns)%Columns,(center-x+Columns)%Columns);
                        if(distance<6)for(int y=4;y<6;y++)Pixel(x,y,(byte)(139-distance*17),(byte)(155-distance*15),(byte)(179-distance*13));
                    }
                    break;
                }
            case DisplayMode.Speaking:
                {
                    int intensity=Math.Clamp((int)(2+_speechLevel*50),2,10);
                    for(int x=0;x<Columns;x++)
                        Bar(x,Math.Clamp(intensity-((x*11+_tick/2)%6),1,Rows),167,181,218);
                    break;
                }
            case DisplayMode.Music:
                for(int x=0;x<Columns;x++)
                {
                    int h=1+(int)(4+4*Math.Abs(Math.Sin((x+_tick*0.45)*0.29))*
                        Math.Abs(Math.Cos((x-_tick)*0.15)));
                    Bar(x,Math.Min(h,Rows),117,154,199);
                }
                break;
            case DisplayMode.Error:
                for(int x=12;x<20;x++){Pixel(x,3,189,102,103);Pixel(x,4,189,102,103);}
                Pixel(15,6,189,102,103);Pixel(16,6,189,102,103);
                break;
        }
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(Math.Min(availableSize.Width, 770), Math.Min(availableSize.Height, 235));
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double pitch=Math.Max(2,Math.Min((ActualWidth-18)/Columns,(ActualHeight-18)/Rows));
        double radius=Math.Max(1.1,pitch*0.32);
        double ox=(ActualWidth-pitch*Columns)/2,oy=(ActualHeight-pitch*Rows)/2;
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(21,24,30)),_border,
            new Rect(0,0,ActualWidth,ActualHeight),12,12);
        for(int y=0;y<Rows;y++)
            for(int x=0;x<Columns;x++)
            {
                int i=(y*Columns+x)*3;
                byte r=_frame[i],g=_frame[i+1],b=_frame[i+2];
                Brush brush = r+g+b==0 ? _pixelOff :
                    new SolidColorBrush(Color.FromRgb(r,g,b));
                dc.DrawEllipse(brush,null,
                    new Point(ox+(x+.5)*pitch,oy+(y+.5)*pitch),radius,radius);
            }
    }
    public void Dispose() => _timer.Stop();
}
