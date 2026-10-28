using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Hindsight.Core.Settings;
using HotkeyValue = Hindsight.Core.Settings.Hotkey;

namespace Hindsight.App.Controls;

/// <summary>
/// A text box that records a key combination instead of text. Backspace/Delete/Esc clears it.
/// While it has focus the global hotkeys are paused (see <see cref="EditingChanged"/>) so pressing
/// the current shortcut does not also save a clip.
/// </summary>
public sealed class HotkeyBox : TextBox
{
    public static readonly DependencyProperty HotkeyProperty = DependencyProperty.Register(
        nameof(Hotkey), typeof(string), typeof(HotkeyBox),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((HotkeyBox)d).Text = Display((string?)e.NewValue)));

    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        Cursor = Cursors.Hand;
        MinWidth = 170;
        ToolTip = "Tıkla ve yeni kısayola bas. Silmek için Backspace.";
    }

    public static event EventHandler<bool>? EditingChanged;

    public string Hotkey
    {
        get => (string)GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value);
    }

    private static string Display(string? value) => HotkeyValue.Parse(value) is { IsEmpty: false } h ? h.ToString() : "Yok";

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        Text = "Kısayola bas…";
        EditingChanged?.Invoke(this, true);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        Text = Display(Hotkey);
        EditingChanged?.Invoke(this, false);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return;
        }

        var modifiers = HotkeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= HotkeyModifiers.Ctrl;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= HotkeyModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= HotkeyModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= HotkeyModifiers.Win;

        if (modifiers == HotkeyModifiers.None && key is Key.Back or Key.Delete or Key.Escape)
        {
            Hotkey = "Yok";
            MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            return;
        }

        var vk = KeyInterop.VirtualKeyFromKey(key);
        var isFunctionKey = vk is >= 0x70 and <= 0x87 || vk is 0x13 or 0x91 or 0x2C;
        if (!HotkeyValue.IsNamedKey(vk) || (modifiers == HotkeyModifiers.None && !isFunctionKey))
        {
            Text = "Ctrl / Alt / Shift ile birlikte bas…";
            return;
        }

        Hotkey = new HotkeyValue(modifiers, vk).ToString();
        MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    }
}
