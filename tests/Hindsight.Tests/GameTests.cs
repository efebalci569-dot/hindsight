using Hindsight.Core;
using Hindsight.Core.Games;

namespace Hindsight.Tests;

public class ScannerTests
{
    [Fact]
    public void VdfParsesLibraryFolders()
    {
        const string text = """
            "libraryfolders"
            {
                "0" { "path" "C:\\Program Files (x86)\\Steam" "apps" { "730" "1" } }
                "1" { "path" "D:\\SteamLibrary" }
            }
            """;
        var root = VdfParser.Parse(text);
        Assert.Equal(@"D:\SteamLibrary", root["libraryfolders"]!["1"]!.GetString("path"));
    }

    [Fact]
    public void EpicSkipsDlcAndMissingFolders()
    {
        const string dlc = """{"DisplayName":"Pack","AppName":"dlc1","MainGameAppName":"game","InstallLocation":"C:\\"}""";
        Assert.Null(EpicScanner.Parse(dlc));

        const string missing = """{"DisplayName":"Game","AppName":"g","InstallLocation":"Z:\\does\\not\\exist"}""";
        Assert.Null(EpicScanner.Parse(missing));
    }

    [Fact]
    public void EpicReadsAnInstalledGame()
    {
        var dir = Path.GetTempPath().TrimEnd('\\').Replace("\\", "\\\\");
        var json = $$"""{"DisplayName":"Rocket","AppName":"Sugar","CatalogNamespace":"ns","CatalogItemId":"item","InstallLocation":"{{dir}}","LaunchExecutable":"Game.exe","AppCategories":["public","games"]}""";
        var game = EpicScanner.Parse(json);
        Assert.NotNull(game);
        Assert.Equal("epic:Sugar", game!.Id);
        Assert.StartsWith("com.epicgames.launcher://apps/ns%3Aitem%3ASugar", game.LaunchUri);
        Assert.Equal(["Game.exe"], game.ProcessNames);
    }

    [Fact]
    public void RiotUninstallStringBecomesALaunchCommand()
    {
        var launch = UninstallRegistryScanner.RiotLaunch(
            "\"C:\\Riot Games\\Riot Client\\RiotClientServices.exe\" --uninstall-product=valorant --uninstall-patchline=live");
        Assert.Equal(@"C:\Riot Games\Riot Client\RiotClientServices.exe", launch!.Value.Exe);
        Assert.Equal("--launch-product=valorant --launch-patchline=live", launch.Value.Args);
    }

    [Fact]
    public void SafeNameRemovesInvalidCharacters()
    {
        Assert.Equal("Half Life 2 Episode One", AppPaths.SafeName("Half: Life 2 / Episode One"));
        Assert.Equal("Diğer", AppPaths.SafeName("   "));
        Assert.Equal("Diğer", AppPaths.SafeName("CON"));
    }
}

public class GameLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hindsight-tests-" + Guid.NewGuid().ToString("N"));

    public GameLibraryTests() => AppPaths.OverrideDataRoot(_root);

    [Fact]
    public void MatchesByLongestInstallFolderThenByExeName()
    {
        var library = new GameLibrary();
        var outer = Directory.CreateDirectory(Path.Combine(_root, "games", "Outer")).FullName;
        var inner = Directory.CreateDirectory(Path.Combine(outer, "Inner")).FullName;
        var a = library.AddManual(Path.Combine(outer, "outer.exe"), "Outer");
        var b = library.AddManual(Path.Combine(inner, "inner.exe"), "Inner");

        Assert.Equal(b.Id, library.Match(Path.Combine(inner, "bin", "x.exe"), "x")?.Id);
        Assert.Equal(a.Id, library.Match(Path.Combine(outer, "y.exe"), "y")?.Id);
        Assert.Equal(b.Id, library.Match(null, "inner")?.Id); // protected process: path hidden, name known
        Assert.Null(library.Match(@"C:\Elsewhere\z.exe", "z"));
    }

    [Fact]
    public void DetectedGamesAreRememberedOnce()
    {
        var library = new GameLibrary();
        var first = library.AddDetected(@"C:\Indie\Cool.exe", "Cool", "Cool Game");
        var second = library.AddDetected(@"C:\Indie\Cool.exe", "Cool", "Cool Game");
        Assert.Equal(first.Id, second.Id);
        Assert.Single(library.Games, g => g.Source == GameSource.Detected);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }
}
