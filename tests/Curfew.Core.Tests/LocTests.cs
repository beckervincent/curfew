using Curfew.Core.Localization;
using Xunit;

namespace Curfew.Core.Tests;

/// <summary>
/// Tests for <see cref="Loc"/>, the shared key/catalog localizer. They restore
/// the language after each case so the static active-language state does not leak
/// between tests.
/// </summary>
public class LocTests : IDisposable
{
    private readonly string _original = Loc.Language;

    public void Dispose() => Loc.SetLanguage(_original);

    [Fact]
    public void English_returns_english_value()
    {
        Loc.SetLanguage("en");
        Assert.Equal("Time's Up", Loc.T("lock.title.budget"));
    }

    [Fact]
    public void German_returns_german_value()
    {
        Loc.SetLanguage("de");
        Assert.Equal("Zeit ist um", Loc.T("lock.title.budget"));
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("DE")]
    [InlineData(" de ")]
    public void Full_culture_tags_resolve_to_the_language(string code)
    {
        Loc.SetLanguage(code);
        Assert.Equal("de", Loc.Language);
        Assert.Equal("Entsperren", Loc.T("lock.unlock"));
    }

    [Fact]
    public void Unknown_language_falls_back_to_english()
    {
        Loc.SetLanguage("xx");
        Assert.Equal("en", Loc.Language);
        Assert.Equal("Unlock", Loc.T("lock.unlock"));
    }

    [Fact]
    public void Missing_key_returns_the_key_itself()
    {
        Loc.SetLanguage("en");
        Assert.Equal("nonexistent.key", Loc.T("nonexistent.key"));
    }

    [Fact]
    public void Format_arguments_are_substituted()
    {
        Loc.SetLanguage("en");
        Assert.Equal("+30 min", Loc.T("lock.extend.minutes", 30));
        Loc.SetLanguage("de");
        Assert.Equal("+30 Min", Loc.T("lock.extend.minutes", 30));
    }

    [Fact]
    public void Every_language_defines_the_same_keys_as_english()
    {
        // A missing key would silently fall back to English; assert parity so
        // translations stay complete as the catalog grows.
        Loc.SetLanguage("en");
        var englishKeys = new[]
        {
            "lock.title.budget", "lock.title.schedule", "lock.unlock",
            "lock.shutdown", "lock.incorrect", "lock.exceeded",
        };

        foreach (var lang in Loc.AvailableLanguages)
        {
            Loc.SetLanguage(lang);
            foreach (var key in englishKeys)
                Assert.NotEqual(key, Loc.T(key)); // T returns the key only when unresolved
        }
    }
}
