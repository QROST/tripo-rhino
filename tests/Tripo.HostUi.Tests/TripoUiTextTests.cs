using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace Tripo.HostUi.Tests;

[CollectionDefinition("UI language", DisableParallelization = true)]
public sealed class UiLanguageTestGroup;

[Collection("UI language")]
public sealed class TripoUiTextTests
{
    [Theory]
    [InlineData(1033, "zh-CN", "en")]
    [InlineData(1036, "zh-TW", "en")]
    [InlineData(2052, "en-US", "zh-CN")]
    [InlineData(1028, "en-US", "zh-CN")]
    [InlineData(3076, "en-US", "zh-CN")]
    [InlineData(4, "en-US", "zh-CN")]
    [InlineData(null, "zh-Hant", "zh-CN")]
    [InlineData(null, "zh-Hans", "zh-CN")]
    [InlineData(0, "zh-HK", "zh-CN")]
    [InlineData(-1, "zh-CN", "zh-CN")]
    [InlineData(127, "zh-CN", "zh-CN")]
    [InlineData(int.MaxValue, "zh-CN", "zh-CN")]
    [InlineData(null, "ja-JP", "en")]
    [InlineData(null, "", "en")]
    public void RhinoLanguageWinsAndOnlyUnavailableLanguageUsesSystem(int? rhino, string system, string expected) =>
        Assert.Equal(expected, TripoUiText.ResolveLanguage(rhino, CultureInfo.GetCultureInfo(system)));

    [Fact]
    public void UnknownLanguageAndUnknownTextUseEnglishUnmodified()
    {
        Assert.Equal("en", TripoUiText.ResolveLanguage(null, null));
        Assert.Equal("Create in Rhino", TripoUiText.Get("Create in Rhino", "fr-FR"));
        const string unknown = "Remote detail: file \"Generate\" not found; id=Create in Rhino";
        Assert.Equal(unknown, TripoUiText.Get(unknown, "zh-CN"));
    }

    [Fact]
    public void EveryTranslatedTemplatePreservesAllArgumentsAndFormats()
    {
        foreach ((string english, string chinese) in TripoUiText.Translations)
        {
            Assert.False(string.IsNullOrWhiteSpace(chinese));
            Assert.Equal(Placeholders(english), Placeholders(chinese));
        }
    }

    [Fact]
    public async Task BackgroundPresentationUsesHostLanguageAndPreservesUserData()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo previousUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            TripoUiText.Initialize(2052, CultureInfo.GetCultureInfo("en-US"));
            var state = TripoPanelState.Initial with
            {
                Connected = true,
                Context = new Tripo.Bridge.HostContextReceipt("rhino", "8-test", 123,
                    "session-id", "Create in Rhino", "Meters", []),
            };
            TripoPanelPresentation presentation = await Task.Run(() =>
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
                return TripoPanelPresentation.Create(state, TripoPanelRecoveryLoadResult.Empty,
                    null, "Generate", "Create in Rhino", "glb", DirectGlbCreateUiStage.Inactive,
                    imageMode: true, hasImage: true, imageName: "Generate.png");
            });
            Assert.Equal("文档：Create in Rhino", presentation.DocumentStatus);
            Assert.Equal("session-id", presentation.DocumentSessionId);
            Assert.Equal("Generate.png", presentation.ImageName);
            Assert.Equal("在 Rhino 中创建", presentation.CreateInRhinoText);
            Assert.Equal("操作 ID：Create in Rhino", TripoUiText.Format("Operation ID: {0}", "Create in Rhino"));
            Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
            Assert.Equal(previousUi, CultureInfo.CurrentUICulture);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
            TripoUiText.Initialize(1033, null);
        }
    }

    [Fact]
    public void EnglishHostLeavesExistingPresentationUnchangedOnChineseSystem()
    {
        try
        {
            TripoUiText.Initialize(1033, CultureInfo.GetCultureInfo("zh-CN"));
            var panel = TripoPanelPresentation.Create(TripoPanelState.Initial,
                TripoPanelRecoveryLoadResult.Empty, null, "a chair", "Chair", "glb");
            Assert.Equal("Document: not connected", panel.DocumentStatus);
            Assert.Equal("Create in Rhino", panel.CreateInRhinoText);
        }
        finally { TripoUiText.Initialize(1033, null); }
    }

    private static string[] Placeholders(string value) =>
        Regex.Matches(value, @"\{\d+(?:,[^}:]+)?(?::[^}]+)?\}")
            .Select(match => match.Value).Order(StringComparer.Ordinal).ToArray();
}
