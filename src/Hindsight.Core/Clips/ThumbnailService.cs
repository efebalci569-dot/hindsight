using Hindsight.Core.Media;

namespace Hindsight.Core.Clips;

/// <summary>
/// Makes 384-px JPEG thumbnails of video clips on demand, one at a time and below normal priority,
/// and caches them on disk. Screenshots need none: the UI decodes the image itself at thumbnail size.
/// </summary>
public sealed class ThumbnailService(ClipTools tools)
{
    private readonly SemaphoreSlim _one = new(1, 1);

    public static string PathFor(ClipItem item) => Path.Combine(AppPaths.ThumbsDir, item.ThumbnailKey + ".jpg");

    public async Task<string?> GetAsync(ClipItem item, CancellationToken cancellationToken = default)
    {
        if (item.IsImage)
        {
            return item.Path;
        }

        var target = PathFor(item);
        if (File.Exists(target))
        {
            return target;
        }

        await _one.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(target))
            {
                return target;
            }

            var at = item.Meta.DurationSeconds > 0 ? Math.Min(2, item.Meta.DurationSeconds / 3) : 1;
            return await tools.ThumbnailAsync(item.Path, target, at, cancellationToken).ConfigureAwait(false) ? target : null;
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>Fills in duration/size for clips that were not made by Hindsight (copied into the folder).</summary>
    public async Task EnsureMetadataAsync(ClipItem item, ClipLibrary library, CancellationToken cancellationToken = default)
    {
        if (!item.IsVideo || item.Meta.DurationSeconds > 0)
        {
            return;
        }

        if (await tools.ProbeAsync(item.Path, cancellationToken).ConfigureAwait(false) is { } info)
        {
            item.Meta.DurationSeconds = info.DurationSeconds;
            item.Meta.Width = info.Width;
            item.Meta.Height = info.Height;
            library.UpdateMeta(item);
        }
    }

    /// <summary>Deletes cached thumbnails whose clip no longer exists.</summary>
    public static void Prune(IEnumerable<ClipItem> items)
    {
        try
        {
            var keep = items.Select(i => i.ThumbnailKey + ".jpg").ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(AppPaths.ThumbsDir, "*.jpg"))
            {
                if (!keep.Contains(Path.GetFileName(file)))
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
