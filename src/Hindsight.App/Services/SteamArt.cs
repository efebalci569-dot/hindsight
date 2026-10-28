using System.IO;
using System.Net.Http;
using Hindsight.Core;

namespace Hindsight.App.Services;

/// <summary>
/// Cover art for Steam games that Steam itself has not cached yet: downloaded once from Steam's public
/// CDN (only the app id is sent) and kept in %LOCALAPPDATA%\Hindsight\art.
/// </summary>
public static class SteamArt
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly SemaphoreSlim One = new(2, 2);
    private static readonly HashSet<string> Failed = [];

    public static async Task<string?> GetCoverAsync(string gameId)
    {
        if (!gameId.StartsWith("steam:", StringComparison.Ordinal))
        {
            return null;
        }

        var appId = gameId["steam:".Length..];
        if (!appId.All(char.IsAsciiDigit))
        {
            return null;
        }

        var target = Path.Combine(AppPaths.ArtDir, $"steam_{appId}.jpg");
        if (File.Exists(target))
        {
            return target;
        }

        lock (Failed)
        {
            if (Failed.Contains(appId))
            {
                return null;
            }
        }

        await One.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var name in new[] { "library_600x900.jpg", "header.jpg" })
            {
                try
                {
                    var bytes = await Http.GetByteArrayAsync($"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/{name}").ConfigureAwait(false);
                    if (bytes.Length > 1024)
                    {
                        await File.WriteAllBytesAsync(target, bytes).ConfigureAwait(false);
                        return target;
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
                {
                }
            }

            lock (Failed)
            {
                Failed.Add(appId);
            }

            return null;
        }
        finally
        {
            One.Release();
        }
    }
}
