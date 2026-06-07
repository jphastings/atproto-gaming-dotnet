using System.IO;
using System.Text.Json.Serialization;
using ByJP.AtprotoGaming.Core;
using Xunit;

namespace ByJP.AtprotoGaming.Core.Tests;

public class ConfigStoreTests
{
    private sealed class MyConfig : CoreConfig
    {
        [JsonPropertyName("favouriteColour")] public string FavouriteColour { get; set; } = "";
    }

    [Fact]
    public void FirstRunWritesTemplateAndLogsBanner()
    {
        using var fs = new TempFileSystem();
        var log = new CapturingLogSink();

        ConfigStore<MyConfig>.LoadOrCreate(fs, log);

        Assert.True(File.Exists(Path.Combine(fs.ConfigDirectory, "config.json")));
        Assert.Contains(log.Warns, w => w.Contains("not yet configured"));
    }

    [Fact]
    public void ReBannersWhenCredentialsBlank()
    {
        using var fs = new TempFileSystem();
        File.WriteAllText(Path.Combine(fs.ConfigDirectory, "config.json"), "{ \"handle\": \"\" }");
        var log = new CapturingLogSink();

        ConfigStore<MyConfig>.LoadOrCreate(fs, log);

        Assert.Contains(log.Warns, w => w.Contains("not yet configured"));
    }

    [Fact]
    public void SaveRoundTripsCoreAndConsumerFields()
    {
        using var fs = new TempFileSystem();
        var log = new CapturingLogSink();

        var store = ConfigStore<MyConfig>.LoadOrCreate(fs, log);
        store.Current.Handle = "me.bsky.social";
        store.Current.StatsRkey = "3abc";
        store.Current.FavouriteColour = "teal";
        store.Save();

        var reloaded = ConfigStore<MyConfig>.LoadOrCreate(fs, new CapturingLogSink());
        Assert.Equal("me.bsky.social", reloaded.Current.Handle);
        Assert.Equal("3abc", reloaded.Current.StatsRkey);
        Assert.Equal("teal", reloaded.Current.FavouriteColour);
    }

    [Fact]
    public void SaveIsAtomicNoTempLeftBehind()
    {
        using var fs = new TempFileSystem();
        var store = ConfigStore<MyConfig>.LoadOrCreate(fs, new CapturingLogSink());
        store.Current.Handle = "x";
        store.Save();

        Assert.False(File.Exists(Path.Combine(fs.ConfigDirectory, "config.json.tmp")));
    }

    [Fact]
    public void CorruptConfigIsToleratedAndPreserved()
    {
        using var fs = new TempFileSystem();
        var path = Path.Combine(fs.ConfigDirectory, "config.json");
        File.WriteAllText(path, "{ this is not valid json ");
        var log = new CapturingLogSink();

        var store = ConfigStore<MyConfig>.LoadOrCreate(fs, log);

        Assert.Equal("", store.Current.Handle);                 // fell back to defaults
        Assert.Contains(log.Errors, e => e.Contains("unreadable"));
        Assert.Equal("{ this is not valid json ", File.ReadAllText(path)); // not overwritten
    }
}
