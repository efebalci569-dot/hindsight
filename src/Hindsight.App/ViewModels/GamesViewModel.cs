using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hindsight.App.Services;
using Hindsight.App.Views;
using Hindsight.Core.Games;
using Hindsight.Core.Media;

namespace Hindsight.App.ViewModels;

/// <summary>The game library, one section per launcher. Rows are built for a virtualized list.</summary>
public sealed partial class GamesViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly MainViewModel _main;
    private List<GameCardViewModel> _cards = [];
    private int _columns = 5;

    public GamesViewModel(AppServices services, MainViewModel main)
    {
        _services = services;
        _main = main;
        _services.Games.Changed += OnLibraryChanged;
        Reload();
    }

    public ObservableCollection<object> Rows { get; } = [];

    [ObservableProperty]
    public partial string Search { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowHidden { get; set; }

    [ObservableProperty]
    public partial bool IsScanning { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial GameCardViewModel? Selected { get; set; }

    public bool HasSelection => Selected is not null;

    public bool IsEmpty => _cards.Count == 0;

    partial void OnSearchChanged(string value) => BuildRows();

    partial void OnShowHiddenChanged(bool value) => BuildRows();

    partial void OnSelectedChanged(GameCardViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedPath));
        OnPropertyChanged(nameof(SelectedCanLaunch));
        OnPropertyChanged(nameof(SelectedCanRemove));
        OnPropertyChanged(nameof(SelectedHiddenLabel));
    }

    public string? SelectedPath => Selected?.Game.InstallDirectory ?? Selected?.Game.ExecutablePath;

    public bool SelectedCanLaunch => Selected is not null && GameLauncher.CanLaunch(Selected.Game);

    public bool SelectedCanRemove => Selected?.Game.Source is GameSource.Manual or GameSource.Detected;

    public string SelectedHiddenLabel => Selected?.Game.Hidden == true ? "Kütüphanede göster" : "Kütüphaneden gizle";

    public void Activate()
    {
        RefreshStats();
        if (_services.Games.LastScan is null && !IsScanning)
        {
            _ = RescanAsync();
        }
    }

    public void SetColumns(int columns)
    {
        columns = Math.Max(1, columns);
        if (columns != _columns)
        {
            _columns = columns;
            BuildRows();
        }
    }

    private void OnLibraryChanged(object? sender, EventArgs e) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(Reload);

    private void Reload()
    {
        var selectedId = Selected?.Game.Id;
        _cards = _services.Games.Games.Select(g => new GameCardViewModel(g)).ToList();
        RefreshStats();
        Selected = selectedId is null ? null : _cards.FirstOrDefault(c => c.Game.Id == selectedId);
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void RefreshStats()
    {
        var clips = _services.Clips.Items;
        var byId = clips.Where(c => c.Meta.GameId is not null).GroupBy(c => c.Meta.GameId!).ToDictionary(g => g.Key, g => g.Count());
        var byName = clips.GroupBy(c => c.GameName, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var active = _services.Capture.ActiveGame?.Game.Id;
        var excluded = _services.Settings.Current.ExcludedGames;
        foreach (var card in _cards)
        {
            card.ClipCount = byId.TryGetValue(card.Game.Id, out var n) ? n : byName.GetValueOrDefault(card.Game.Name);
            var stats = _services.Playtime.Get(card.Game.Id);
            card.PlaytimeText = stats is null || stats.TotalSeconds < 60
                ? string.Empty
                : stats.TotalSeconds >= 3600 ? $"{stats.TotalSeconds / 3600:0.#} saat" : $"{stats.TotalSeconds / 60:0} dk";
            card.LastPlayedText = stats?.LastPlayed is { } last ? ClipCardViewModel.FormatDate(last) : "Henüz oynanmadı";
            card.IsRunning = card.Game.Id == active;
            card.IsExcluded = excluded.Contains(card.Game.Id, StringComparer.OrdinalIgnoreCase);
        }

        BuildRows();
    }

    private void BuildRows()
    {
        Rows.Clear();
        var query = Search.Trim();
        var visible = _cards.Where(c => (ShowHidden || !c.Game.Hidden) &&
                                        (query.Length == 0 || c.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
            .ToList();
        foreach (var group in visible.GroupBy(c => c.Game.Source).OrderBy(g => g.Key))
        {
            var items = group.OrderByDescending(c => c.IsRunning).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            var (badge, color) = PlatformBadge(group.Key);
            Rows.Add(new HeaderRow(GameEntry.SourceName(group.Key), $"{items.Count} oyun", badge, color));
            foreach (var chunk in items.Chunk(_columns))
            {
                Rows.Add(new GameRow(chunk));
            }
        }

        var hidden = _cards.Count(c => c.Game.Hidden);
        Summary = $"{_cards.Count - hidden} oyun · {visible.Select(c => c.Game.Source).Distinct().Count()} platform" +
                  (hidden > 0 ? $" · {hidden} gizli" : string.Empty);
    }

    private static (string Badge, string Color) PlatformBadge(GameSource source) => source switch
    {
        GameSource.Steam => ("S", "#1A6FD8"),
        GameSource.Epic => ("E", "#3A3A44"),
        GameSource.Gog => ("G", "#7B3FD1"),
        GameSource.Ubisoft => ("U", "#1E5BD6"),
        GameSource.Ea => ("EA", "#E0303A"),
        GameSource.BattleNet => ("B", "#1497E0"),
        GameSource.Riot => ("R", "#D8323A"),
        GameSource.Rockstar => ("R", "#E8A317"),
        GameSource.Xbox => ("X", "#1F8A3A"),
        GameSource.Detected => ("⚡", "#C2410C"),
        _ => ("+", "#4B4B5A"),
    };

    [RelayCommand]
    private async Task RescanAsync()
    {
        if (IsScanning)
        {
            return;
        }

        IsScanning = true;
        try
        {
            await _services.Games.ScanAsync();
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private void AddGame()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Oyunun .exe dosyasını seç",
            Filter = "Oyun (*.exe)|*.exe",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() == true)
        {
            var game = _services.Games.AddManual(dialog.FileName);
            Selected = _cards.FirstOrDefault(c => c.Game.Id == game.Id);
        }
    }

    [RelayCommand]
    private void Select(GameCardViewModel? card) => Selected = card;

    [RelayCommand]
    private void CloseDetail() => Selected = null;

    [RelayCommand]
    private void Launch()
    {
        if (Selected is not null && GameLauncher.Launch(Selected.Game) is { } error)
        {
            _main.Actions.Notify("Başlatılamadı", error, Hindsight.Core.NoticeKind.Warning);
        }
    }

    [RelayCommand]
    private void ShowClips()
    {
        if (Selected is not null)
        {
            _main.ShowClipsFor(Selected.Game.Name);
        }
    }

    [RelayCommand]
    private void ToggleExcluded()
    {
        if (Selected is null)
        {
            return;
        }

        var id = Selected.Game.Id;
        _services.Settings.Update(s =>
        {
            if (!s.ExcludedGames.Remove(id))
            {
                s.ExcludedGames.Add(id);
            }
        });
        RefreshStats();
    }

    [RelayCommand]
    private void Rename()
    {
        if (Selected is null)
        {
            return;
        }

        if (DialogWindow.Prompt("Oyunu yeniden adlandır", "Yeni ad", Selected.Name) is { Length: > 0 } name)
        {
            _services.Games.Rename(Selected.Game.Id, name);
        }
    }

    [RelayCommand]
    private void ToggleHidden()
    {
        if (Selected is not null)
        {
            _services.Games.SetHidden(Selected.Game.Id, !Selected.Game.Hidden);
        }
    }

    [RelayCommand]
    private void Remove()
    {
        if (Selected is not null && DialogWindow.Confirm("Oyunu listeden kaldır", $"\"{Selected.Name}\" kütüphaneden kaldırılsın mı? Oyun dosyalarına ve kliplerine dokunulmaz."))
        {
            _services.Games.Remove(Selected.Game.Id);
            Selected = null;
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (SelectedPath is { } path)
        {
            _main.Actions.OpenFolder(Directory.Exists(path) ? path : Path.GetDirectoryName(path)!);
        }
    }

    public void Dispose() => _services.Games.Changed -= OnLibraryChanged;
}
