using AcDream.App;
using AcDream.App.Configuration;
using AcDream.Runtime.Session;

namespace AcDream.App.Tests.Configuration;

public sealed class RuntimeOptionsSessionConfigTests
{
    [Fact]
    public void SessionConfigOverridesLiveSettingsAndCarriesAllFiveNewFields()
    {
        var config = new SessionConfiguration { Version = 1 };
        var session = new SessionDescriptor
        {
            Id = "gui-session",
            Endpoint = new SessionEndpointDescriptor
            {
                Host = "192.168.1.50",
                Port = 9123,
            },
            Account = "guiaccount",
            Character = new SessionCharacterSelectorDescriptor { Name = "GuiToon" },
            Credential = new SessionCredentialDescriptor
            {
                Provider = SessionCredentialProviderKind.Environment,
                Reference = "IGNORED",
            },
            Plugins = ["PluginA", "PluginB"],
            LoginCommands = ["/tell x, hi"],
            LoginCommandDelayMs = 900,
            StatusFile = "status.jsonl",
        };

        RuntimeOptions options = RuntimeOptions.FromSessionConfig(
            "D:\\dat",
            _ => null,
            "session.json",
            config,
            session,
            "resolved-password");

        Assert.True(options.LiveMode);
        Assert.Equal("192.168.1.50", options.LiveHost);
        Assert.Equal(9123, options.LivePort);
        Assert.Equal("guiaccount", options.LiveUser);
        Assert.Equal("resolved-password", options.LivePass);
        Assert.Equal("session.json", options.SessionConfigPath);
        Assert.Equal("gui-session", options.SessionId);
        Assert.Equal(
            new LiveSessionCharacterSelector(null, null, "GuiToon"),
            options.LiveCharacterSelector);
        Assert.Equal("status.jsonl", options.StatusFilePath);
        Assert.Equal(["PluginA", "PluginB"], options.Plugins);
        Assert.Equal(["/tell x, hi"], options.LoginCommands);
        Assert.Equal(900, options.LoginCommandDelayMs);
        Assert.True(options.RetailUi);
    }

    [Fact]
    public void AbsentCharacterSelectorRemainsNullForGraphicalSelectionFlow()
    {
        var config = new SessionConfiguration { Version = 1 };
        var session = new SessionDescriptor
        {
            Id = "no-selector",
            Endpoint = new SessionEndpointDescriptor { Host = "127.0.0.1", Port = 9000 },
            Account = "account",
            Credential = new SessionCredentialDescriptor
            {
                Provider = SessionCredentialProviderKind.Environment,
                Reference = "X",
            },
        };

        RuntimeOptions options = RuntimeOptions.FromSessionConfig(
            "D:\\dat",
            _ => null,
            "session.json",
            config,
            session,
            "password");

        Assert.Null(options.LiveCharacterSelector);
        Assert.Null(options.Plugins);
        Assert.Empty(options.LoginCommands);
        Assert.Equal(500, options.LoginCommandDelayMs);
        Assert.Null(options.StatusFilePath);
    }

    [Fact]
    public void SessionConfigPreservesExplicitRetailUiOptOut()
    {
        var config = new SessionConfiguration { Version = 1 };
        var session = new SessionDescriptor
        {
            Id = "no-ui",
            Endpoint = new SessionEndpointDescriptor { Host = "127.0.0.1", Port = 9000 },
            Account = "account",
            Credential = new SessionCredentialDescriptor
            {
                Provider = SessionCredentialProviderKind.Environment,
                Reference = "X",
            },
        };

        RuntimeOptions options = RuntimeOptions.FromSessionConfig(
            "D:\\dat",
            key => key == "ACDREAM_RETAIL_UI" ? "0" : null,
            "session.json",
            config,
            session,
            "password");

        Assert.False(options.RetailUi);
    }

    [Fact]
    public void ProcessContentOverridesDatDirectoryAndPreparedAssetPath()
    {
        var config = new SessionConfiguration
        {
            Version = 1,
            Process = new SessionProcessSettings
            {
                Content = new SessionContentDescriptor
                {
                    DatDirectory = "D:\\configured-dats",
                    PreparedAssetPath = "D:\\configured-dats\\acdream.pak",
                },
            },
        };
        var session = new SessionDescriptor
        {
            Id = "content-session",
            Endpoint = new SessionEndpointDescriptor { Host = "127.0.0.1", Port = 9000 },
            Account = "account",
            Credential = new SessionCredentialDescriptor
            {
                Provider = SessionCredentialProviderKind.Environment,
                Reference = "X",
            },
        };

        RuntimeOptions options = RuntimeOptions.FromSessionConfig(
            "D:\\configured-dats",
            _ => null,
            "session.json",
            config,
            session,
            "password");

        Assert.Equal(
            "D:\\configured-dats\\acdream.pak",
            options.PreparedAssetPath);
    }

    [Fact]
    public void ProcessContentCarriesOptionalOverlayAndBothRecipeIdentities()
    {
        var config = new SessionConfiguration
        {
            Version = 1,
            Process = new SessionProcessSettings
            {
                Content = new SessionContentDescriptor
                {
                    DatDirectory = "D:\\dats",
                    PreparedAssetPath = "D:\\content\\acdream.pak",
                    PreparedAssetOverlayPath =
                        "D:\\content\\acdream-update-5.pak",
                    PreparedAssetBaseRecipeVersion = 4,
                    PreparedAssetEffectiveRecipeVersion = 5,
                },
            },
        };
        var session = new SessionDescriptor
        {
            Id = "overlay-session",
            Endpoint = new SessionEndpointDescriptor
            {
                Host = "127.0.0.1",
                Port = 9000,
            },
            Account = "account",
            Credential = new SessionCredentialDescriptor
            {
                Provider = SessionCredentialProviderKind.Environment,
                Reference = "X",
            },
        };

        RuntimeOptions options = RuntimeOptions.FromSessionConfig(
            "D:\\dats",
            _ => null,
            "session.json",
            config,
            session,
            "password");

        Assert.Equal(
            "D:\\content\\acdream-update-5.pak",
            options.PreparedAssetOverlayPath);
        Assert.Equal(4u, options.PreparedAssetBaseRecipeVersion);
        Assert.Equal(5u, options.PreparedAssetEffectiveRecipeVersion);
    }

    [Fact]
    public void EnvironmentFlowLeavesEveryNewFieldAtItsNothingConfiguredDefault()
    {
        RuntimeOptions options = RuntimeOptions.Parse("D:\\dat", _ => null);

        Assert.Null(options.SessionConfigPath);
        Assert.Null(options.SessionId);
        Assert.Null(options.LiveCharacterSelector);
        Assert.Null(options.StatusFilePath);
        Assert.Null(options.Plugins);
        Assert.Empty(options.LoginCommands);
        Assert.Equal(500, options.LoginCommandDelayMs);
        Assert.Null(options.PreparedAssetOverlayPath);
        Assert.Null(options.PreparedAssetBaseRecipeVersion);
        Assert.Null(options.PreparedAssetEffectiveRecipeVersion);
    }
}
