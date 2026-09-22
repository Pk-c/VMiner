using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VMiner.Models;
using VMiner.Windows;

namespace VMiner.Services;

public sealed class GlobalCaptureService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Func<AppConfig> _getConfig;
    private readonly NativeMethods.HookProc _keyboardCallback;
    private readonly NativeMethods.HookProc _mouseCallback;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private bool _hotkeyDown;
    private volatile bool _selecting;
    private NativeMethods.Point _origin;
    private NativeMethods.Point _current;
    private SelectionOverlay? _overlay;

    public event EventHandler<BitmapSource>? RegionCaptured;
    public event EventHandler<string>? CaptureFailed;

    public GlobalCaptureService(Dispatcher dispatcher, Func<AppConfig> getConfig)
    {
        _dispatcher = dispatcher;
        _getConfig = getConfig;
        _keyboardCallback = KeyboardHook;
        _mouseCallback = MouseHook;
    }

    public void Start()
    {
        if (_keyboardHook != IntPtr.Zero)
            return;

        _keyboardHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WhKeyboardLl, _keyboardCallback, IntPtr.Zero, 0);
        _mouseHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WhMouseLl, _mouseCallback, IntPtr.Zero, 0);

        if (_keyboardHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
        {
            Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Unable to install the global keyboard hooks.");
        }
    }

    private IntPtr KeyboardHook(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var info = Marshal.PtrToStructure<NativeMethods.KeyboardHookData>(data);
            var kind = message.ToInt32();
            var isDown = kind is NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown;
            var isUp = kind is NativeMethods.WmKeyUp or NativeMethods.WmSysKeyUp;
            var captureKey = VirtualKey(_getConfig().Hotkey);

            if (info.VkCode == NativeMethods.VkEscape && isDown && _selecting)
                _dispatcher.BeginInvoke(CancelSelection);
            else if (info.VkCode == captureKey && isDown && !_hotkeyDown)
            {
                _hotkeyDown = true;
                NativeMethods.GetCursorPos(out var point);
                _dispatcher.BeginInvoke(() => BeginSelection(point));
            }
            else if (info.VkCode == captureKey && isUp)
            {
                _hotkeyDown = false;
                NativeMethods.GetCursorPos(out var point);
                _dispatcher.BeginInvoke(() => FinishSelection(point));
            }
        }

        return NativeMethods.CallNextHookEx(_keyboardHook, code, message, data);
    }

    private IntPtr MouseHook(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && _selecting)
        {
            var kind = message.ToInt32();
            if (kind == NativeMethods.WmMouseMove)
            {
                var info = Marshal.PtrToStructure<NativeMethods.MouseHookData>(data);
                _dispatcher.BeginInvoke(() => UpdateSelection(info.Position));
            }
            else if (kind == NativeMethods.WmRightButtonDown)
            {
                _dispatcher.BeginInvoke(CancelSelection);
            }
        }

        return NativeMethods.CallNextHookEx(_mouseHook, code, message, data);
    }

    private void BeginSelection(NativeMethods.Point point)
    {
        if (_selecting)
            return;
        _selecting = true;
        _origin = _current = point;
        _overlay = new SelectionOverlay();
        _overlay.Show();
        _overlay.Update(_origin, _current);
    }

    private void UpdateSelection(NativeMethods.Point point)
    {
        if (!_selecting)
            return;
        _current = point;
        _overlay?.Update(_origin, _current);
    }

    private async void FinishSelection(NativeMethods.Point point)
    {
        if (!_selecting)
            return;
        _current = point;
        _selecting = false;
        CloseOverlay();

        var left = Math.Min(_origin.X, _current.X);
        var top = Math.Min(_origin.Y, _current.Y);
        var width = Math.Abs(_current.X - _origin.X);
        var height = Math.Abs(_current.Y - _origin.Y);
        if (width < _getConfig().MinimumCaptureSize || height < _getConfig().MinimumCaptureSize)
            return;

        await Task.Delay(70);
        try
        {
            RegionCaptured?.Invoke(this, ScreenCaptureService.Capture(
                new Int32Rect(left, top, width, height)));
        }
        catch (Exception exception)
        {
            CaptureFailed?.Invoke(this, exception.Message);
        }
    }

    private void CancelSelection()
    {
        _selecting = false;
        CloseOverlay();
    }

    private void CloseOverlay()
    {
        _overlay?.Close();
        _overlay = null;
    }

    private static uint VirtualKey(string key) => key switch
    {
        "RightShift" => 0xA1,
        "LeftControl" => 0xA2,
        "RightControl" => 0xA3,
        "LeftAlt" => 0xA4,
        "RightAlt" => 0xA5,
        _ => 0xA0,
    };

    public void Dispose()
    {
        CloseOverlay();
        if (_keyboardHook != IntPtr.Zero)
            NativeMethods.UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != IntPtr.Zero)
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
        _keyboardHook = _mouseHook = IntPtr.Zero;
    }
}
