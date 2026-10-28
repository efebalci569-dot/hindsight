using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Hindsight.App.Services;
using Hindsight.Core.Clips;
using Hindsight.Core.Games;
using Hindsight.Core.Media;

namespace Hindsight.App.ViewModels;

/// <summary>A clip/screenshot tile. The thumbnail loads lazily the first time a visible tile asks for it.</summary>
public sealed partial class ClipCardViewModel(ClipItem item, ThumbnailService? thumbnails) : ObservableObject
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");
    private bool _loading;
    private ImageSource? _thumbnail;

    public ClipItem Item { get; } = item;

    public string Title => Item.FileName;

    public string GameName => Item.GameName;

    public bool IsVideo => Item.IsVideo;

    public bool IsImage => Item.IsImage;

    public bool IsRecording => Item.Meta.Kind == ClipKind.Recording;

    public bool IsFavorite
    {
        get => Item.Meta.Favorite;
        set
        {
            if (Item.Meta.Favorite != value)
            {
                Item.Meta.Favorite = value;
                OnPropertyChanged();
            }
        }
    }

    public string Badge => Item.Meta.Kind switch
    {
        ClipKind.Screenshot => "Ekran görüntüsü",
        ClipKind.Gif => "GIF",
        _ => Item.Meta.DurationSeconds > 0 ? VideoMath.FormatDuration(Item.Meta.DurationSeconds) : "Video",
    };

    public string KindGlyph => Item.Meta.Kind switch
    {
        ClipKind.Screenshot => "",
        ClipKind.Gif => "",
        ClipKind.Recording => "",
        _ => "",
    };

    public string DateText => FormatDate(Item.Meta.CreatedAt);

    public string SizeText => VideoMath.FormatSize(Item.SizeBytes);

    public string Details
    {
        get
        {
            var parts = new List<string> { DateText, SizeText };
            if (Item.Meta.Height > 0)
            {
                parts.Insert(1, $"{Item.Meta.Height}p");
            }

            return string.Join("  ·  ", parts);
        }
    }

    public ImageSource? Thumbnail
    {
        get
        {
            if (_thumbnail is null && !_loading)
            {
                _loading = true;
                _ = LoadAsync();
            }

            return _thumbnail;
        }
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Badge));
        OnPropertyChanged(nameof(Details));
        OnPropertyChanged(nameof(IsFavorite));
    }

    private async Task LoadAsync()
    {
        try
        {
            var path = Item.IsImage ? Item.Path : thumbnails is null ? null : await thumbnails.GetAsync(Item);
            if (path is not null && await ImageCache.LoadAsync(path, 360) is { } image)
            {
                _thumbnail = image;
                OnPropertyChanged(nameof(Thumbnail));
            }
        }
        catch (Exception ex)
        {
            Hindsight.Core.Log.Warn($"Thumbnail failed: {ex.Message}");
        }
        finally
        {
            _loading = false;
        }
    }

    public static string FormatDate(DateTime date)
    {
        if (date.Date == DateTime.Today)
        {
            return "Bugün " + date.ToString("HH:mm", Tr);
        }

        if (date.Date == DateTime.Today.AddDays(-1))
        {
            return "Dün " + date.ToString("HH:mm", Tr);
        }

        return date.ToString(date.Year == DateTime.Today.Year ? "d MMM HH:mm" : "d MMM yyyy", Tr);
    }
}

/// <summary>A game tile in the library.</summary>
public sealed partial class GameCardViewModel : ObservableObject
{
    private static readonly (string A, string B)[] Gradients =
    [
        ("#3B1D4A", "#1A1428"), ("#1D3A4A", "#121D28"), ("#4A2A1D", "#28161A"), ("#1D4A35", "#12261E"),
        ("#4A1D2E", "#28121B"), ("#2E1D4A", "#18122A"), ("#3F3A1D", "#24200F"), ("#1D2E4A", "#111A2B"),
    ];

    private ImageSource? _cover;
    private bool _coverLoading;
    private ImageSource? _icon;
    private bool _iconLoading;

    public GameCardViewModel(GameEntry game)
    {
        Game = game;
        var hash = Math.Abs(StringComparer.OrdinalIgnoreCase.GetHashCode(game.Name)) % Gradients.Length;
        var (a, b) = Gradients[hash];
        Background = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(a), (Color)ColorConverter.ConvertFromString(b), 60);
        Background.Freeze();
    }

    public GameEntry Game { get; }

    public string Name => Game.Name;

    public string SourceName => Game.SourceDisplayName;

    public Brush Background { get; }

    public string Initials
    {
        get
        {
            var words = Game.Name.Split([' ', '-', ':', '_'], StringSplitOptions.RemoveEmptyEntries).Where(w => char.IsLetterOrDigit(w[0])).ToList();
            return words.Count switch
            {
                0 => "?",
                1 => words[0][..Math.Min(2, words[0].Length)].ToUpperInvariant(),
                _ => string.Concat(words.Take(2).Select(w => char.ToUpperInvariant(w[0]))),
            };
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    public partial int ClipCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    public partial string PlaytimeText { get; set; } = string.Empty;

    /// <summary>Line under the tile: play time if known, else clip count, else the launcher.</summary>
    public string Subtitle => PlaytimeText.Length > 0
        ? $"{PlaytimeText} oynandı"
        : ClipCount > 0 ? $"{ClipCount} klip" : SourceName;

    [ObservableProperty]
    public partial string LastPlayedText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial bool IsExcluded { get; set; }

    private string? _coverPath;

    public bool HasCover => (_coverPath ?? Game.CoverImage) is not null;

    public ImageSource? Cover
    {
        get
        {
            if (_cover is null && !_coverLoading)
            {
                _coverLoading = true;
                _ = Task.Run(async () =>
                {
                    // Steam's own cache first; otherwise one download from Steam's CDN (cached on disk).
                    var path = Game.CoverImage ?? await SteamArt.GetCoverAsync(Game.Id);
                    if (path is null)
                    {
                        return;
                    }

                    _coverPath = path;
                    _cover = await ImageCache.LoadAsync(path, 300);
                    OnPropertyChanged(nameof(HasCover));
                    OnPropertyChanged(nameof(Cover));
                });
            }

            return _cover;
        }
    }

    public ImageSource? Icon
    {
        get
        {
            if (_icon is null && !_iconLoading && Game.CoverImage is null && !Game.Id.StartsWith("steam:", StringComparison.Ordinal))
            {
                _iconLoading = true;
                _ = Task.Run(async () =>
                {
                    var path = IconExtractor.GetCachedIcon(Game.Id, Game.IconSource ?? Game.ExecutablePath);
                    if (path is not null)
                    {
                        _icon = await ImageCache.LoadAsync(path, 96);
                        OnPropertyChanged(nameof(Icon));
                    }
                });
            }

            return _icon;
        }
    }
}

/// <summary>Section header in a virtualized list. Badge = short text in a coloured tile (platform or game initials).</summary>
public sealed record HeaderRow(string Title, string Subtitle, string Badge = "", string BadgeColor = "#2A2A36")
{
    public System.Windows.Media.Brush BadgeBrush { get; } = Frozen(BadgeColor);

    private static System.Windows.Media.Brush Frozen(string color)
    {
        var brush = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}

public sealed record ClipRow(IReadOnlyList<ClipCardViewModel> Items);

public sealed record GameRow(IReadOnlyList<GameCardViewModel> Items);

public sealed record Option<T>(T Value, string Label);
