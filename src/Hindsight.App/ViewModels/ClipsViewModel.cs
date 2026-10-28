using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hindsight.App.Services;
using Hindsight.Core.Clips;
using Hindsight.Core.Media;

namespace Hindsight.App.ViewModels;

public enum ClipKindFilter { All, Videos, Screenshots }

public enum ClipSort { Newest, Oldest, Largest, Longest }

public sealed partial class GameFilterItem(string? key, string name, int count, string glyph) : ObservableObject
{
    /// <summary>null = all clips, "*fav" = favourites, otherwise the game name.</summary>
    public string? Key { get; } = key;

    public string Name { get; } = name;

    public int Count { get; } = count;

    public string Glyph { get; } = glyph;
}

/// <summary>Clips, grouped game by game. Uses virtualized rows so thousands of clips stay light on memory.</summary>
public sealed partial class ClipsViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _debounce;
    private Dictionary<string, ClipCardViewModel> _cards = new(StringComparer.OrdinalIgnoreCase);
    private int _columns = 4;
    private string? _pendingGame;

    public ClipsViewModel(AppServices services, MainViewModel main)
    {
        _services = services;
        _main = main;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Rebuild();
        };
        _services.Clips.Changed += OnLibraryChanged;
        Rebuild();
    }

    public ClipActions Actions => _main.Actions;

    public ObservableCollection<GameFilterItem> Filters { get; } = [];

    public ObservableCollection<object> Rows { get; } = [];

    public IReadOnlyList<Option<ClipSort>> SortOptions { get; } =
    [
        new(ClipSort.Newest, "En yeni"), new(ClipSort.Oldest, "En eski"), new(ClipSort.Largest, "En büyük"), new(ClipSort.Longest, "En uzun"),
    ];

    [ObservableProperty]
    public partial GameFilterItem? SelectedFilter { get; set; }

    [ObservableProperty]
    public partial ClipKindFilter Kind { get; set; }

    [ObservableProperty]
    public partial ClipSort Sort { get; set; }

    [ObservableProperty]
    public partial string Search { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Title { get; set; } = "Tüm klipler";

    public bool IsEmpty => Rows.Count == 0;

    public bool KindAll
    {
        get => Kind == ClipKindFilter.All;
        set { if (value) Kind = ClipKindFilter.All; }
    }

    public bool KindVideos
    {
        get => Kind == ClipKindFilter.Videos;
        set { if (value) Kind = ClipKindFilter.Videos; }
    }

    public bool KindScreenshots
    {
        get => Kind == ClipKindFilter.Screenshots;
        set { if (value) Kind = ClipKindFilter.Screenshots; }
    }

    partial void OnSelectedFilterChanged(GameFilterItem? value) => BuildRows();

    partial void OnKindChanged(ClipKindFilter value)
    {
        OnPropertyChanged(nameof(KindAll));
        OnPropertyChanged(nameof(KindVideos));
        OnPropertyChanged(nameof(KindScreenshots));
        BuildRows();
    }

    partial void OnSortChanged(ClipSort value) => BuildRows();

    partial void OnSearchChanged(string value) => BuildRows();

    public void Activate() => _ = Task.Run(() => _services.Clips.Refresh());

    public void SelectGame(string? gameName)
    {
        _pendingGame = gameName;
        SelectedFilter = Filters.FirstOrDefault(f => string.Equals(f.Key, gameName, StringComparison.OrdinalIgnoreCase)) ?? Filters.FirstOrDefault();
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
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _debounce.Stop();
            _debounce.Start();
        });

    private void Rebuild()
    {
        var items = _services.Clips.Items;
        var fresh = new Dictionary<string, ClipCardViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            fresh[item.Path] = _cards.TryGetValue(item.Path, out var card) && ReferenceEquals(card.Item, item)
                ? card
                : new ClipCardViewModel(item, _services.Capture.Thumbnails);
        }

        _cards = fresh;
        var selectedKey = _pendingGame ?? SelectedFilter?.Key;
        _pendingGame = null;
        Filters.Clear();
        Filters.Add(new GameFilterItem(null, "Tüm klipler", items.Count, ""));
        Filters.Add(new GameFilterItem("*fav", "Favoriler", items.Count(i => i.Meta.Favorite), ""));
        foreach (var game in items.GroupBy(i => i.GameName, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Max(i => i.Meta.CreatedAt)))
        {
            Filters.Add(new GameFilterItem(game.Key, game.Key, game.Count(), ""));
        }

        var match = Filters.FirstOrDefault(f => string.Equals(f.Key, selectedKey, StringComparison.OrdinalIgnoreCase)) ?? Filters[0];
        if (ReferenceEquals(match, SelectedFilter))
        {
            BuildRows();
        }
        else
        {
            SelectedFilter = match; // triggers BuildRows
        }

        // Fill in durations of clips that were copied into the folder by hand.
        if (_services.Capture.Thumbnails is { } thumbs)
        {
            var missing = items.Where(i => i.IsVideo && i.Meta.DurationSeconds <= 0).Take(50).ToList();
            if (missing.Count > 0)
            {
                _ = Task.Run(async () =>
                {
                    foreach (var item in missing)
                    {
                        await thumbs.EnsureMetadataAsync(item, _services.Clips);
                    }

                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        foreach (var item in missing)
                        {
                            if (_cards.TryGetValue(item.Path, out var c))
                            {
                                c.Refresh();
                            }
                        }
                    });
                });
            }
        }
    }

    private void BuildRows()
    {
        Rows.Clear();
        var filter = SelectedFilter;
        var query = Search.Trim();
        IEnumerable<ClipCardViewModel> cards = _cards.Values;
        if (filter?.Key == "*fav")
        {
            cards = cards.Where(c => c.Item.Meta.Favorite);
        }
        else if (filter?.Key is { } game)
        {
            cards = cards.Where(c => string.Equals(c.GameName, game, StringComparison.OrdinalIgnoreCase));
        }

        cards = Kind switch
        {
            ClipKindFilter.Videos => cards.Where(c => c.IsVideo),
            ClipKindFilter.Screenshots => cards.Where(c => c.IsImage),
            _ => cards,
        };
        if (query.Length > 0)
        {
            cards = cards.Where(c => c.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                                     c.GameName.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        }

        cards = Sort switch
        {
            ClipSort.Oldest => cards.OrderBy(c => c.Item.Meta.CreatedAt),
            ClipSort.Largest => cards.OrderByDescending(c => c.Item.SizeBytes),
            ClipSort.Longest => cards.OrderByDescending(c => c.Item.Meta.DurationSeconds),
            _ => cards.OrderByDescending(c => c.Item.Meta.CreatedAt),
        };

        var list = cards.ToList();
        if (filter?.Key is null)
        {
            // "All clips": one section per game, most recently played first.
            foreach (var group in list.GroupBy(c => c.GameName, StringComparer.OrdinalIgnoreCase)
                         .OrderByDescending(g => Sort == ClipSort.Newest ? g.Max(c => c.Item.Meta.CreatedAt) : DateTime.MinValue))
            {
                Rows.Add(new HeaderRow(group.Key, Describe(group.ToList())));
                foreach (var chunk in group.Chunk(_columns))
                {
                    Rows.Add(new ClipRow(chunk));
                }
            }
        }
        else
        {
            foreach (var chunk in list.Chunk(_columns))
            {
                Rows.Add(new ClipRow(chunk));
            }
        }

        Title = filter?.Name ?? "Tüm klipler";
        Summary = Describe(list);
        OnPropertyChanged(nameof(IsEmpty));
    }

    private static string Describe(IReadOnlyCollection<ClipCardViewModel> cards)
    {
        var videos = cards.Count(c => c.IsVideo);
        var images = cards.Count - videos;
        var parts = new List<string>();
        if (videos > 0) parts.Add($"{videos} video");
        if (images > 0) parts.Add($"{images} görüntü");
        parts.Add(VideoMath.FormatSize(cards.Sum(c => c.Item.SizeBytes)));
        return string.Join(" · ", parts);
    }

    [RelayCommand]
    private void Open(ClipCardViewModel? card) => Actions.Open(card);

    [RelayCommand]
    private void ShowInFolder(ClipCardViewModel? card)
    {
        if (card is not null) Actions.ShowInFolder(card.Item);
    }

    [RelayCommand]
    private void Copy(ClipCardViewModel? card)
    {
        if (card is not null) Actions.Copy(card.Item);
    }

    [RelayCommand]
    private void Rename(ClipCardViewModel? card)
    {
        if (card is not null) Actions.Rename(card.Item);
    }

    [RelayCommand]
    private void Favorite(ClipCardViewModel? card)
    {
        if (card is not null) Actions.ToggleFavorite(card);
    }

    [RelayCommand]
    private void Delete(ClipCardViewModel? card)
    {
        if (card is not null) Actions.Delete(card.Item);
    }

    [RelayCommand]
    private async Task Compress10(ClipCardViewModel? card)
    {
        if (card is { IsVideo: true }) await Actions.CompressAsync(card.Item, 10);
    }

    [RelayCommand]
    private async Task Compress50(ClipCardViewModel? card)
    {
        if (card is { IsVideo: true }) await Actions.CompressAsync(card.Item, 50);
    }

    [RelayCommand]
    private async Task MakeGif(ClipCardViewModel? card)
    {
        if (card is { IsVideo: true })
        {
            var duration = card.Item.Meta.DurationSeconds;
            var start = Math.Max(0, duration - 8);
            await Actions.GifAsync(card.Item, start, Math.Min(8, duration > 0 ? duration : 8));
        }
    }

    [RelayCommand]
    private void OpenFolder() => Actions.OpenFolder(SelectedFilter?.Key is { } game && game != "*fav"
        ? System.IO.Path.Combine(_services.Clips.Root, Hindsight.Core.AppPaths.SafeName(game))
        : _services.Clips.Root);

    [RelayCommand]
    private void Refresh() => Activate();

    public void Dispose()
    {
        _debounce.Stop();
        _services.Clips.Changed -= OnLibraryChanged;
    }
}
