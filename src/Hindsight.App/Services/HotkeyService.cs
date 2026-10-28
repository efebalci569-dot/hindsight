using System.Runtime.InteropServices;
using System.Windows.Interop;
using Hindsight.Core;
using Hindsight.Core.Settings;

namespace Hindsight.App.Services;

public enum HotkeyAction { SaveClip, ToggleRecording, Screenshot, ToggleBuffer }

/// <summary>
/// System-wide shortcuts via RegisterHotKey on a hidden message-only window, so they work while a game
/// has focus and while Hindsight's own window is closed.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private readonly HwndSource _source;
    private readonly Dictionary<int, HotkeyAction> _registered = [];

    public HotkeyService()
    {
        var parameters = new HwndSourceParameters("Hindsight hotkeys") { ParentWindow = new IntPtr(-3), WindowStyle = 0 }; // HWND_MESSAGE
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public event EventHandler<HotkeyAction>? Pressed;

    /// <summary>Registers the shortcuts from settings. Returns the ones Windows refused (already taken by another app).</summary>
    public IReadOnlyList<string> Apply(AppSettings settings)
    {
        Clear();
        var failed = new List<string>();
        var id = 1;
        foreach (var (action, text) in new[]
                 {
                     (HotkeyAction.SaveClip, settings.SaveClipHotkey),
                     (HotkeyAction.ToggleRecording, settings.ToggleRecordingHotkey),
                     (HotkeyAction.Screenshot, settings.ScreenshotHotkey),
                     (HotkeyAction.ToggleBuffer, settings.ToggleBufferHotkey),
                 })
        {
            var hotkey = Hotkey.Parse(text);
            if (hotkey.IsEmpty)
            {
                continue;
            }

            if (RegisterHotKey(_source.Handle, id, (uint)hotkey.Modifiers | ModNoRepeat, (uint)hotkey.VirtualKey))
            {
                _registered[id] = action;
            }
            else
            {
                failed.Add(hotkey.ToString());
                Log.Warn($"Hotkey {hotkey} is used by another application.");
            }

            id++;
        }

        return failed;
    }

    private void Clear()
    {
        foreach (var id in _registered.Keys)
        {
            UnregisterHotKey(_source.Handle, id);
        }

        _registered.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _registered.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            Pressed?.Invoke(this, action);
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Clear();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
