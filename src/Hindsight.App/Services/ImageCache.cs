using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hindsight.Core;

namespace Hindsight.App.Services;

/// <summary>
/// Thumbnails and cover art, decoded on a background thread at display size (not full size) and kept in a
/// small LRU. Bitmaps are frozen and read fully, so files are never locked.
/// </summary>
public static class ImageCache
{
    private const int Capacity = 160;
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, LinkedListNode<(string Key, ImageSource Image)>> Map = [];
    private static readonly LinkedList<(string Key, ImageSource Image)> Order = new();
    private static readonly SemaphoreSlim Decoders = new(2, 2);

    public static ImageSource? TryGet(string path, int width)
    {
        var key = $"{width}|{path}";
        lock (Gate)
        {
            if (Map.TryGetValue(key, out var node))
            {
                Order.Remove(node);
                Order.AddFirst(node);
                return node.Value.Image;
            }
        }

        return null;
    }

    public static async Task<ImageSource?> LoadAsync(string path, int width)
    {
        if (TryGet(path, width) is { } cached)
        {
            return cached;
        }

        await Decoders.WaitAsync().ConfigureAwait(false);
        try
        {
            var image = await Task.Run(() => Decode(path, width)).ConfigureAwait(false);
            if (image is null)
            {
                return null;
            }

            var key = $"{width}|{path}";
            lock (Gate)
            {
                if (!Map.ContainsKey(key))
                {
                    Map[key] = Order.AddFirst((key, image));
                    while (Order.Count > Capacity)
                    {
                        Map.Remove(Order.Last!.Value.Key);
                        Order.RemoveLast();
                    }
                }
            }

            return image;
        }
        finally
        {
            Decoders.Release();
        }
    }

    public static ImageSource? Decode(string path, int width)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var bytes = File.ReadAllBytes(path);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = width;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            Log.Warn($"Image decode failed ({Path.GetFileName(path)}): {ex.Message}");
            return null;
        }
    }

    /// <summary>Drops everything (called when the window closes to the tray).</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            Map.Clear();
            Order.Clear();
        }
    }
}
