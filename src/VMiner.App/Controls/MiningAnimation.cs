using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace VMiner.Controls;

public sealed class MiningAnimation : Image
{
    private static readonly Uri GifUri = new(
        "pack://application:,,,/img/mining.gif", UriKind.Absolute);

    private readonly DispatcherTimer _timer = new();
    private IReadOnlyList<BitmapFrame> _frames = [];
    private IReadOnlyList<TimeSpan> _delays = [];
    private int _frameIndex;

    internal int FrameCount => _frames.Count;
    internal bool IsPlaying => _timer.IsEnabled;

    public MiningAnimation()
    {
        Stretch = Stretch.Uniform;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        _timer.Tick += AdvanceFrame;
        Loaded += (_, _) =>
        {
            LoadFrames();
            UpdatePlayback();
        };
        Unloaded += (_, _) => _timer.Stop();
        IsVisibleChanged += (_, _) => UpdatePlayback();
    }

    private void LoadFrames()
    {
        if (_frames.Count > 0)
            return;

        var resource = Application.GetResourceStream(GifUri)
                       ?? throw new InvalidOperationException(
                           "The embedded mining animation could not be loaded.");
        using (resource.Stream)
        {
            var decoder = new GifBitmapDecoder(
                resource.Stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            _frames = decoder.Frames.ToArray();
            _delays = _frames.Select(ReadDelay).ToArray();
        }

        if (_frames.Count > 0)
            Source = _frames[0];
    }

    private void UpdatePlayback()
    {
        if (!IsLoaded || !IsVisible || _frames.Count < 2)
        {
            _timer.Stop();
            return;
        }

        _timer.Interval = _delays[_frameIndex];
        _timer.Start();
    }

    private void AdvanceFrame(object? sender, EventArgs e)
    {
        _frameIndex = (_frameIndex + 1) % _frames.Count;
        Source = _frames[_frameIndex];
        _timer.Interval = _delays[_frameIndex];
    }

    private static TimeSpan ReadDelay(BitmapFrame frame)
    {
        const int defaultDelayMilliseconds = 100;
        try
        {
            if (frame.Metadata is BitmapMetadata metadata &&
                metadata.ContainsQuery("/grctlext/Delay"))
            {
                var hundredths = Convert.ToInt32(metadata.GetQuery("/grctlext/Delay"));
                return TimeSpan.FromMilliseconds(Math.Max(2, hundredths) * 10);
            }
        }
        catch
        {
        }
        return TimeSpan.FromMilliseconds(defaultDelayMilliseconds);
    }
}
