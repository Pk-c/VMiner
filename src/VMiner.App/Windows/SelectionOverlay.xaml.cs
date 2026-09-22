using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace VMiner.Windows;

public partial class SelectionOverlay : Window
{
    private readonly int _virtualX;
    private readonly int _virtualY;
    private double _scaleX = 1;
    private double _scaleY = 1;

    public SelectionOverlay()
    {
        InitializeComponent();
        _virtualX = NativeMethods.GetSystemMetrics(NativeMethods.SmXVirtualScreen);
        _virtualY = NativeMethods.GetSystemMetrics(NativeMethods.SmYVirtualScreen);

        SourceInitialized += (_, _) =>
        {
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget is { } target)
            {
                _scaleX = target.TransformToDevice.M11;
                _scaleY = target.TransformToDevice.M22;
            }

            Left = _virtualX / _scaleX;
            Top = _virtualY / _scaleY;
            Width = NativeMethods.GetSystemMetrics(NativeMethods.SmCxVirtualScreen) / _scaleX;
            Height = NativeMethods.GetSystemMetrics(NativeMethods.SmCyVirtualScreen) / _scaleY;
            NativeMethods.ConfigureOverlayWindow(new WindowInteropHelper(this).Handle, true);
        };
    }

    internal void Update(NativeMethods.Point origin, NativeMethods.Point current)
    {
        var left = (Math.Min(origin.X, current.X) - _virtualX) / _scaleX;
        var top = (Math.Min(origin.Y, current.Y) - _virtualY) / _scaleY;
        var widthPixels = Math.Abs(current.X - origin.X);
        var heightPixels = Math.Abs(current.Y - origin.Y);
        var width = widthPixels / _scaleX;
        var height = heightPixels / _scaleY;

        Canvas.SetLeft(SelectionRectangle, left);
        Canvas.SetTop(SelectionRectangle, top);
        SelectionRectangle.Width = width;
        SelectionRectangle.Height = height;

        SizeText.Text = $"{widthPixels} × {heightPixels}";
        SizeBadge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(SizeBadge, left);
        Canvas.SetTop(SizeBadge, Math.Max(0, top - SizeBadge.DesiredSize.Height - 4));
    }
}
