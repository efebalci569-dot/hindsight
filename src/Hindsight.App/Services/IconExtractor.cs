using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Hindsight.Core;

namespace Hindsight.App.Services;

/// <summary>Pulls a game's icon out of its exe once and caches it as a PNG under %LOCALAPPDATA%\Hindsight\art.</summary>
public static class IconExtractor
{
    public static string? GetCachedIcon(string gameId, string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        if (source.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || source.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
        {
            return File.Exists(source) ? source : null; // already an image (e.g. an Xbox package logo)
        }

        var target = Path.Combine(AppPaths.ArtDir, AppPaths.SafeName(gameId.Replace(':', '_'), "game") + ".png");
        if (File.Exists(target))
        {
            return target;
        }

        try
        {
            if (!File.Exists(source))
            {
                return null;
            }

            using var icon = source.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)
                ? new Icon(source, 256, 256)
                : Icon.ExtractAssociatedIcon(source);
            if (icon is null)
            {
                return null;
            }

            using var bitmap = icon.ToBitmap();
            bitmap.Save(target, ImageFormat.Png);
            return target;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or ExternalException)
        {
            return null;
        }
    }
}
