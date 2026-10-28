using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Hindsight.App.Services;
using Hindsight.App.Views;
using Hindsight.Core;
using Hindsight.Core.Clips;
using Hindsight.Core.Media;

namespace Hindsight.App.ViewModels;

/// <summary>Everything you can do with a saved clip. Shared by the home page, the clips page and the player.</summary>
public sealed class ClipActions(AppServices services, Action<Notice> notify)
{
    public void Notify(string title, string message, NoticeKind kind, string? path = null) => notify(new Notice(title, message, kind, path));

    public void Open(ClipCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        if (!File.Exists(card.Item.Path))
        {
            Notify("Dosya bulunamadı", "Klip taşınmış ya da silinmiş olabilir.", NoticeKind.Warning);
            services.Clips.Refresh();
            return;
        }

        if (card.IsVideo)
        {
            PlayerWindow.Open(card.Item, services, this);
        }
        else
        {
            Shell(card.Item.Path);
        }
    }

    public void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Shell(folder);
    }

    public void ShowInFolder(ClipItem item)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Path}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Notify("Klasör açılamadı", ex.Message, NoticeKind.Error);
        }
    }

    /// <summary>Puts the file on the clipboard, ready to paste into Discord, WhatsApp Web or Explorer.</summary>
    public void Copy(ClipItem item)
    {
        try
        {
            Clipboard.SetFileDropList(new StringCollection { item.Path });
            Notify("Panoya kopyalandı", "Discord'a veya bir klasöre yapıştırabilirsin (Ctrl+V).", NoticeKind.Info);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            Notify("Kopyalanamadı", ex.Message, NoticeKind.Error);
        }
    }

    public void Rename(ClipItem item)
    {
        if (DialogWindow.Prompt("Yeniden adlandır", "Dosya adı", item.FileName) is not { Length: > 0 } name || name == item.FileName)
        {
            return;
        }

        var (_, error) = services.Clips.Rename(item, name);
        if (error is not null)
        {
            Notify("Yeniden adlandırılamadı", error, NoticeKind.Warning);
        }
    }

    public void ToggleFavorite(ClipCardViewModel card)
    {
        services.Clips.SetFavorite(card.Item, !card.Item.Meta.Favorite);
        card.Refresh();
    }

    public bool Delete(ClipItem item)
    {
        if (services.Clips.Recycle(item))
        {
            Notify("Geri Dönüşüm Kutusu'na taşındı", item.FileName, NoticeKind.Info);
            return true;
        }

        Notify("Silinemedi", "Dosya başka bir uygulamada açık olabilir.", NoticeKind.Error);
        return false;
    }

    public async Task CompressAsync(ClipItem item, double megabytes)
    {
        var tools = services.Capture.Tools;
        if (tools is null)
        {
            return;
        }

        var duration = item.Meta.DurationSeconds;
        if (duration <= 0 && await tools.ProbeAsync(item.Path) is { } info)
        {
            duration = info.DurationSeconds;
        }

        if (duration <= 0)
        {
            Notify("Süre okunamadı", item.FileName, NoticeKind.Error);
            return;
        }

        var output = Sibling(item.Path, $"{item.FileName} ({megabytes:0} MB)", ".mp4");
        Notify("Küçültülüyor…", $"{megabytes:0} MB hedefi · birkaç saniye sürebilir", NoticeKind.Info);
        var encoder = services.Capture.Recorder?.Session?.Variant.Encoder;
        var result = await tools.CompressAsync(item.Path, output, megabytes, duration, encoder);
        AddResult(item, output, result, ClipKind.Clip, $"{megabytes:0} MB'a sığacak şekilde küçültüldü");
    }

    public async Task TrimAsync(ClipItem item, double start, double end)
    {
        var tools = services.Capture.Tools;
        if (tools is null)
        {
            return;
        }

        var output = Sibling(item.Path, $"{item.FileName} (kırpılmış)", ".mp4");
        var encoder = services.Capture.Recorder?.Session?.Variant.Encoder;
        var kbps = Math.Max(6000, (int)(item.SizeBytes * 8 / 1000 / Math.Max(1, item.Meta.DurationSeconds)));
        var result = await tools.TrimAsync(item.Path, output, start, end, encoder, kbps);
        AddResult(item, output, result, item.Meta.Kind, $"{VideoMath.FormatDuration(end - start)} uzunluğunda yeni klip");
    }

    public async Task GifAsync(ClipItem item, double start, double seconds)
    {
        var tools = services.Capture.Tools;
        if (tools is null)
        {
            return;
        }

        var output = Sibling(item.Path, item.FileName, ".gif");
        Notify("GIF hazırlanıyor…", $"{Math.Min(15, seconds):0} sn · 480 px · 15 FPS", NoticeKind.Info);
        var result = await tools.GifAsync(item.Path, output, start, seconds);
        AddResult(item, output, result, ClipKind.Gif, "480 px, 15 FPS");
    }

    private void AddResult(ClipItem source, string output, FfmpegResult result, ClipKind kind, string message)
    {
        if (!result.Success || !File.Exists(output))
        {
            Log.Error($"Clip export failed: {result.ErrorTail}");
            Notify("İşlem başarısız", "Ayrıntılar günlük dosyasında.", NoticeKind.Error);
            return;
        }

        services.Clips.Add(output, new ClipMeta
        {
            GameId = source.Meta.GameId,
            GameName = source.Meta.GameName,
            Kind = kind,
            CreatedAt = DateTime.Now,
        });
        if (services.Clips.Items.FirstOrDefault(i => i.Path == output) is { } added && services.Capture.Thumbnails is { } thumbs)
        {
            _ = thumbs.EnsureMetadataAsync(added, services.Clips);
        }

        Notify("Hazır", $"{message} · {VideoMath.FormatSize(new FileInfo(output).Length)}", NoticeKind.Success, output);
    }

    private static string Sibling(string path, string name, string extension)
    {
        var folder = Path.GetDirectoryName(path)!;
        var target = Path.Combine(folder, AppPaths.SafeName(name) + extension);
        for (var i = 2; File.Exists(target); i++)
        {
            target = Path.Combine(folder, $"{AppPaths.SafeName(name)} {i}{extension}");
        }

        return target;
    }

    private void Shell(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Notify("Açılamadı", ex.Message, NoticeKind.Error);
        }
    }
}
