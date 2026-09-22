using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using VMiner.Windows;

namespace VMiner.Services;

public static class ScreenCaptureService
{
    public static BitmapSource Capture(Int32Rect region)
    {
        if (region.Width <= 0 || region.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(region));

        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        var memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
        var bitmap = NativeMethods.CreateCompatibleBitmap(screenDc, region.Width, region.Height);
        var previous = NativeMethods.SelectObject(memoryDc, bitmap);

        try
        {
            if (!NativeMethods.BitBlt(memoryDc, 0, 0, region.Width, region.Height,
                    screenDc, region.X, region.Y, NativeMethods.SrcCopy | NativeMethods.CaptureBlt))
                throw new InvalidOperationException("Windows screen capture failed.");

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            NativeMethods.SelectObject(memoryDc, previous);
            NativeMethods.DeleteObject(bitmap);
            NativeMethods.DeleteDC(memoryDc);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
