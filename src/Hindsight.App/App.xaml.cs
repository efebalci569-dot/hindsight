using System.Diagnostics;
using System.IO;
using System.Runtime;
using System.Windows;
using System.Windows.Threading;
using Hindsight.App.Controls;
using Hindsight.App.Services;
using Hindsight.App.ViewModels;
using Hindsight.App.Views;
using Hindsight.Core;
using Hindsight.Core.Native;
using Hindsight.Core.Settings;

namespace Hindsight.App;

/// <summary>
/// Hindsight runs as a tray app. The recorder, game detector and hotkeys live for the whole session;
/// the main window is created when opened and fully destroyed when closed, so an idle Hindsight holds
/// almost no UI memory.
/// </summary>
public partial class App : Application
{
    private const string InstanceName = "Local\\Hindsight.SingleInstance";
    private const string ShowEventName = "Local\\Hindsight.ShowWindow";
    private Mutex? _instance;
    private EventWaitHandle? _showEvent;
    private AppServices? _services;
    private TrayIcon? _tray;
    private HotkeyService? _hotkeys;
    private SoundService? _sounds;
    private ToastWindow? _toast;
    private ClipActions? _actions;
    private MainWindow? _window;
    private MainViewModel? _windowVm;
    private bool _exiting;
    private bool _trayHintShown;

    private static readonly string[] Args = Environment.GetCommandLineArgs();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var dataDir = ArgValue("--data-dir");
        if (dataDir is not null)
        {
            AppPaths.OverrideDataRoot(dataDir);
        }

        _instance = new Mutex(true, InstanceName + (dataDir is null ? string.Empty : ".dev"), out var first);
        if (!first)
        {
            // Already running: ask the first instance to show its window.
            try
            {
                using var show = EventWaitHandle.OpenExisting(ShowEventName);
                show.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }

            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Unhandled", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task", args.Exception);
            args.SetObserved();
        };

        Log.Info($"Hindsight {typeof(App).Assembly.GetName().Version} starting (pid {Environment.ProcessId}).");
        _services = new AppServices();
        if (ArgValue("--capture") is { } capturePath)
        {
            _ = CaptureScreenshotAsync(capturePath);
            return;
        }

        if (Args.Contains("--selftest"))
        {
            _ = SelfTestAsync();
            return;
        }

        _sounds = new SoundService();
        _actions = new ClipActions(_services, ShowNotice);
        _toast = new ToastWindow();

        _tray = new TrayIcon();
        _tray.OpenRequested += (_, _) => ShowMainWindow();
        _tray.ActionRequested += (_, action) => RunAction(action);
        _tray.FolderRequested += (_, _) => _actions.OpenFolder(_services.Clips.Root);
        _tray.ExitRequested += (_, _) => _ = ExitAsync();

        _hotkeys = new HotkeyService();
        _hotkeys.Pressed += (_, action) => RunAction(action);
        ApplyHotkeys(_services.Settings.Current, announce: true);
        HotkeyBox.EditingChanged += (_, editing) =>
        {
            if (editing)
            {
                _hotkeys.Apply(new AppSettings { SaveClipHotkey = "", ToggleRecordingHotkey = "", ScreenshotHotkey = "", ToggleBufferHotkey = "" });
            }
            else
            {
                ApplyHotkeys(_services.Settings.Current, announce: false);
            }
        };

        _services.Settings.Changed += (_, settings) => Dispatcher.BeginInvoke(() =>
        {
            ApplyHotkeys(settings, announce: true);
            UpdateTray();
        });
        _services.Capture.Notified += (_, notice) => Dispatcher.BeginInvoke(() => ShowNotice(notice));
        _services.Capture.StateChanged += (_, _) => Dispatcher.BeginInvoke(UpdateTray);

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => Dispatcher.BeginInvoke(ShowMainWindow), null, -1, false);

        if (_services.Settings.Current.StartWithWindows)
        {
            StartupRegistration.Apply(true); // keeps the path right after an update or move
        }

        // Background start-up work: library scan, clip folder, recorder (probes the GPU once).
        _ = Task.Run(async () =>
        {
            try
            {
                _services.Clips.Refresh();
                await _services.Games.ScanAsync();
                await _services.Capture.InitializeAsync();
                Dispatcher.Invoke(() =>
                {
                    UpdateTray();
                    if (_window is null)
                    {
                        TrimMemory(); // start-up work (scan, probe) is done: give its memory back
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error("Start-up failed", ex);
            }
        });

        var minimized = Args.Contains("--minimized") && _services.Settings.Current.StartMinimized;
        if (!minimized)
        {
            ShowMainWindow();
        }
        else
        {
            TrimMemory();
        }

        UpdateTray();
    }

    /// <summary>
    /// Dev/docs tool: renders the main window off-screen to a PNG and exits. It never records, never
    /// registers hotkeys and never takes focus. Usage: --data-dir X --capture out.png --page games
    /// </summary>
    private async Task CaptureScreenshotAsync(string path)
    {
        var services = _services!;
        var live = Args.Contains("--live"); // also run the recorder (records the screen into the --data-dir)
        services.Clips.Refresh();
        await services.Games.ScanAsync();
        if (live)
        {
            services.Settings.Update(s => s.Mode = RecordingMode.Always);
        }

        await services.Capture.InitializeAsync(startRecording: live);
        var actions = new ClipActions(services, _ => { });
        var vm = new MainViewModel(services, actions, _ => { });
        vm.Navigate(ArgValue("--page") ?? "home");
        var window = new MainWindow(vm)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        if (int.TryParse(ArgValue("--width"), out var w)) window.Width = w;
        if (int.TryParse(ArgValue("--height"), out var h)) window.Height = h;
        window.Show();
        await Task.Delay(TimeSpan.FromSeconds(double.TryParse(ArgValue("--wait"), out var wait) ? wait : 4));
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY,
            System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        await using (var file = File.Create(path))
        {
            encoder.Save(file);
        }

        Log.Info($"Captured {path}; working set {Process.GetCurrentProcess().WorkingSet64 / 1048576} MB.");
        window.Close();
        vm.Dispose();
        if (live)
        {
            await services.Capture.DisposeAsync();
        }

        Shutdown();
    }

    /// <summary>
    /// Dev check of the whole chain without UI: buffer on the desktop → save clip → screenshot → manual recording.
    /// Results go to the log. Usage: --data-dir X --selftest
    /// </summary>
    private async Task SelfTestAsync()
    {
        var services = _services!;
        var capture = services.Capture;
        capture.Notified += (_, n) => Log.Info($"[selftest] notice: {n.Kind} {n.Title} — {n.Message}");
        services.Settings.Update(s => s.Mode = RecordingMode.Always);
        services.Clips.Refresh();
        await capture.InitializeAsync();
        await Task.Delay(TimeSpan.FromSeconds(7));
        Log.Info($"[selftest] buffering={capture.IsBuffering} encoder={capture.Recorder?.Session?.Variant.Encoder.FfmpegName} usage={capture.RecorderUsage()}");
        var clip = await capture.SaveClipAsync();
        Log.Info($"[selftest] clip: {clip?.Path} {clip?.Meta.DurationSeconds:0.0}s {clip?.SizeBytes} bytes");
        var shot = await capture.TakeScreenshotAsync();
        Log.Info($"[selftest] screenshot: {shot?.Path} {shot?.SizeBytes} bytes");
        await capture.ToggleManualRecordingAsync();
        await Task.Delay(TimeSpan.FromSeconds(4));
        await capture.ToggleManualRecordingAsync();
        Log.Info($"[selftest] library now has {services.Clips.Items.Count} items: {string.Join(" | ", services.Clips.Items.Select(i => $"{i.Meta.Kind}:{i.GameName}:{i.Meta.DurationSeconds:0.0}s"))}");
        var test = await capture.TestRecordingAsync();
        Log.Info($"[selftest] settings test-recording: ok={test.Ok} — {test.Message}");
        await capture.DisposeAsync();
        Log.Info($"[selftest] done; ffmpeg left running: {Process.GetProcessesByName("ffmpeg").Length}");
        Shutdown();
    }

    private static string? ArgValue(string name)
    {
        var index = Array.IndexOf(Args, name);
        return index >= 0 && index + 1 < Args.Length ? Args[index + 1] : null;
    }

    public void ShowMainWindow()
    {
        if (_services is null || _actions is null || _exiting)
        {
            return;
        }

        if (_window is null)
        {
            _windowVm = new MainViewModel(_services, _actions, RunAction);
            if (ArgValue("--page") is { } page)
            {
                _windowVm.Navigate(page);
            }

            _window = new MainWindow(_windowVm);
            _window.Closing += OnWindowClosing;
            _window.Closed += OnWindowClosed;
            MainWindow = _window;
            _window.Show();
        }

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
        _window.Topmost = true; // bring to front over other apps once
        _window.Topmost = false;
        _window.Focus();
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting || _services is null)
        {
            return;
        }

        if (!_services.Settings.Current.CloseToTray)
        {
            e.Cancel = true;
            _ = ExitAsync();
            return;
        }

        if (!_trayHintShown && !_services.Settings.Current.FirstRunCompleted)
        {
            _trayHintShown = true;
            _tray?.ShowBalloon("Hindsight çalışmaya devam ediyor", $"Oyun açınca kayıt başlar. {_services.Settings.Current.SaveClipHotkey} ile klip kaydet.");
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _windowVm?.Dispose();
        _windowVm = null;
        _window = null;
        MainWindow = null;
        // Close any open players too: everything UI goes away while in the tray.
        foreach (var player in Windows.OfType<PlayerWindow>().ToList())
        {
            player.Close();
        }

        TrimMemory();
    }

    /// <summary>Drops cached images, compacts the heap and hands unused pages back to Windows.</summary>
    private void TrimMemory()
    {
        if (_services?.Settings.Current.TrimMemoryInTray != true)
        {
            return;
        }

        ImageCache.Clear();
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect();
            WindowInfo.TrimWorkingSet();
            Log.Info($"UI unloaded; working set now {Process.GetCurrentProcess().WorkingSet64 / 1048576} MB.");
        });
    }

    private void RunAction(HotkeyAction action)
    {
        if (_services is null)
        {
            return;
        }

        var capture = _services.Capture;
        _ = action switch
        {
            HotkeyAction.SaveClip => capture.SaveClipAsync(),
            HotkeyAction.ToggleRecording => capture.ToggleManualRecordingAsync(),
            HotkeyAction.Screenshot => capture.TakeScreenshotAsync(),
            HotkeyAction.ToggleBuffer => capture.ToggleBufferAsync(),
            _ => Task.CompletedTask,
        };
    }

    private void ShowNotice(Notice notice)
    {
        var settings = _services?.Settings.Current;
        if (settings is null)
        {
            return;
        }

        if (settings.PlaySaveSound)
        {
            switch (notice.Kind)
            {
                case NoticeKind.Success:
                    _sounds?.Success();
                    break;
                case NoticeKind.Error:
                    _sounds?.Error();
                    break;
                case NoticeKind.Info when notice.Title.StartsWith("Kayıt başladı", StringComparison.Ordinal):
                    _sounds?.Start();
                    break;
            }
        }

        if (settings.ShowSaveToast || notice.Kind == NoticeKind.Error)
        {
            _toast?.Show(notice);
        }

        if (notice.Kind == NoticeKind.Error)
        {
            Log.Warn($"{notice.Title}: {notice.Message}");
        }
    }

    private void ApplyHotkeys(AppSettings settings, bool announce)
    {
        if (_hotkeys is null)
        {
            return;
        }

        var failed = _hotkeys.Apply(settings);
        if (announce && failed.Count > 0)
        {
            ShowNotice(new Notice("Kısayol kullanılamıyor",
                $"{string.Join(", ", failed)} başka bir uygulama tarafından kullanılıyor. Ayarlar > Kısayollar'dan değiştirebilirsin.", NoticeKind.Warning));
        }
    }

    private void UpdateTray()
    {
        if (_tray is null || _services is null)
        {
            return;
        }

        var capture = _services.Capture;
        _tray.Update(capture.IsBuffering, capture.IsManualRecording, capture.BufferGame?.Name, _services.Settings.Current);
    }

    private async Task ExitAsync()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        Log.Info("Exiting.");
        _window?.Close();
        _hotkeys?.Dispose();
        if (_services is not null)
        {
            try
            {
                await _services.Capture.DisposeAsync();
            }
            catch (Exception ex)
            {
                Log.Error("Shutdown", ex);
            }
        }

        _tray?.Dispose();
        _sounds?.Dispose();
        _toast?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _showEvent?.Dispose();
        if (_instance is not null)
        {
            try
            {
                _instance.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }

            _instance.Dispose();
        }

        base.OnExit(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI exception", e.Exception);
        e.Handled = true;
        ShowNotice(new Notice("Beklenmeyen bir hata oldu", e.Exception.Message, NoticeKind.Error));
    }
}
