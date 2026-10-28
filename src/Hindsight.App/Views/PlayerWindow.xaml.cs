using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Hindsight.App.Services;
using Hindsight.App.ViewModels;
using Hindsight.Core.Clips;
using Hindsight.Core.Media;

namespace Hindsight.App.Views;

/// <summary>Plays a clip and offers quick edits: trim, shrink for Discord, GIF, copy, delete.</summary>
public partial class PlayerWindow : Window
{
    private readonly ClipItem _item;
    private readonly ClipActions _actions;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private double _duration;
    private double _in;
    private double _out;
    private bool _playing;
    private bool _dragging;
    private bool _updating;

    private PlayerWindow(ClipItem item, ClipActions actions)
    {
        InitializeComponent();
        _item = item;
        _actions = actions;
        TitleText.Text = item.FileName;
        SubtitleText.Text = $"{item.GameName} · {VideoMath.FormatSize(item.SizeBytes)}";
        _timer.Tick += (_, _) => UpdatePosition();
        Media.Volume = Volume.Value;
        Media.Source = new Uri(item.Path);
        Loaded += (_, _) =>
        {
            Media.Play();
            Media.Pause();
            Play();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            Media.Stop();
            Media.Close();
            Media.Source = null;
        };
        PreviewKeyDown += OnKey;
        StateChanged += (_, _) => Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        SizeChanged += (_, _) => UpdateTrimBand();
    }

    public static void Open(ClipItem item, AppServices services, ClipActions actions)
    {
        var window = new PlayerWindow(item, actions);
        if (Application.Current.MainWindow is { IsVisible: true } owner && owner != window)
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        window.Show();
        window.Activate();
    }

    private void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        _duration = Media.NaturalDuration.HasTimeSpan ? Media.NaturalDuration.TimeSpan.TotalSeconds : Math.Max(1, _item.Meta.DurationSeconds);
        Seek.Maximum = Math.Max(0.1, _duration);
        _in = 0;
        _out = _duration;
        UpdateTrimTexts();
    }

    private void OnMediaEnded(object sender, RoutedEventArgs e)
    {
        Pause();
        Media.Position = TimeSpan.FromSeconds(_in);
        UpdatePosition();
    }

    private void OnMediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        ErrorText.Text = "Bu video oynatılamadı. HEVC veya AV1 kliplerini oynatmak için Microsoft Store'dan " +
                         "\"HEVC Video Uzantıları\" / \"AV1 Video Uzantısı\" gerekebilir. Dosyayı klasöründen başka bir oynatıcıyla da açabilirsin.";
        ErrorText.Visibility = Visibility.Visible;
        PausedOverlay.Visibility = Visibility.Collapsed;
    }

    private void Play()
    {
        Media.Play();
        _playing = true;
        _timer.Start();
        PlayButton.Content = "";
        PausedOverlay.Visibility = Visibility.Collapsed;
    }

    private void Pause()
    {
        Media.Pause();
        _playing = false;
        _timer.Stop();
        PlayButton.Content = "";
        PausedOverlay.Visibility = ErrorText.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Toggle()
    {
        if (_playing)
        {
            Pause();
        }
        else
        {
            if (Media.Position.TotalSeconds >= _duration - 0.05)
            {
                Media.Position = TimeSpan.FromSeconds(_in);
            }

            Play();
        }
    }

    private void UpdatePosition()
    {
        var position = Media.Position.TotalSeconds;
        if (!_dragging)
        {
            _updating = true;
            Seek.Value = position;
            _updating = false;
        }

        TimeText.Text = $"{VideoMath.FormatDuration(position)} / {VideoMath.FormatDuration(_duration)}";
    }

    private void OnPlayPause(object sender, RoutedEventArgs e) => Toggle();

    private void OnVideoClick(object sender, MouseButtonEventArgs e) => Toggle();

    private void OnSeekDown(object sender, MouseButtonEventArgs e) => _dragging = true;

    private void OnSeekUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        Media.Position = TimeSpan.FromSeconds(Seek.Value);
        UpdatePosition();
    }

    private void OnSeekChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_updating && _dragging)
        {
            Media.Position = TimeSpan.FromSeconds(e.NewValue);
            TimeText.Text = $"{VideoMath.FormatDuration(e.NewValue)} / {VideoMath.FormatDuration(_duration)}";
        }
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Media is not null)
        {
            Media.Volume = e.NewValue;
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space:
                Toggle();
                break;
            case Key.Left:
                Media.Position = TimeSpan.FromSeconds(Math.Max(0, Media.Position.TotalSeconds - 5));
                UpdatePosition();
                break;
            case Key.Right:
                Media.Position = TimeSpan.FromSeconds(Math.Min(_duration, Media.Position.TotalSeconds + 5));
                UpdatePosition();
                break;
            case Key.I:
                OnMarkIn(this, new RoutedEventArgs());
                break;
            case Key.O:
                OnMarkOut(this, new RoutedEventArgs());
                break;
            case Key.Escape:
                Close();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void OnMarkIn(object sender, RoutedEventArgs e)
    {
        _in = Math.Min(Media.Position.TotalSeconds, Math.Max(0, _out - 0.5));
        UpdateTrimTexts();
    }

    private void OnMarkOut(object sender, RoutedEventArgs e)
    {
        _out = Math.Max(Media.Position.TotalSeconds, _in + 0.5);
        UpdateTrimTexts();
    }

    private void UpdateTrimTexts()
    {
        InText.Text = VideoMath.FormatDuration(_in);
        OutText.Text = VideoMath.FormatDuration(_out);
        TrimButton.IsEnabled = _in > 0.05 || _out < _duration - 0.05;
        UpdateTrimBand();
    }

    private void UpdateTrimBand()
    {
        if (_duration <= 0 || TrimLayer.ActualWidth <= 0)
        {
            return;
        }

        var width = TrimLayer.ActualWidth;
        Canvas.SetLeft(TrimBand, width * _in / _duration);
        TrimBand.Width = Math.Max(2, width * (_out - _in) / _duration);
    }

    private async void OnTrim(object sender, RoutedEventArgs e)
    {
        TrimButton.IsEnabled = false;
        try
        {
            await _actions.TrimAsync(_item, _in, _out);
        }
        finally
        {
            TrimButton.IsEnabled = true;
        }
    }

    private async void OnCompress10(object sender, RoutedEventArgs e) => await _actions.CompressAsync(_item, 10);

    private async void OnCompress50(object sender, RoutedEventArgs e) => await _actions.CompressAsync(_item, 50);

    private async void OnGif(object sender, RoutedEventArgs e)
    {
        var length = _out - _in;
        var start = length > 15 ? Math.Max(_in, _out - 15) : _in;
        await _actions.GifAsync(_item, start, Math.Min(15, length));
    }

    private void OnCopy(object sender, RoutedEventArgs e) => _actions.Copy(_item);

    private void OnShowInFolder(object sender, RoutedEventArgs e) => _actions.ShowInFolder(_item);

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        Media.Stop();
        Media.Close();
        Media.Source = null;
        if (_actions.Delete(_item))
        {
            Close();
        }
        else
        {
            Media.Source = new Uri(_item.Path);
        }
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
