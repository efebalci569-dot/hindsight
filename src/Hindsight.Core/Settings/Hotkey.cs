namespace Hindsight.Core.Settings;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x1,
    Ctrl = 0x2,
    Shift = 0x4,
    Win = 0x8,
}

/// <summary>A global shortcut: Win32 modifier flags + virtual-key code, written like "Ctrl+Alt+F10".</summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, int VirtualKey)
{
    private static readonly Dictionary<int, string> Names = BuildNames();

    private static readonly Dictionary<string, int> Codes =
        Names.ToDictionary(p => p.Value, p => p.Key, StringComparer.OrdinalIgnoreCase);

    public bool IsEmpty => VirtualKey == 0;

    public static Hotkey None => default;

    public override string ToString()
    {
        if (IsEmpty)
        {
            return "Yok";
        }

        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(KeyName(VirtualKey));
        return string.Join('+', parts);
    }

    public static string KeyName(int vk) => Names.TryGetValue(vk, out var name) ? name : $"0x{vk:X2}";

    public static bool IsNamedKey(int vk) => Names.ContainsKey(vk);

    public static Hotkey Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Equals("Yok", StringComparison.OrdinalIgnoreCase))
        {
            return None;
        }

        var modifiers = HotkeyModifiers.None;
        var key = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= HotkeyModifiers.Ctrl;
                    break;
                case "alt":
                    modifiers |= HotkeyModifiers.Alt;
                    break;
                case "shift":
                    modifiers |= HotkeyModifiers.Shift;
                    break;
                case "win" or "windows":
                    modifiers |= HotkeyModifiers.Win;
                    break;
                default:
                    if (Codes.TryGetValue(raw, out var code))
                    {
                        key = code;
                    }
                    else if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                             int.TryParse(raw[2..], System.Globalization.NumberStyles.HexNumber, null, out code))
                    {
                        key = code;
                    }
                    else
                    {
                        return None;
                    }

                    break;
            }
        }

        return key == 0 ? None : new Hotkey(modifiers, key);
    }

    private static Dictionary<int, string> BuildNames()
    {
        var names = new Dictionary<int, string>();
        for (var i = 0; i < 24; i++) names[0x70 + i] = $"F{i + 1}";
        for (var c = 'A'; c <= 'Z'; c++) names[c] = c.ToString();
        for (var d = 0; d <= 9; d++)
        {
            names['0' + d] = d.ToString();
            names[0x60 + d] = $"Num{d}";
        }

        names[0x13] = "Pause";
        names[0x91] = "ScrollLock";
        names[0x2C] = "PrintScreen";
        names[0x2D] = "Insert";
        names[0x2E] = "Delete";
        names[0x24] = "Home";
        names[0x23] = "End";
        names[0x21] = "PageUp";
        names[0x22] = "PageDown";
        names[0x20] = "Space";
        names[0x6A] = "NumMultiply";
        names[0x6B] = "NumAdd";
        names[0x6D] = "NumSubtract";
        names[0x6F] = "NumDivide";
        names[0xC0] = "Tilde";
        return names;
    }
}
