using System.Diagnostics;

namespace Hindsight.Core.Games;

public static class GameLauncher
{
    public static bool CanLaunch(GameEntry game) =>
        game.LaunchUri is not null || (game.ExecutablePath is not null && File.Exists(game.ExecutablePath));

    /// <summary>Starts a game through its launcher URI, or its exe. Returns an error message, or null on success.</summary>
    public static string? Launch(GameEntry game)
    {
        try
        {
            if (game.LaunchUri is not null)
            {
                Process.Start(new ProcessStartInfo(game.LaunchUri) { UseShellExecute = true })?.Dispose();
                return null;
            }

            if (game.ExecutablePath is not null && File.Exists(game.ExecutablePath))
            {
                var info = new ProcessStartInfo(game.ExecutablePath)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(game.ExecutablePath)!,
                };
                if (!string.IsNullOrWhiteSpace(game.LaunchArguments))
                {
                    info.Arguments = game.LaunchArguments;
                }

                Process.Start(info)?.Dispose();
                return null;
            }

            return game.Source == GameSource.Xbox
                ? "Xbox oyunları Xbox uygulamasından başlatılır."
                : "Bu oyunun başlatma bilgisi bulunamadı.";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Log.Warn($"Launch failed for {game.Name}: {ex.Message}");
            return "Oyun başlatılamadı: " + ex.Message;
        }
    }
}
