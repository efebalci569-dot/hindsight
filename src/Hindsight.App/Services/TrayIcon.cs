using System.Drawing;
using System.Windows.Forms;
using Hindsight.Core.Settings;

namespace Hindsight.App.Services;

/// <summary>The notification-area icon. A red dot on the icon means the replay buffer is running.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _idle;
    private readonly Icon _recording;
    private readonly ToolStripMenuItem _saveClip;
    private readonly ToolStripMenuItem _record;
    private readonly ToolStripMenuItem _screenshot;
    private readonly ToolStripMenuItem _buffer;

    public TrayIcon()
    {
        using var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Resources/hindsight.ico"))!.Stream;
        _idle = new Icon(stream, SystemInformation.SmallIconSize);
        _recording = WithDot(_idle);

        var menu = new ContextMenuStrip { ShowImageMargin = false, Renderer = new DarkRenderer(), Font = new Font("Segoe UI", 9.5f) };
        var open = new ToolStripMenuItem("Hindsight'ı aç", null, (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty)) { Font = new Font("Segoe UI", 9.5f, System.Drawing.FontStyle.Bold) };
        _saveClip = new ToolStripMenuItem("Klip kaydet", null, (_, _) => ActionRequested?.Invoke(this, HotkeyAction.SaveClip));
        _record = new ToolStripMenuItem("Kaydı başlat", null, (_, _) => ActionRequested?.Invoke(this, HotkeyAction.ToggleRecording));
        _screenshot = new ToolStripMenuItem("Ekran görüntüsü al", null, (_, _) => ActionRequested?.Invoke(this, HotkeyAction.Screenshot));
        _buffer = new ToolStripMenuItem("Arka plan kaydını kapat", null, (_, _) => ActionRequested?.Invoke(this, HotkeyAction.ToggleBuffer));
        var folder = new ToolStripMenuItem("Klip klasörünü aç", null, (_, _) => FolderRequested?.Invoke(this, EventArgs.Empty));
        var exit = new ToolStripMenuItem("Çıkış", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.AddRange([open, new ToolStripSeparator(), _saveClip, _record, _screenshot, _buffer, new ToolStripSeparator(), folder, exit]);

        _icon = new NotifyIcon { Icon = _idle, Text = "Hindsight", ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                OpenRequested?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    public event EventHandler? OpenRequested;

    public event EventHandler<HotkeyAction>? ActionRequested;

    public event EventHandler? FolderRequested;

    public event EventHandler? ExitRequested;

    public void Update(bool buffering, bool manualRecording, string? gameName, AppSettings settings)
    {
        _icon.Icon = buffering ? _recording : _idle;
        var text = buffering ? $"Hindsight — kaydediliyor{(gameName is null ? string.Empty : " · " + gameName)}" : "Hindsight — beklemede";
        _icon.Text = text.Length > 63 ? text[..63] : text;
        _saveClip.Text = $"Klip kaydet\t{settings.SaveClipHotkey}";
        _record.Text = (manualRecording ? "Kaydı durdur" : "Kaydı başlat") + $"\t{settings.ToggleRecordingHotkey}";
        _screenshot.Text = $"Ekran görüntüsü al\t{settings.ScreenshotHotkey}";
        _buffer.Text = (buffering ? "Arka plan kaydını kapat" : "Arka plan kaydını aç") + $"\t{settings.ToggleBufferHotkey}";
    }

    public void ShowBalloon(string title, string text)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text;
        _icon.ShowBalloonTip(4000);
    }

    private static Icon WithDot(Icon source)
    {
        using var bitmap = source.ToBitmap();
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var d = bitmap.Width * 0.46f;
            var x = bitmap.Width - d - 0.5f;
            var y = bitmap.Height - d - 0.5f;
            using var ring = new SolidBrush(Color.FromArgb(255, 11, 11, 16));
            g.FillEllipse(ring, x - 1.5f, y - 1.5f, d + 3, d + 3);
            using var dot = new SolidBrush(Color.FromArgb(255, 255, 77, 94));
            g.FillEllipse(dot, x, y, d, d);
        }

        return Icon.FromHandle(bitmap.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _idle.Dispose();
        _recording.Dispose();
    }

    private sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        private static readonly Color Back = Color.FromArgb(27, 27, 38);
        private static readonly Color Hover = Color.FromArgb(44, 44, 58);
        private static readonly Color Line = Color.FromArgb(42, 42, 56);

        public DarkRenderer()
            : base(new DarkColors())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = Color.FromArgb(244, 244, 247);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            using var brush = new SolidBrush(e.Item.Selected ? Hover : Back);
            e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.Item.Size));
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(Line);
            var y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
        }

        private sealed class DarkColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Back;

            public override Color MenuBorder => Line;

            public override Color ImageMarginGradientBegin => Back;

            public override Color ImageMarginGradientMiddle => Back;

            public override Color ImageMarginGradientEnd => Back;
        }
    }
}
