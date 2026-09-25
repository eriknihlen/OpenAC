using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using AcDream.Content;
using AcDream.Content.CharGen;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Headless.Configuration;
using AcDream.Headless.Credentials;
using AcDream.Headless.Diagnostics;
using AcDream.Headless.Hosting;
using AcDream.Headless.Platform;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Physics;
using AcDream.Runtime.Session;
using AcDream.Runtime.World;
using DatCharGen = DatReaderWriter.DBObjs.CharGen;
using DatHeritageGroupCG = DatReaderWriter.Types.HeritageGroupCG;
using DatIDBObj = DatReaderWriter.Lib.IO.IDBObj;
using DatDatabaseImpl = DatReaderWriter.DatDatabase;
using DatPStringBaseByte = DatReaderWriter.Types.PStringBase<byte>;

namespace AcDream.Headless.Tests;

public sealed class HeadlessSessionHostTests
{
    [Fact]
    public void ContentLease_InstallsRealChargenOptions_SelectHeritageIsAccepted()
    {
        var factory = new ChargenFixtureContentFactory();
        using var owner = new HeadlessProcessContentOwner(
            ContentDescriptor(),
            _ => { },
            factory);
        HeadlessProcessContentOwner.HeadlessProcessContentLease lease =
            owner.AcquireLease("chargen-fixture");
        using var credential = new HeadlessCredentialSecret(
            "fixture",
            "password");
        using var diagnosticsOutput = new StringWriter();
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(diagnosticsOutput),
            new FixtureSessionOperations(),
            contentLease: lease);

        RuntimeCharacterCreationState creation =
            host.Runtime.Session.CharacterCreationState;

        Assert.True(creation.Options.HeritagesById.ContainsKey(
            ChargenFixtureContentFactory.HeritageId));

        creation.Begin(host.Runtime.Generation);
        Assert.True(creation.TrySelectHeritage(ChargenFixtureContentFactory.HeritageId));
        Assert.Equal(
            ChargenFixtureContentFactory.HeritageId,
            creation.View.Snapshot.HeritageId);
    }

    [Fact]
    public void LoginCommandsUseTheHeadlessLiveBusAndPreserveWireOrder()
    {
        var captured = new List<byte[]>();
        var operations = new FixtureSessionOperations
        {
            GameActionCapture = body => captured.Add(body),
        };
        using var diagnosticsOutput = new StringWriter();
        using var credential = new HeadlessCredentialSecret(
            "fixture",
            "password");
        using var host = new HeadlessSessionHost(
            Descriptor(
                loginCommands:
                [
                    "hello",
                    "/tell Bob, secret",
                    "/f group",
                    "@admin raw",
                    "/vt start",
                ],
                loginCommandDelayMs: 0),
            credential,
            new HeadlessDiagnosticWriter(diagnosticsOutput),
            operations);

        RuntimeSessionStartResult result = host.Start();

        Assert.Equal(RuntimeSessionStartStatus.Connected, result.Status);
        Assert.Equal(
            [
                ChatRequests.TalkOpcode,
                ChatRequests.TellOpcode,
                ChatRequests.ChatChannelOpcode,
                ChatRequests.ChatChannelOpcode,
                ChatRequests.TalkOpcode,
            ],
            LoginCommandSends(captured).Select(ActionOpcode));
        IReadOnlyList<byte[]> loginSends = LoginCommandSends(captured);
        Assert.Equal(
            0x00000800u,
            BinaryPrimitives.ReadUInt32LittleEndian(loginSends[2].AsSpan(12)));
        Assert.Equal(
            0x00000002u,
            BinaryPrimitives.ReadUInt32LittleEndian(loginSends[3].AsSpan(12)));
    }

    /// <summary>
    /// A login command may be one the server carries out, and the server
    /// takes nothing from a character whose login is not complete. The list
    /// waits for that rather than losing its first entry.
    /// </summary>
    [Fact]
    public void LoginCommandsWaitUntilTheServerIsListening()
    {
        var captured = new List<byte[]>();
        var operations = new FixtureSessionOperations
        {
            GameActionCapture = body => captured.Add(body),
            ServerListensFromTheStart = false,
        };
        using var diagnosticsOutput = new StringWriter();
        using var credential = new HeadlessCredentialSecret("fixture", "password");
        using var host = new HeadlessSessionHost(
            Descriptor(loginCommands: ["hello"], loginCommandDelayMs: 0),
            credential,
            new HeadlessDiagnosticWriter(diagnosticsOutput),
            operations);

        Assert.Equal(RuntimeSessionStartStatus.Connected, host.Start().Status);
        host.Tick(0.015d);

        Assert.DoesNotContain(ChatRequests.TalkOpcode, LoginCommandSends(captured).Select(ActionOpcode));
    }

    [Fact]
    public void PluginDrivenLogoutRoutesThroughTheHostsOwnStopAndClearsTheSession()
    {
        // ILoginAutomation.Logout() is the forwarding alias for
        // RequestLogout(): a plugin calling it gets the same server logoff,
        // confirmation wait, and session end as the graphical client's
        // logout control, never a raw host stop.
        string statusPath = Path.Combine(
            Path.GetTempPath(),
            $"acdream-headless-plugin-logout-{Guid.NewGuid():N}.jsonl");
        try
        {
            var operations = new FixtureSessionOperations();
            using var diagnosticsOutput = new StringWriter();
            using var credential = new HeadlessCredentialSecret(
                "fixture",
                "password");
            bool confirmed = false;
            using var host = new HeadlessSessionHost(
                Descriptor(statusFile: statusPath),
                credential,
                new HeadlessDiagnosticWriter(diagnosticsOutput),
                operations,
                logoutConfirmedOverride: () => confirmed);

            Assert.Equal(
                RuntimeSessionStartStatus.Connected,
                host.Start().Status);
            Assert.True(host.Runtime.Session.IsInWorld);

            bool logoutAccepted = host.Plugins.Host.Automation.Login.Logout();

            Assert.True(logoutAccepted);
            Assert.True(host.Runtime.TransitOwner.IsLogoutActive);
            Assert.False(host.Plugins.Host.Automation.Login.CanRequestLogout);
            Assert.False(host.Plugins.Host.Automation.Login.Logout());

            confirmed = true;
            host.Tick(0.015d);

            Assert.Equal(1, operations.ReturnToCharacterSelectCount);
            Assert.False(host.Runtime.Session.IsInWorld);
            Assert.True(host.IsPolicyComplete);
            Assert.False(host.IsFaulted);

            host.Dispose();
            JsonElement[] events = LiveStatusFile.ReadAllLines(statusPath)
                .Select(static line =>
                    JsonDocument.Parse(line).RootElement.Clone())
                .ToArray();
            JsonElement exited = Assert.Single(
                events,
                static item => item.GetProperty("e").GetString() == "exited");
            Assert.Equal(
                (int)HeadlessExitCode.Success,
                exited.GetProperty("code").GetInt32());
        }
        finally
        {
            if (File.Exists(statusPath))
                File.Delete(statusPath);
        }
    }

    [Fact]
    public void ConfirmationDoneClearsThePendingConfirmationForTheMatchingContext()
    {
        // The server can resolve or cancel a confirmation on its own (a
        // different client answered it, the request timed out) without our
        // own RespondToConfirmation call ever running. Before this fix,
        // OnConfirmationDone was wired to null, so a completed confirmation
        // left the stale request sitting in _pendingConfirmation forever.
        using var host = new HeadlessSessionHost(
            Descriptor(),
            new HeadlessCredentialSecret("fixture", "password"),
            new HeadlessDiagnosticWriter(new StringWriter()),
            new FixtureSessionOperations());

        FieldInfo pendingField = typeof(HeadlessSessionHost)
            .GetField(
                "_pendingConfirmation",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
        var request = new GameEvents.CharacterConfirmationRequest(
            5u,
            77u,
            "continue?");
        pendingField.SetValue(host, request);
        Assert.Equal(request, host.PendingConfirmation);

        // A done for a different, already-superseded context id must not
        // clear a newer pending request.
        host.HandleConfirmationDone(
            new GameEvents.CharacterConfirmationDone(5u, 78u));
        Assert.Equal(request, host.PendingConfirmation);

        host.HandleConfirmationDone(
            new GameEvents.CharacterConfirmationDone(5u, 77u));
        Assert.Null(host.PendingConfirmation);
    }

    [Fact]
    public void AConfirmationDoneOnTheWireClearsThePendingConfirmationThroughProductionRouting()
    {
        // Unlike the direct-call test above, this drives an actual
        // CharacterConfirmationDone game-event envelope through the live
        // session's GameEventWiring so the character.OnConfirmationDone ->
        // HeadlessSessionHost.HandleConfirmationDone route is exercised end
        // to end, not just the handler in isolation.
        var operations = new FixtureSessionOperations();
        using var host = new HeadlessSessionHost(
            Descriptor(),
            new HeadlessCredentialSecret("fixture", "password"),
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations);
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            host.Start().Status);

        WorldSession session = operations.Sessions[^1];

        FieldInfo pendingField = typeof(HeadlessSessionHost)
            .GetField(
                "_pendingConfirmation",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
        var request = new GameEvents.CharacterConfirmationRequest(5u, 77u, "continue?");
        pendingField.SetValue(host, request);
        Assert.Equal(request, host.PendingConfirmation);

        session.GameEvents.Dispatch(
            GameEventEnvelope.TryParse(
                WrapCharacterConfirmationDoneEnvelope(type: 5u, contextId: 77u))!
                .Value);

        Assert.Null(host.PendingConfirmation);
    }

    [Fact]
    public void RegisterTradeGameEventReachesTradeOwnerThroughTheRealWireRouter()
    {
        // HeadlessSessionHost never passed Trade: Runtime.TradeOwner into
        // LiveSocialSessionBindings, so LiveSessionEventRouter's
        // onTradeRegister/onTradeAdd/onTradeAccept delegate holes were all
        // null and every inbound trade wire message was silently dropped
        // on the headless host -- a trade opened by a real partner never
        // became visible to TradeOwner at all, regardless of plugin-surface
        // identity, object lookup, or settings storage.
        //
        // ACE's real wire landmine: RegisterTrade carries the partner's
        // guid in BOTH the Initiator and Partner fields (the true
        // initiator is never actually on the wire) -- this is that exact
        // shape.
        var operations = new FixtureSessionOperations();
        using var host = new HeadlessSessionHost(
            Descriptor(),
            new HeadlessCredentialSecret("fixture", "password"),
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations);
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            host.Start().Status);

        WorldSession session = operations.Sessions[^1];

        Assert.False(host.Runtime.Trade.Snapshot.IsOpen);

        const uint partnerGuid = 0x50000B0Bu;
        session.GameEvents.Dispatch(
            GameEventEnvelope.TryParse(
                WrapRegisterTradeEnvelope(
                    initiator: partnerGuid,
                    partner: partnerGuid,
                    stamp: 0uL))!.Value);

        Assert.True(host.Runtime.Trade.Snapshot.IsOpen);
        Assert.Equal(partnerGuid, host.Runtime.Trade.Snapshot.PartnerGuid);
    }

    [Fact]
    public void RegisterTradeWithDistinctInitiatorAndPartnerGuidsResolvesToTheInitiator()
    {
        // A non-landmine RegisterTrade (Initiator and Partner genuinely
        // distinct, neither the bot's own guid) still routes -- RuntimeTradeState.
        // ApplyRegister's own rule ("initiator wins unless it's the local
        // player or zero") is exercised end to end, not just directly.
        var operations = new FixtureSessionOperations();
        using var host = new HeadlessSessionHost(
            Descriptor(),
            new HeadlessCredentialSecret("fixture", "password"),
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations);
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            host.Start().Status);

        WorldSession session = operations.Sessions[^1];

        const uint initiatorGuid = 0x50000B0Bu;
        const uint otherPartyGuid = 0x50000C0Cu;
        session.GameEvents.Dispatch(
            GameEventEnvelope.TryParse(
                WrapRegisterTradeEnvelope(
                    initiator: initiatorGuid,
                    partner: otherPartyGuid,
                    stamp: 0uL))!.Value);

        Assert.True(host.Runtime.Trade.Snapshot.IsOpen);
        Assert.Equal(initiatorGuid, host.Runtime.Trade.Snapshot.PartnerGuid);
    }

    [Fact]
    public void RegisterTradeWhereTheBotIsTheInitiatorResolvesPartnerFromTheOtherField()
    {
        // When the headless bot itself opened the trade, ACE's Initiator
        // field is the bot's own guid -- RuntimeTradeState.ApplyRegister
        // must fall back to the Partner field for the actual partner
        // rather than mistaking the bot for its own trade partner.
        var operations = new FixtureSessionOperations();
        using var host = new HeadlessSessionHost(
            Descriptor(),
            new HeadlessCredentialSecret("fixture", "password"),
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations);
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            host.Start().Status);

        WorldSession session = operations.Sessions[^1];
        uint botGuid = host.Runtime.PlayerIdentity.ServerGuid;
        const uint otherPartyGuid = 0x50000C0Cu;

        session.GameEvents.Dispatch(
            GameEventEnvelope.TryParse(
                WrapRegisterTradeEnvelope(
                    initiator: botGuid,
                    partner: otherPartyGuid,
                    stamp: 0uL))!.Value);

        Assert.True(host.Runtime.Trade.Snapshot.IsOpen);
        Assert.Equal(otherPartyGuid, host.Runtime.Trade.Snapshot.PartnerGuid);
    }

    [Fact]
    public void LoginCommandsRouteWireOnlyClientCommandsWithExactPolarityAndOrder()
    {
        var captured = new List<byte[]>();
        var operations = new FixtureSessionOperations
        {
            GameActionCapture = body => captured.Add(body),
        };
        using var diagnosticsOutput = new StringWriter();
        using var credential = new HeadlessCredentialSecret(
            "fixture",
            "password");
        using var host = new HeadlessSessionHost(
            Descriptor(
                loginCommands:
                [
                    "/permit add Aunt Agatha",
                    "@permit remove Lord Gnarly Beard",
                    "/chat on",
                    "/chat off",
                    "/notell on",
                    "/notell off",
                ],
                loginCommandDelayMs: 0),
            credential,
            new HeadlessDiagnosticWriter(diagnosticsOutput),
            operations);

        Assert.Equal(RuntimeSessionStartStatus.Connected, host.Start().Status);
        Assert.Equal(
            [
                ClientCommandRequests.AddPlayerPermissionOpcode,
                ClientCommandRequests.RemovePlayerPermissionOpcode,
                ClientCommandRequests.ModifyGlobalSquelchOpcode,
                ClientCommandRequests.ModifyGlobalSquelchOpcode,
                ClientCommandRequests.ModifyGlobalSquelchOpcode,
                ClientCommandRequests.ModifyGlobalSquelchOpcode,
            ],
            LoginCommandSends(captured).Select(ActionOpcode));
        IReadOnlyList<byte[]> loginSends = LoginCommandSends(captured);
        Assert.Equal("Aunt Agatha", StringActionArgument(loginSends[0]));
        Assert.Equal("Lord Gnarly Beard", StringActionArgument(loginSends[1]));
        Assert.Equal(
            [
                (Add: 0u, MessageType: 2u),
                (Add: 1u, MessageType: 2u),
                (Add: 1u, MessageType: 3u),
                (Add: 0u, MessageType: 3u),
            ],
            LoginCommandSends(captured).Skip(2).Select(static body => (
                Add: BinaryPrimitives.ReadUInt32LittleEndian(
                    body.AsSpan(12, sizeof(uint))),
                MessageType: BinaryPrimitives.ReadUInt32LittleEndian(
                    body.AsSpan(16, sizeof(uint))))));
    }

    [Theory]
    [InlineData("/permit add")]
    [InlineData("/chat maybe")]
    [InlineData("/notell maybe")]
    public void InvalidWireOnlyArgumentKeepsTypedFeedbackWithoutStatusFailure(
        string invalidCommand)
    {
        string statusPath = Path.Combine(
            Path.GetTempPath(),
            $"acdream-headless-wire-client-errors-{Guid.NewGuid():N}.jsonl");
        try
        {
            var captured = new List<byte[]>();
            var operations = new FixtureSessionOperations
            {
                GameActionCapture = body => captured.Add(body),
            };
            using var diagnosticsOutput = new StringWriter();
            using var credential = new HeadlessCredentialSecret(
                "fixture",
                "password");
            using var host = new HeadlessSessionHost(
                Descriptor(
                    statusFile: statusPath,
                    loginCommands:
                    [
                        invalidCommand,
                        "after",
                    ],
                    loginCommandDelayMs: 0),
                credential,
                new HeadlessDiagnosticWriter(diagnosticsOutput),
                operations);

            Assert.Equal(
                RuntimeSessionStartStatus.Connected,
                host.Start().Status);
            Assert.Equal(
                "after",
                TalkText(Assert.Single(LoginCommandSends(captured))));

            host.Runtime.CommunicationOwner.SpewBox.Tick(0d);
            Assert.Equal(
                "That is not a valid command.",
                Assert.Single(
                    host.Runtime.CommunicationOwner.SpewBox.Snapshot()).Text);

            JsonElement[] events = LiveStatusFile.ReadAllLines(statusPath)
                .Select(static line =>
                    JsonDocument.Parse(line).RootElement.Clone())
                .ToArray();
            Assert.DoesNotContain(
                events,
                static item => item.GetProperty("e").GetString()
                    == "loginCommandFailed");
            Assert.True(host.Runtime.Session.IsInWorld);
        }
        finally
        {
            if (File.Exists(statusPath))
                File.Delete(statusPath);
        }
    }

    [Fact]
    public void LoginCommandFailuresAreVersionedOrderedAndSessionIsolated()
    {
        string statusPath = Path.Combine(
            Path.GetTempPath(),
            $"acdream-headless-login-commands-{Guid.NewGuid():N}.jsonl");
        try
        {
            var captured = new List<byte[]>();
            var operations = new FixtureSessionOperations
            {
                GameActionCapture = body => captured.Add(body),
            };
            using var diagnosticsOutput = new StringWriter();
            using var credential = new HeadlessCredentialSecret(
                "fixture",
                "password");
            using var host = new HeadlessSessionHost(
                Descriptor(
                    statusFile: statusPath,
                    loginCommands: ["/", "/version", "after"],
                    loginCommandDelayMs: 0),
                credential,
                new HeadlessDiagnosticWriter(diagnosticsOutput),
                operations);

            RuntimeSessionStartResult result = host.Start();

            Assert.Equal(RuntimeSessionStartStatus.Connected, result.Status);
            Assert.True(host.Runtime.Session.IsInWorld);
            Assert.Equal(
                ChatRequests.TalkOpcode,
                ActionOpcode(Assert.Single(LoginCommandSends(captured))));

            JsonElement[] events = LiveStatusFile.ReadAllLines(statusPath)
                .Select(static line =>
                    JsonDocument.Parse(line).RootElement.Clone())
                .ToArray();
            // "/version" used to be a second failure here: a client with no
            // window carried its own shorter list of client commands and threw
            // on the ones it had never implemented. There is one dispatcher
            // now, so the only failure left is the one that is a failure in a
            // chat box too -- "/" is not a command on either.
            Assert.Equal(
                [
                    "started", "connected", "characterList", "enteredWorld",
                    "loginCommandFailed",
                ],
                events.Select(static item =>
                    item.GetProperty("e").GetString()));
            JsonElement[] failures = events
                .Where(static item =>
                    item.GetProperty("e").GetString()
                        == "loginCommandFailed")
                .ToArray();
            Assert.Equal(1, failures[0].GetProperty("v").GetInt32());
            Assert.Equal(0, failures[0].GetProperty("commandIndex").GetInt32());
            Assert.Equal("/", failures[0].GetProperty("command").GetString());
            Assert.Equal(
                "Chat command routing returned UnknownCommand.",
                failures[0].GetProperty("error").GetString());
        }
        finally
        {
            if (File.Exists(statusPath))
                File.Delete(statusPath);
        }
    }

    [Fact]
    public void LoginCommandDelayIsGenerationScopedAcrossReconnect()
    {
        var time = new ManualTimeProvider();
        var captured = new List<byte[]>();
        var operations = new FixtureSessionOperations
        {
            GameActionCapture = body => captured.Add(body),
        };
        using var diagnosticsOutput = new StringWriter();
        using var credential = new HeadlessCredentialSecret(
            "fixture",
            "password");
        using var host = new HeadlessSessionHost(
            Descriptor(
                loginCommands: ["first", "second"],
                loginCommandDelayMs: 500),
            credential,
            new HeadlessDiagnosticWriter(diagnosticsOutput),
            operations,
            timeProvider: time);

        Assert.Equal(RuntimeSessionStartStatus.Connected, host.Start().Status);
        Assert.Equal(["first"], LoginCommandSends(captured).Select(TalkText));

        host.Tick(0.1d);
        Assert.Equal(["first"], LoginCommandSends(captured).Select(TalkText));

        Assert.Equal(RuntimeSessionStartStatus.Connected, host.Reconnect().Status);
        Assert.Equal(["first", "first"], LoginCommandSends(captured).Select(TalkText));

        time.Advance(TimeSpan.FromMilliseconds(499));
        host.Tick(0.1d);
        Assert.Equal(["first", "first"], LoginCommandSends(captured).Select(TalkText));

        time.Advance(TimeSpan.FromMilliseconds(1));
        host.Tick(0.1d);
        Assert.Equal(["first", "first", "second"], LoginCommandSends(captured).Select(TalkText));
    }

    [Fact]
    public void SingleSessionStartsReconnectsAndConvergesWithoutPresentation()
    {
        var operations = new FixtureSessionOperations();
        using var diagnosticsOutput = new StringWriter();
        using var credential = new HeadlessCredentialSecret(
            "fixture",
            "password");
        var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(diagnosticsOutput),
            operations);

        RuntimeSessionStartResult first = host.Start();
        ulong firstGeneration = host.Runtime.Generation.Value;
        host.Tick(0.015d);
        RuntimeSessionStartResult second = host.Reconnect();

        Assert.Equal(RuntimeSessionStartStatus.Connected, first.Status);
        Assert.Equal(RuntimeSessionStartStatus.Connected, second.Status);
        Assert.Equal(0x50000002u, first.CharacterId);
        Assert.Equal("Headless", host.ActiveCharacterName);
        Assert.True(host.Runtime.Session.IsInWorld);
        Assert.True(host.Runtime.Generation.Value > firstGeneration);
        Assert.Equal(2, operations.CreatedSessionCount);
        Assert.Equal(1, operations.DisposedSessionCount);

        host.Dispose();

        Assert.Equal(2, operations.DisposedSessionCount);
        Assert.True(host.Runtime.CaptureOwnership().IsConverged);
        Assert.True(credential.IsDisposed);
        string diagnostics = diagnosticsOutput.ToString();
        Assert.Contains("\"state\":\"start-result\"", diagnostics);
        Assert.DoesNotContain("password", diagnostics);
        Assert.DoesNotContain("AcDream.App", diagnostics);
    }

    [Fact]
    public void LoginAutomationRefusesLogoutOutsideTheWorld()
    {
        var operations = new FixtureSessionOperations();
        using var credential = new HeadlessCredentialSecret(
            "fixture",
            "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(new StringWriter()),
            operations);

        Assert.False(host.Plugins.Host.Automation.Login.CanRequestLogout);
        Assert.False(host.Plugins.Host.Automation.Login.RequestLogout());
        Assert.Equal(0, operations.RequestCharacterLogOffCount);
    }

    [Fact]
    public void TransitGuardRefusesLogoutDuringAPendingTeleport()
    {
        var operations = new FixtureSessionOperations();
        using var credential = new HeadlessCredentialSecret(
            "fixture",
            "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(new StringWriter()),
            operations);
        host.Start();
        host.Runtime.TransitOwner.TryQueueTeleportStart(1);

        Assert.False(host.Plugins.Host.Automation.Login.CanRequestLogout);
        Assert.False(host.Plugins.Host.Automation.Login.RequestLogout());
        Assert.Equal(0, operations.RequestCharacterLogOffCount);
    }

    [Fact]
    public void RequestLogoutSendsOneLogoffAndCanRequestLogoutGoesFalseWhilePending()
    {
        var operations = new FixtureSessionOperations();
        using var credential = new HeadlessCredentialSecret(
            "fixture",
            "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(new StringWriter()),
            operations);
        host.Start();

        Assert.True(host.Plugins.Host.Automation.Login.CanRequestLogout);
        Assert.True(host.Plugins.Host.Automation.Login.RequestLogout());

        Assert.Equal(1, operations.RequestCharacterLogOffCount);
        Assert.False(host.Plugins.Host.Automation.Login.CanRequestLogout);
        Assert.False(host.IsPolicyComplete);
    }

    [Fact]
    public void SecondRequestLogoutSendsNothing()
    {
        var operations = new FixtureSessionOperations();
        using var credential = new HeadlessCredentialSecret(
            "fixture",
            "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(new StringWriter()),
            operations);
        host.Start();
        Assert.True(host.Plugins.Host.Automation.Login.RequestLogout());

        Assert.False(host.Plugins.Host.Automation.Login.RequestLogout());

        Assert.Equal(1, operations.RequestCharacterLogOffCount);
    }

    [Fact]
    public void RequestLogoutAfterAReconnectSendsAFreshLogoff()
    {
        var operations = new FixtureSessionOperations();
        using var credential = new HeadlessCredentialSecret(
            "fixture",
            "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(new StringWriter()),
            operations);
        host.Start();
        Assert.True(host.Plugins.Host.Automation.Login.RequestLogout());
        Assert.Equal(1, operations.RequestCharacterLogOffCount);

        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            host.Reconnect().Status);

        Assert.True(host.Plugins.Host.Automation.Login.RequestLogout());
        Assert.Equal(2, operations.RequestCharacterLogOffCount);
    }

    [Fact]
    public void ConfirmationCompletesTheLogoffOnceAndEndsTheSessionSuccessfully()
    {
        string statusPath = Path.Combine(
            Path.GetTempPath(),
            $"acdream-headless-logout-confirmed-{Guid.NewGuid():N}.jsonl");
        try
        {
            var operations = new FixtureSessionOperations();
            using var credential = new HeadlessCredentialSecret(
                "fixture",
                "password");
            bool confirmed = false;
            using var host = new HeadlessSessionHost(
                Descriptor(statusFile: statusPath),
                credential,
                new HeadlessDiagnosticWriter(new StringWriter()),
                operations,
                logoutConfirmedOverride: () => confirmed);
            host.Start();
            Assert.True(host.Plugins.Host.Automation.Login.RequestLogout());

            confirmed = true;
            host.Tick(0.015d);

            Assert.Equal(1, operations.ReturnToCharacterSelectCount);
            Assert.True(host.IsPolicyComplete);
            Assert.False(host.IsFaulted);

            // CompleteCharacterLogOff fires exactly once, even if the scheduler ticks it again.
            host.Tick(0.015d);
            Assert.Equal(1, operations.ReturnToCharacterSelectCount);

            // Nothing re-enters the world once the session's logout policy is complete.
            int enterWorldCalls = operations.EnterWorldCallCount;
            host.Tick(0.015d);
            Assert.Equal(enterWorldCalls, operations.EnterWorldCallCount);
            Assert.True(host.IsPolicyComplete);

            host.Dispose();
            string exitedLine = LiveStatusFile.ReadAllLines(statusPath)
                .Single(static line =>
                    JsonDocument.Parse(line).RootElement.GetProperty("e").GetString()
                        == "exited");
            using JsonDocument exited = JsonDocument.Parse(exitedLine);
            Assert.Equal(
                (int)HeadlessExitCode.Success,
                exited.RootElement.GetProperty("code").GetInt32());
        }
        finally
        {
            if (File.Exists(statusPath))
                File.Delete(statusPath);
        }
    }

    [Fact]
    public void LogoutNeverConfirmedByTheDeadlineFaultsTheSessionWithARuntimeErrorExitCode()
    {
        string statusPath = Path.Combine(
            Path.GetTempPath(),
            $"acdream-headless-logout-timeout-{Guid.NewGuid():N}.jsonl");
        try
        {
            var operations = new FixtureSessionOperations();
            using var credential = new HeadlessCredentialSecret(
                "fixture",
                "password");
            var time = new ManualTimeProvider();
            using var host = new HeadlessSessionHost(
                Descriptor(statusFile: statusPath),
                credential,
                new HeadlessDiagnosticWriter(new StringWriter()),
                operations,
                timeProvider: time,
                logoutConfirmedOverride: () => false);
            host.Start();
            Assert.True(host.Plugins.Host.Automation.Login.RequestLogout());

            host.Tick(0.015d);
            Assert.False(host.IsPolicyComplete);

            time.Advance(HeadlessLogoutAutomation.DefaultConfirmationDeadline
                + TimeSpan.FromSeconds(1));
            host.Tick(0.015d);

            Assert.True(host.IsFaulted);
            Assert.True(host.IsPolicyComplete);
            Assert.IsType<TimeoutException>(host.Fault);
            Assert.Equal(0, operations.ReturnToCharacterSelectCount);

            host.Dispose();
            string exitedLine = LiveStatusFile.ReadAllLines(statusPath)
                .Single(static line =>
                    JsonDocument.Parse(line).RootElement.GetProperty("e").GetString()
                        == "exited");
            using JsonDocument exited = JsonDocument.Parse(exitedLine);
            Assert.Equal(
                (int)HeadlessExitCode.RuntimeError,
                exited.RootElement.GetProperty("code").GetInt32());
        }
        finally
        {
            if (File.Exists(statusPath))
                File.Delete(statusPath);
        }
    }

    [Fact]
    public void RequestLogoutFromANestedTickHandlerIsRetriedLaterInTheSameTick()
    {
        var operations = new FixtureSessionOperations();
        using var credential = new HeadlessCredentialSecret(
            "fixture",
            "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(new StringWriter()),
            operations);
        host.Start();

        bool requested = false;
        bool? acceptedDuringNestedCall = null;
        int? sentCountDuringNestedCall = null;
        operations.OnTick = () =>
        {
            if (requested)
                return;
            requested = true;
            acceptedDuringNestedCall =
                host.Plugins.Host.Automation.Login.RequestLogout();
            sentCountDuringNestedCall = operations.RequestCharacterLogOffCount;
        };

        host.Tick(0.015d);

        Assert.True(acceptedDuringNestedCall);
        // Refused inside the nested operations.Tick call (operation depth != 0); not sent yet.
        Assert.Equal(0, sentCountDuringNestedCall);
        // The sequencer's own Tick, later in the same host.Tick, retried it at depth 0.
        Assert.Equal(1, operations.RequestCharacterLogOffCount);
    }

    [Fact]
    public void ReconnectPublishesDisconnectedBeforeTheSecondConnectedEdge()
    {
        string statusPath = Path.Combine(
            Path.GetTempPath(),
            $"acdream-headless-reconnect-status-{Guid.NewGuid():N}.jsonl");
        try
        {
            var operations = new FixtureSessionOperations();
            using var diagnosticsOutput = new StringWriter();
            using var credential = new HeadlessCredentialSecret(
                "fixture",
                "password");
            using var host = new HeadlessSessionHost(
                Descriptor(statusFile: statusPath),
                credential,
                new HeadlessDiagnosticWriter(diagnosticsOutput),
                operations);

            Assert.Equal(
                RuntimeSessionStartStatus.Connected,
                host.Start().Status);
            Assert.Equal(
                RuntimeSessionStartStatus.Connected,
                host.Reconnect().Status);
            host.Dispose();

            JsonElement[] events = LiveStatusFile.ReadAllLines(statusPath)
                .Select(static line => JsonDocument.Parse(line).RootElement.Clone())
                .ToArray();
            Assert.Equal(
                [
                    "started", "connected", "characterList", "enteredWorld",
                    "disconnected", "connected", "characterList", "enteredWorld",
                    "disconnected", "exited",
                ],
                events.Select(static item => item.GetProperty("e").GetString()));

            JsonElement[] disconnected = events
                .Where(static item =>
                    item.GetProperty("e").GetString() == "disconnected")
                .ToArray();
            Assert.Equal(2, disconnected.Length);
            Assert.Equal(
                "reconnect",
                disconnected[0].GetProperty("reason").GetString());
            Assert.Equal(
                "stopped",
                disconnected[1].GetProperty("reason").GetString());

            JsonElement exited = events[^1];
            Assert.Equal(0, exited.GetProperty("code").GetInt32());
            Assert.Equal("graceful", exited.GetProperty("reason").GetString());
        }
        finally
        {
            if (File.Exists(statusPath))
                File.Delete(statusPath);
        }
    }

    [Fact]
    public void PluginRequestCloseEndsOnlyThisSessionAndRecordsTheNormalTerminalEvent()
    {
        // IHostWindow.RequestClose on a headless host never touches the
        // process-wide quit token: it ends this session's own connection
        // through the same Stop()+terminal-status path a policy deciding
        // it is complete already leaves for the process's final disposal.
        // The status file records the normal "exited"/"graceful" event
        // right away, and IsPolicyComplete flips true immediately so the
        // scheduler excludes this session from further ticks -- all while
        // the session's own objects are not disposed yet (that still
        // happens later, at the process's own final disposal, unchanged
        // from how a policy-completed session already behaves today).
        string statusPath = Path.Combine(
            Path.GetTempPath(),
            $"acdream-headless-requestclose-status-{Guid.NewGuid():N}.jsonl");
        try
        {
            var operations = new FixtureSessionOperations();
            using var diagnosticsOutput = new StringWriter();
            using var credential = new HeadlessCredentialSecret(
                "fixture",
                "password");
            using var host = new HeadlessSessionHost(
                Descriptor(statusFile: statusPath),
                credential,
                new HeadlessDiagnosticWriter(diagnosticsOutput),
                operations);

            Assert.Equal(
                RuntimeSessionStartStatus.Connected,
                host.Start().Status);
            Assert.False(host.IsPolicyComplete);

            HostWindowResult result = host.Plugins.Host.Window.RequestClose();

            Assert.Equal(HostWindowStatus.Done, result.Status);
            Assert.True(host.IsPolicyComplete);
            Assert.False(host.Runtime.Session.IsInWorld);

            // A second call is a harmless no-op, not a second "exited"
            // write (SessionStatusWriter itself latches off further
            // writes once "exited" has landed, but this also proves the
            // session-side idempotency guard does not throw or re-stop).
            HostWindowResult secondResult =
                host.Plugins.Host.Window.RequestClose();
            Assert.Equal(HostWindowStatus.Done, secondResult.Status);

            JsonElement[] events = LiveStatusFile.ReadAllLines(statusPath)
                .Select(static line => JsonDocument.Parse(line).RootElement.Clone())
                .ToArray();
            JsonElement exited = Assert.Single(
                events,
                static item => item.GetProperty("e").GetString() == "exited");
            Assert.Equal(0, exited.GetProperty("code").GetInt32());
            Assert.Equal("graceful", exited.GetProperty("reason").GetString());
        }
        finally
        {
            if (File.Exists(statusPath))
                File.Delete(statusPath);
        }
    }

    [Fact]
    public void PluginRequestCloseIsUnavailableAndQuarantinesWhenTeardownDoesNotConverge()
    {
        // A session whose live-session teardown throws (DisposeSession
        // failing here models a stuck transport) must not report success
        // and must not write a "graceful" exited event: every other
        // caller of Stop() (Dispose, Quarantine) treats a non-complete
        // RuntimeTeardownAcknowledgement as fatal, and RequestOwnGracefulStop
        // has to follow the same rule -- otherwise the status file would
        // claim a clean exit while the session is still actually stuck,
        // and the eventual real Dispose() would throw during process
        // shutdown after that false-positive status line.
        string statusPath = Path.Combine(
            Path.GetTempPath(),
            $"acdream-headless-requestclose-nonconverge-{Guid.NewGuid():N}.jsonl");
        try
        {
            var operations = new FixtureSessionOperations
            {
                ThrowOnDisposeSession = true,
            };
            using var diagnosticsOutput = new StringWriter();
            using var credential = new HeadlessCredentialSecret(
                "fixture",
                "password");
            using var host = new HeadlessSessionHost(
                Descriptor(statusFile: statusPath),
                credential,
                new HeadlessDiagnosticWriter(diagnosticsOutput),
                operations);

            Assert.Equal(
                RuntimeSessionStartStatus.Connected,
                host.Start().Status);

            HostWindowResult result = host.Plugins.Host.Window.RequestClose();

            Assert.Equal(HostWindowStatus.Unavailable, result.Status);
            Assert.True(host.IsFaulted);
            Assert.NotNull(host.Fault);
            // IsPolicyComplete is still true here -- through _faulted, the
            // same mechanism any other quarantined session already uses to
            // stop the scheduler from retrying it, not through the
            // graceful-stop flag this call failed to earn.
            Assert.True(host.IsPolicyComplete);

            if (File.Exists(statusPath))
            {
                JsonElement[] events = LiveStatusFile.ReadAllLines(statusPath)
                    .Select(static line =>
                        JsonDocument.Parse(line).RootElement.Clone())
                    .ToArray();
                Assert.DoesNotContain(
                    events,
                    static item =>
                        item.GetProperty("e").GetString() == "exited"
                            && item.GetProperty("reason").GetString()
                                == "graceful");
            }

            // Let the underlying teardown succeed once the test has
            // observed the stuck state, so this test's own `using host`
            // disposal at scope exit converges cleanly instead of
            // exercising the (separately expected) Dispose()-throws path
            // for a session that never recovers.
            operations.ThrowOnDisposeSession = false;
        }
        finally
        {
            if (File.Exists(statusPath))
                File.Delete(statusPath);
        }
    }
    [Fact]
    public void StatusFileReceivesThePinnedLifecycleEventsInOrder()
    {
        string statusPath = Path.Combine(
            Path.GetTempPath(),
            $"acdream-headless-status-{Guid.NewGuid():N}.jsonl");
        try
        {
            var operations = new FixtureSessionOperations();
            using var diagnosticsOutput = new StringWriter();
            using var credential = new HeadlessCredentialSecret(
                "fixture",
                "password");
            using var host = new HeadlessSessionHost(
                Descriptor(statusFile: statusPath),
                credential,
                new HeadlessDiagnosticWriter(diagnosticsOutput),
                operations);

            RuntimeSessionStartResult started = host.Start();
            Assert.Equal(RuntimeSessionStartStatus.Connected, started.Status);
            host.Dispose();

            string[] lines = LiveStatusFile.ReadAllLines(statusPath);
            string[] eventNames = lines
                .Select(line => JsonDocument.Parse(line)
                    .RootElement.GetProperty("e").GetString()!)
                .ToArray();
            Assert.Equal(
                [
                    "started", "connected", "characterList", "enteredWorld",
                    "disconnected", "exited",
                ],
                eventNames);

            using JsonDocument characterListDoc = JsonDocument.Parse(
                lines[Array.IndexOf(eventNames, "characterList")]);
            JsonElement characterList = characterListDoc.RootElement;
            Assert.Equal("account", characterList.GetProperty("accountName").GetString());
            Assert.Equal(2, characterList.GetProperty("characters").GetArrayLength());

            using JsonDocument enteredWorldDoc = JsonDocument.Parse(
                lines[Array.IndexOf(eventNames, "enteredWorld")]);
            Assert.Equal(
                0x50000002u,
                enteredWorldDoc.RootElement.GetProperty("characterId").GetUInt32());

            using JsonDocument exitedDoc = JsonDocument.Parse(
                lines[Array.IndexOf(eventNames, "exited")]);
            Assert.Equal(0, exitedDoc.RootElement.GetProperty("code").GetInt32());

            string contents = LiveStatusFile.ReadAllText(statusPath);
            Assert.DoesNotContain("password", contents, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(statusPath))
                File.Delete(statusPath);
        }
    }

    [Fact]
    public void AbsentStatusFileConstructsANoOpWriter()
    {
        var operations = new FixtureSessionOperations();
        using var diagnosticsOutput = new StringWriter();
        using var credential = new HeadlessCredentialSecret("fixture", "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(diagnosticsOutput),
            operations);

        RuntimeSessionStartResult started = host.Start();

        Assert.Equal(RuntimeSessionStartStatus.Connected, started.Status);
    }

    [Fact]
    public void ProbeSessionEmitsRosterThenExitsSuccessfullyWithoutEnteringWorld()
    {
        string statusPath = Path.Combine(
            Path.GetTempPath(),
            $"acdream-headless-probe-status-{Guid.NewGuid():N}.jsonl");
        try
        {
            var operations = new FixtureSessionOperations();
            using var diagnosticsOutput = new StringWriter();
            using var credential = new HeadlessCredentialSecret(
                "fixture",
                "password");
            using var host = new HeadlessSessionHost(
                ProbeDescriptor(statusFile: statusPath),
                credential,
                new HeadlessDiagnosticWriter(diagnosticsOutput),
                operations);

            RuntimeSessionStartResult started = host.Start();
            Assert.Equal(RuntimeSessionStartStatus.ProbeComplete, started.Status);
            Assert.Equal(0, operations.EnterWorldCallCount);
            Assert.False(host.Runtime.Session.IsInWorld);

            host.Dispose();

            Assert.Equal(0, operations.EnterWorldCallCount);
            Assert.True(host.Runtime.CaptureOwnership().IsConverged);

            string[] lines = LiveStatusFile.ReadAllLines(statusPath);
            string[] eventNames = lines
                .Select(line => JsonDocument.Parse(line)
                    .RootElement.GetProperty("e").GetString()!)
                .ToArray();
            Assert.DoesNotContain("enteredWorld", eventNames);
            Assert.Contains("characterList", eventNames);
            Assert.Contains("exited", eventNames);
            Assert.True(
                Array.IndexOf(eventNames, "characterList")
                    < Array.IndexOf(eventNames, "exited"),
                "characterList must land before the terminal exited event.");

            using JsonDocument exitedDoc = JsonDocument.Parse(
                lines[Array.IndexOf(eventNames, "exited")]);
            Assert.Equal(0, exitedDoc.RootElement.GetProperty("code").GetInt32());
            Assert.Equal(
                "probe",
                exitedDoc.RootElement.GetProperty("reason").GetString());

            string contents = LiveStatusFile.ReadAllText(statusPath);
            Assert.DoesNotContain("password", contents, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(statusPath))
                File.Delete(statusPath);
        }
    }

    [Fact]
    public async Task ProcessHostMapsProbeCompleteStartToSuccessExitCode()
    {
        var configuration = new HeadlessConfiguration
        {
            Version = 1,
            Sessions =
            [
                ProbeDescriptor(
                    provider: HeadlessCredentialProviderKind.StandardInput,
                    credentialReference: "probe-password"),
            ],
        };
        HeadlessPathSet paths = IsolatedHeadlessPaths.Create();
        using var diagnostics = new StringWriter();
        var operations = new FixtureSessionOperations();
        using var host = new HeadlessProcessHost(
            configuration,
            paths,
            new System.IO.StringReader("probe-password" + Environment.NewLine),
            diagnostics,
            operations);
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromSeconds(10));

        HeadlessExitCode result = await host.RunAsync(cancellation.Token);

        Assert.Equal(HeadlessExitCode.Success, result);
        Assert.Equal(0, operations.EnterWorldCallCount);
        Assert.False(cancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task ProbeWithoutRosterReportsTheProcessConnectionErrorExactlyOnce()
    {
        string statusPath = Path.Combine(
            Path.GetTempPath(),
            $"acdream-headless-probe-no-roster-{Guid.NewGuid():N}.jsonl");
        string dataDirectory = CreateIsolatedDataDirectory();
        try
        {
            var configuration = new HeadlessConfiguration
            {
                Version = 1,
                Sessions =
                [
                    ProbeDescriptor(
                        provider: HeadlessCredentialProviderKind.StandardInput,
                        credentialReference: "probe-password",
                        statusFile: statusPath),
                ],
            };
            var operations = new FixtureSessionOperations
            {
                Characters = null,
            };
            using var diagnostics = new StringWriter();
            using var host = new HeadlessProcessHost(
                configuration,
                IsolatedHeadlessPaths.Create(),
                new System.IO.StringReader(
                    "probe-password" + Environment.NewLine),
                diagnostics,
                operations);

            HeadlessExitCode result = await host.RunAsync(
                CancellationToken.None);

            Assert.Equal(HeadlessExitCode.ConnectionError, result);
            Assert.Equal(0, operations.EnterWorldCallCount);
            Assert.Equal(1, operations.DisposedSessionCount);

            host.Dispose();
            host.Dispose();

            string[] lines = LiveStatusFile.ReadAllLines(statusPath);
            JsonElement[] events = lines
                .Select(static line =>
                    JsonDocument.Parse(line).RootElement.Clone())
                .ToArray();
            Assert.Equal(
                ["started", "connected", "disconnected", "exited"],
                events.Select(static item =>
                    item.GetProperty("e").GetString()));
            Assert.DoesNotContain(
                events,
                static item =>
                    item.GetProperty("e").GetString() == "characterList");
            JsonElement exited = Assert.Single(
                events,
                static item => item.GetProperty("e").GetString() == "exited");
            Assert.Equal(
                (int)result,
                exited.GetProperty("code").GetInt32());
            Assert.Equal(
                "connection-error",
                exited.GetProperty("reason").GetString());
        }
        finally
        {
            if (File.Exists(statusPath))
                File.Delete(statusPath);
            DeleteIsolatedDataDirectory(dataDirectory);
        }
    }

    [Fact]
    public async Task ProbeSessionSharingAProcessDoesNotTearDownASiblingPlaySession()
    {
        var configuration = new HeadlessConfiguration
        {
            Version = 1,
            Sessions =
            [
                ProbeDescriptor(
                    "probe-sibling",
                    provider: HeadlessCredentialProviderKind.StandardInput,
                    credentialReference: "probe-password"),
                Descriptor(
                    HeadlessCredentialProviderKind.StandardInput,
                    "play-password"),
            ],
        };
        HeadlessPathSet paths = IsolatedHeadlessPaths.Create();
        using var diagnostics = new StringWriter();
        var operations = new FixtureSessionOperations();
        using var host = new HeadlessProcessHost(
            configuration,
            paths,
            new System.IO.StringReader(
                "probe-password" + Environment.NewLine
                + "play-password" + Environment.NewLine),
            diagnostics,
            operations);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        HeadlessExitCode result = await host.RunAsync(cancellation.Token);

        Assert.Equal(HeadlessExitCode.Success, result);
        Assert.Equal(2, host.Sessions.Count);
        HeadlessSessionHost probeSession = Assert.Single(
            host.Sessions,
            s => s.SessionId == "probe-sibling");
        HeadlessSessionHost playSession = Assert.Single(
            host.Sessions,
            s => s.SessionId == "bot");
        Assert.False(probeSession.Runtime.Session.IsInWorld);
        Assert.True(playSession.Runtime.Session.IsInWorld);
        Assert.False(playSession.IsFaulted);
    }

    [Fact]
    public async Task IdlePolicyEntersWorldRunsUntilCancellationAndConvergesExactlyOnce()
    {
        string statusPath = Path.Combine(
            Path.GetTempPath(),
            $"acdream-headless-idle-status-{Guid.NewGuid():N}.jsonl");
        string dataDirectory = CreateIsolatedDataDirectory();
        try
        {
            var configuration = new HeadlessConfiguration
            {
                Version = 1,
                Sessions =
                [
                    Descriptor(
                        HeadlessCredentialProviderKind.StandardInput,
                        "stdin-bot",
                        statusFile: statusPath),
                ],
            };
            HeadlessPathSet paths = IsolatedHeadlessPaths.Create();
            using var diagnostics = new StringWriter();
            var operations = new FixtureSessionOperations();
            using var host = new HeadlessProcessHost(
                configuration,
                paths,
                new System.IO.StringReader(
                    "process-password" + Environment.NewLine),
                diagnostics,
                operations);
            using var cancellation = new CancellationTokenSource();

            Task<HeadlessExitCode> run = host.RunAsync(cancellation.Token);
            var timeout = Stopwatch.StartNew();
            while (operations.TickCallCount < 3
                && !run.IsCompleted
                && timeout.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(5);
            }

            Assert.True(
                operations.TickCallCount >= 3,
                $"Expected at least 3 idle scheduler turns, observed {operations.TickCallCount}.");
            Assert.False(run.IsCompleted);
            Assert.Equal(1, operations.EnterWorldCallCount);
            Assert.Equal("Headless", host.Session.ActiveCharacterName);
            Assert.True(host.Session.Runtime.Session.IsInWorld);
            Assert.False(host.Session.IsPolicyComplete);
            Assert.Equal(
                ["started", "connected", "characterList", "enteredWorld"],
                ReadStatusEventNames(statusPath));

            cancellation.Cancel();
            HeadlessExitCode result = await run.WaitAsync(
                TimeSpan.FromSeconds(10));

            Assert.Equal(HeadlessExitCode.Success, result);
            Assert.True(host.Session.Runtime.Session.IsInWorld);
            Assert.Equal(
                ["started", "connected", "characterList", "enteredWorld"],
                ReadStatusEventNames(statusPath));

            host.Dispose();
            host.Dispose();

            Assert.True(host.Session.Runtime.CaptureOwnership().IsConverged);
            Assert.Equal(1, operations.DisposedSessionCount);
            string[] lines = LiveStatusFile.ReadAllLines(statusPath);
            string[] eventNames = ReadStatusEventNames(statusPath);
            Assert.Equal(
                [
                    "started", "connected", "characterList", "enteredWorld",
                    "disconnected", "exited",
                ],
                eventNames);

            using JsonDocument disconnected = JsonDocument.Parse(
                lines[Array.IndexOf(eventNames, "disconnected")]);
            Assert.Equal(
                "stopped",
                disconnected.RootElement.GetProperty("reason").GetString());

            using JsonDocument exited = JsonDocument.Parse(
                lines[Array.IndexOf(eventNames, "exited")]);
            JsonElement exit = exited.RootElement;
            Assert.Equal(0, exit.GetProperty("code").GetInt32());
            string? exitReason = exit.GetProperty("reason").GetString();
            Assert.False(string.IsNullOrWhiteSpace(exitReason));
            Assert.NotEqual("fault", exitReason);
            Assert.NotEqual("probe", exitReason);
            Assert.DoesNotContain(
                "process-password",
                LiveStatusFile.ReadAllText(statusPath),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "process-password",
                diagnostics.ToString(),
                StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(statusPath))
                File.Delete(statusPath);
            DeleteIsolatedDataDirectory(dataDirectory);
        }
    }

    [Fact]
    public async Task DirectCredentialsOverrideSingleConfiguredSession()
    {
        var configuration = new HeadlessConfiguration
        {
            Version = 1,
            Sessions = [Descriptor()],
        };
        HeadlessPathSet paths = IsolatedHeadlessPaths.Create();
        using var diagnostics = new StringWriter();
        var operations = new FixtureSessionOperations();
        using var host = new HeadlessProcessHost(
            configuration,
            paths,
            TextReader.Null,
            diagnostics,
            operations,
            directCredentials: new HeadlessDirectCredentials(
                "direct-account",
                "direct-secret"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        HeadlessExitCode result =
            await host.RunAsync(cancellation.Token);

        Assert.Equal(HeadlessExitCode.Success, result);
        Assert.Equal("direct-account", operations.LastUser);
        Assert.Equal("direct-secret", operations.LastPassword);
        Assert.DoesNotContain(
            "direct-secret",
            diagnostics.ToString());
    }

    [Fact]
    public async Task DirectCredentialsPreserveDeclaredCharacterOptions()
    {
        var configuration = new HeadlessConfiguration
        {
            Version = 1,
            Sessions =
            [
                Descriptor(characterOptions: new Dictionary<string, bool>
                {
                    ["AutoRepeatAttack"] = true,
                }),
            ],
        };
        HeadlessPathSet paths = IsolatedHeadlessPaths.Create();
        using var diagnostics = new StringWriter();
        var operations = new FixtureSessionOperations();
        using var host = new HeadlessProcessHost(
            configuration,
            paths,
            TextReader.Null,
            diagnostics,
            operations,
            directCredentials: new HeadlessDirectCredentials(
                "direct-account",
                "direct-secret"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        HeadlessExitCode result =
            await host.RunAsync(cancellation.Token);

        Assert.Equal(HeadlessExitCode.Success, result);
        Assert.Equal("direct-account", operations.LastUser);
        Assert.NotNull(host.Session.OptionsSeeder);
        Assert.True(host.Session.OptionsSeeder!.HasDeclaredOptions);
    }

    [Fact]
    public void DirectFirstEntryCompletion_InvokesOnLoginCompleteSentHookThroughProductionWiring()
    {
        var operations = new FixtureSessionOperations();
        using var credential = new HeadlessCredentialSecret(
            "fixture",
            "password");
        using var host = new HeadlessSessionHost(
            Descriptor(characterOptions: new Dictionary<string, bool>
            {
                ["IgnoreAllegianceRequests"] = true,
            }),
            credential,
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations);
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            host.Start().Status);
        Assert.NotNull(host.OptionsSeeder);

        WorldSession session = operations.Sessions[^1];
        var sent = new List<byte[]>();
        session.GameActionCapture = body => sent.Add(body);

        object eventRoute = typeof(HeadlessSessionHost)
            .GetField(
                "_eventRoute",
                BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(host)
            ?? throw new InvalidOperationException(
                "HeadlessSessionHost constructed no event route.");
        var localPlayerCompleted =
            (Action<RuntimeEntityRecord>?)typeof(HeadlessSessionEventRoute)
                .GetField(
                    "_localPlayerCompleted",
                    BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(eventRoute);
        Assert.NotNull(localPlayerCompleted);

        const uint playerGuid = 0x50000009u;
        host.Runtime.PlayerIdentity.ServerGuid = playerGuid;
        RuntimeEntityRecord record = host.Runtime.EntityObjects
            .RegisterEntity(Spawn(playerGuid))
            .Canonical!;

        localPlayerCompleted!(record);

        Assert.Contains(
            sent,
            body => body.SequenceEqual(GameActionLoginComplete.Build()));

        // No PlayerDescription has landed yet — HasServerSeed is still
        // false, so nothing beyond LoginComplete can have sent.
        Assert.DoesNotContain(
            sent,
            body => ActionOpcode(body)
                == SocialActions.SetSingleCharacterOptionOpcode);

        session.GameEvents.Dispatch(
            GameEventEnvelope.TryParse(
                WrapPlayerDescriptionEnvelope(options1: 0u, options2: 0u))!
                .Value);

        Assert.Contains(
            sent,
            body => ActionOpcode(body)
                == SocialActions.SetSingleCharacterOptionOpcode);
    }

    [Fact]
    public void DirectFrameUsesSharedRetailOrderAndMovementCadence()
    {
        var operations = new FixtureSessionOperations();
        using var credential = new HeadlessCredentialSecret(
            "fixture",
            "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations);
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            host.Start().Status);
        HydrateGroundedPlayer(host.Runtime);
        var sent = new List<(byte[] Body, double Time)>();
        var trace = new RuntimeTraceRecorder();
        using IDisposable subscription =
            host.Runtime.Subscribe(trace);
        operations.Sessions[^1].GameActionCapture =
            body => sent.Add((
                body,
                host.Runtime.Clock.SimulationTimeSeconds));

        RuntimeCommandResult intent = host.Commands.Movement.SetIntent(
            host.Runtime.Generation,
            new MovementInput(Forward: true, Run: true));
        host.Tick(0.015d);

        Assert.True(intent.Accepted);
        Assert.Contains(
            trace.Entries,
            static entry =>
                entry.Kind == RuntimeTraceKind.Movement);
        Assert.Equal(
            [
                MoveToState.MoveToStateAction,
                AutonomousPosition.AutonomousPositionAction,
            ],
            sent.Select(static entry =>
                ActionOpcode(entry.Body)).ToArray());

        sent.Clear();
        for (int index = 0; index < 70; index++)
            host.Tick(0.015d);

        (byte[] Body, double Time)[] positions = sent
            .Where(static entry =>
                ActionOpcode(entry.Body)
                    == AutonomousPosition.AutonomousPositionAction)
            .ToArray();
        Assert.InRange(positions.Length, 1, 2);
        Assert.True(positions[^1].Time >= 1d);
        if (positions.Length == 2)
        {
            Assert.True(
                positions[1].Time - positions[0].Time >= 0.99d);
        }
        Assert.DoesNotContain(
            sent,
            entry => ActionOpcode(entry.Body)
                == MoveToState.MoveToStateAction);
    }

    [Fact]
    public void TeardownRetriesOnlyTheUnfinishedSuffix()
    {
        var operations = new FixtureSessionOperations();
        var writer = new FailOnceTextWriter();
        using var credential = new HeadlessCredentialSecret(
            "fixture",
            "password");
        var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(writer),
            operations);
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            host.Start().Status);
        writer.FailNextWrite = true;

        Assert.Throws<IOException>(host.Dispose);
        Assert.Equal(1, operations.DisposedSessionCount);

        host.Dispose();

        Assert.Equal(1, operations.DisposedSessionCount);
        Assert.True(host.Runtime.CaptureOwnership().IsConverged);
    }

    [Fact]
    public void TickRetriesAPreviouslyDeclinedPlacementThroughTheRealEventRoute()
    {
        const uint remote = 0x70004301u;
        const uint landblock = 0xA9B40000u;
        const uint cell = landblock | 0x0001u;
        const float height = 6f;

        var operations = new FixtureSessionOperations();
        using var credential = new HeadlessCredentialSecret(
            "fixture",
            "password");
        var sink = new DecliningThenAcceptingPlacementSink();
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations,
            placementSinkOverride: sink);
        GameRuntime runtime = host.Runtime;
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            host.Start().Status);

        runtime.EntityObjects.Physics.ObserveLocalWorldFrame(
            cell, teleportAdvanced: false);
        runtime.EntityObjects.Physics.SetPosition.BeginCollisionGeneration(
            landblock, 1UL);
        AddFlatLandblock(runtime.EntityObjects.Physics.Engine);
        runtime.EntityObjects.Physics.SetPosition.CommitCollisionGeneration(
            landblock, 1UL, ready: true);

        RuntimeEntityRecord record = runtime.EntityObjects
            .RegisterEntity(Spawn(remote, cell))
            .Canonical!;
        runtime.EntityObjects.Entities.SetFinalPhysicsState(
            record, PhysicsStateFlags.Gravity);
        runtime.EntityObjects.Entities.SetFullCell(
            record, cell, landblock);
        var body = new PhysicsBody
        {
            Position = new Vector3(10f, 10f, height),
            Orientation = Quaternion.Identity,
            LastUpdateTime = 1d,
            State = PhysicsStateFlags.Gravity,
            TransientState = TransientStateFlags.Active,
        };
        body.SnapToCell(cell, body.Position, body.Position);
        runtime.EntityObjects.Entities.SetPhysicsBody(record, body);
        record.ObjectClock.Activate();
        runtime.EntityObjects.Physics.AcknowledgeSpatialProjection(
            record, spatial: true);

        RuntimeEntityPlacementToken token = runtime.EntityObjects.Physics
            .SetPosition.TryBeginExclusiveAuthoredPlacement(
                record,
                record.PositionAuthorityVersion,
                RuntimeSetPositionOperationKind.RemoteAuthoritative);
        Assert.True(token.IsValid);
        RuntimeSetPositionMoverPreparationStatus status = runtime.EntityObjects
            .Physics.SetPosition.TryPrepareAndSubmitAuthoredPlacement(
                record,
                token,
                RuntimeSetPositionOperationKind.RemoteAuthoritative,
                PhysicsSetPositionFlags.Teleport | PhysicsSetPositionFlags.Slide,
                new LoadedSetupCollisionSource(),
                gameTime: runtime.Clock.SimulationTimeSeconds,
                out RuntimeSetPositionOutcome outcome,
                resolveWorldOffsetFromRuntimeFrame: true);
        Assert.Equal(RuntimeSetPositionMoverPreparationStatus.Prepared, status);
        Assert.Equal(
            RuntimeSetPositionStatus.CommittedHostAcknowledgementPending,
            outcome.Status);

        // The production HeadlessSessionEventRoute's subscription attached
        // during host.Start() already observed this Place synchronously —
        // the fake sink is still declining, so it must remain unacknowledged.
        Assert.Equal(1, sink.CallCount);
        Assert.True(
            runtime.EntityObjects.Physics.SetPosition.TryPeekProjection(
                out _));

        // The sink starts accepting (mirrors a landblock finishing streaming
        // in) — driving ONE real host tick is what must re-offer the head,
        // through Tick's own wiring, not a hand-built route.
        sink.Accept = true;
        host.Tick(0.015d);

        Assert.Equal(2, sink.CallCount);
        Assert.False(
            runtime.EntityObjects.Physics.SetPosition.TryPeekProjection(
                out _));
    }

    private sealed class DecliningThenAcceptingPlacementSink
        : IRuntimePlacementProjectionSink
    {
        internal int CallCount { get; private set; }
        internal bool Accept { get; set; }

        public bool TryApply(in RuntimePlacementProjectionSnapshot projection)
        {
            CallCount++;
            return Accept;
        }
    }

    [Fact]
    public void TheCollisionFollowsTheLocalPlayerWalkingIntoTheNextLandblock()
    {
        var operations = new FixtureSessionOperations();
        using var credential = new HeadlessCredentialSecret(
            "fixture",
            "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations);
        GameRuntime runtime = host.Runtime;
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            host.Start().Status);
        const uint player = 0x50000022u;
        runtime.PlayerIdentity.ServerGuid = player;
        runtime.EntityObjects.Physics.SetPosition.BeginCollisionGeneration(
            0xA9B40000u, 1UL);
        AddFlatLandblock(runtime.EntityObjects.Physics.Engine);
        runtime.EntityObjects.Physics.SetPosition.CommitCollisionGeneration(
            0xA9B40000u, 1UL, ready: true);
        AcDream.Runtime.Session.RuntimeFirstEntryDriveController firstEntry =
            CreateFirstEntryDrive(runtime);
        RuntimeEntityRecord record = runtime.EntityObjects
            .RegisterEntityWithInitialResidence(Spawn(player), isLocalPlayer: true)
            .Canonical!;
        Assert.True(runtime.EntityObjects.ApplyAcceptedSpawn(
            record,
            record.CreateIntegrationVersion,
            record.Snapshot,
            replaceGeneration: false));
        var collision = new FixtureCollisionNeighborhood();
        var projection = new HeadlessSessionWorldProjection(
            runtime,
            collision,
            firstEntry,
            CreateAcceptedPositionDrive(runtime));
        projection.ProjectSpawn(record, isLocalPlayer: true);
        PlayerMovementController controller =
            Assert.IsType<PlayerMovementController>(
                runtime.MovementOwner.Controller);
        int centred = collision.CenterCount;

        // A step north over the landblock's edge: the server has said
        // nothing, the character's own movement moved it.
        controller.SeedPlacementForTest(
            new Vector3(48f, 193f, 50f),
            0xA9B50001u, new Vector3(48f, 1f, 50f));
        projection.PumpFirstEntry();

        Assert.Equal(
            0xA9B50000u,
            Assert.Single(collision.Followed) & 0xFFFF0000u);
        Assert.Equal(centred, collision.CenterCount);
    }

    [Fact]
    public void WorldProjectionHydratesCanonicalMovementAndTeleportState()
    {
        var operations = new FixtureSessionOperations();
        using var credential = new HeadlessCredentialSecret(
            "fixture",
            "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations);
        GameRuntime runtime = host.Runtime;
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            host.Start().Status);
        const uint player = 0x50000002u;
        runtime.PlayerIdentity.ServerGuid = player;
        runtime.EntityObjects.Physics.SetPosition.BeginCollisionGeneration(
            0xA9B40000u, 1UL);
        AddFlatLandblock(runtime.EntityObjects.Physics.Engine);
        runtime.EntityObjects.Physics.SetPosition.CommitCollisionGeneration(
            0xA9B40000u, 1UL, ready: true);
        AcDream.Runtime.Session.RuntimeFirstEntryDriveController firstEntry =
            CreateFirstEntryDrive(runtime);
        RuntimeEntityRecord record = runtime.EntityObjects
            .RegisterEntityWithInitialResidence(Spawn(player), isLocalPlayer: true)
            .Canonical!;
        Assert.True(runtime.EntityObjects.ApplyAcceptedSpawn(
            record,
            record.CreateIntegrationVersion,
            record.Snapshot,
            replaceGeneration: false));
        var collision = new FixtureCollisionNeighborhood();
        RuntimeAcceptedPositionDriveController acceptedPositionDrive =
            CreateAcceptedPositionDrive(runtime);
        var projection = new HeadlessSessionWorldProjection(
            runtime,
            collision,
            firstEntry,
            acceptedPositionDrive);

        projection.ProjectSpawn(record, isLocalPlayer: true);
        PlayerMovementController controller =
            Assert.IsType<PlayerMovementController>(
                runtime.MovementOwner.Controller);
        controller.SeedPlacementForTest(
            new Vector3(48f, 49f, 50f),
            0xA9B40001u, new Vector3(48f, 49f, 50f));
        projection.ProjectPosition(
            record,
            isLocalPlayer: true,
            PositionTimestampDisposition.Apply);

        Assert.Same(controller, runtime.MovementOwner.Controller);
        Assert.Equal(record.LocalEntityId, controller.LocalEntityId);
        Assert.Equal(new Vector3(48f, 49f, 50f), controller.Position);
        Assert.Equal(
            0xA9B40000u,
            controller.CellId & 0xFFFF0000u);
        Assert.True((controller.CellId & 0xFFFFu) < 0x0100u);
        projection.BeginTeleport();
        Assert.Equal(PlayerState.PortalSpace, controller.State);

        RuntimeDestinationReadiness readiness =
            projection.PrepareDestination(
                revealGeneration: 7,
                new RuntimeTeleportDestination(
                    player,
                    InstanceSequence: 1,
                    PositionSequence: 2,
                    TeleportSequence: 1,
                    ForcePositionSequence: 0,
                    new Position(
                        0xA9B40001u,
                        new Vector3(96f, 97f, 50f),
                        Quaternion.Identity)),
                // R5/A7: a real, valid token - `default` was fine for the
                // old no-op arm but RuntimePortalPlacementAuthority.IsValid
                // now genuinely gates TryExecuteAcceptedPortalArrival on it.
                new RuntimeWorldHostProjectionToken(7, 0xA9B40001u));

        Assert.True(readiness.IsCollisionReady);
        Assert.False(readiness.IsUnhydratable);
        Assert.Equal(PlayerState.InWorld, controller.State);
        Assert.Equal(2, collision.CenterCount);
        Assert.Equal(0xA9B40001u, collision.LastCell);
        Assert.Equal(96f, controller.Position.X);
        Assert.Equal(97f, controller.Position.Y);
        Assert.Equal(50f, controller.Position.Z, 0.01f);
        Assert.Equal(0xA9B40000u, controller.CellId & 0xFFFF0000u);
        Assert.True((controller.CellId & 0xFFFFu) < 0x0100u);
    }

    [Fact]
    public void HeadlessPortalPrepareDestinationParksThenCommitsOnCollisionGenerationWake()
    {
        var operations = new FixtureSessionOperations();
        using var credential = new HeadlessCredentialSecret(
            "fixture",
            "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations);
        GameRuntime runtime = host.Runtime;
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            host.Start().Status);
        const uint player = 0x50000009u;
        const uint destinationLandblock = 0xAAB40000u;
        runtime.PlayerIdentity.ServerGuid = player;
        runtime.EntityObjects.Physics.SetPosition.BeginCollisionGeneration(
            0xA9B40000u, 1UL);
        AddFlatLandblock(runtime.EntityObjects.Physics.Engine);
        runtime.EntityObjects.Physics.SetPosition.CommitCollisionGeneration(
            0xA9B40000u, 1UL, ready: true);
        AcDream.Runtime.Session.RuntimeFirstEntryDriveController firstEntry =
            CreateFirstEntryDrive(runtime);
        RuntimeEntityRecord record = runtime.EntityObjects
            .RegisterEntityWithInitialResidence(Spawn(player), isLocalPlayer: true)
            .Canonical!;
        Assert.True(runtime.EntityObjects.ApplyAcceptedSpawn(
            record,
            record.CreateIntegrationVersion,
            record.Snapshot,
            replaceGeneration: false));
        var collision = new FixtureCollisionNeighborhood();
        RuntimeAcceptedPositionDriveController acceptedPositionDrive =
            CreateAcceptedPositionDrive(runtime);
        var projection = new HeadlessSessionWorldProjection(
            runtime,
            collision,
            firstEntry,
            acceptedPositionDrive);

        projection.ProjectSpawn(record, isLocalPlayer: true);
        PlayerMovementController controller =
            Assert.IsType<PlayerMovementController>(
                runtime.MovementOwner.Controller);
        controller.SeedPlacementForTest(
            new Vector3(48f, 49f, 50f),
            0xA9B40001u, new Vector3(48f, 49f, 50f));
        projection.ProjectPosition(
            record,
            isLocalPlayer: true,
            PositionTimestampDisposition.Apply);
        projection.BeginTeleport();

        var destination = new RuntimeTeleportDestination(
            player,
            InstanceSequence: 1,
            PositionSequence: 2,
            TeleportSequence: 1,
            ForcePositionSequence: 0,
            new Position(
                destinationLandblock | 0x0001u,
                new Vector3(10f, 10f, 50f),
                Quaternion.Identity));
        var projectionToken = new RuntimeWorldHostProjectionToken(
            7, destinationLandblock | 0x0001u);

        Assert.True(runtime.EntityObjects.TryApplyPosition(
            new WorldSession.EntityPositionUpdate(
                player,
                new CreateObject.ServerPosition(
                    destination.Position.ObjCellId,
                    destination.Position.Frame.Origin.X,
                    destination.Position.Frame.Origin.Y,
                    destination.Position.Frame.Origin.Z,
                    destination.Position.Frame.Orientation.W,
                    destination.Position.Frame.Orientation.X,
                    destination.Position.Frame.Orientation.Y,
                    destination.Position.Frame.Orientation.Z),
                Velocity: null,
                PlacementId: null,
                IsGrounded: true,
                InstanceSequence: 1,
                PositionSequence: 3,
                TeleportSequence: destination.TeleportSequence,
                ForcePositionSequence: 0),
            isLocalPlayer: true,
            forcePositionRotation: Quaternion.Identity,
            currentLocalVelocity: Vector3.Zero,
            acknowledgeProjection: null,
            out _,
            out _,
            out _));

        RuntimeDestinationReadiness parked = projection.PrepareDestination(
            revealGeneration: 7, destination, projectionToken);
        Assert.False(parked.IsCollisionReady);
        Assert.Equal(PlayerState.PortalSpace, controller.State);

        RuntimeDestinationReadiness stillParked =
            projection.PrepareDestination(
                revealGeneration: 7, destination, projectionToken);
        Assert.False(stillParked.IsCollisionReady);

        runtime.EntityObjects.Physics.SetPosition.BeginCollisionGeneration(
            destinationLandblock, 1UL);
        var heights = new byte[81];
        Array.Fill(heights, (byte)50);
        var heightTable = new float[256];
        for (int index = 0; index < heightTable.Length; index++)
            heightTable[index] = index;
        runtime.EntityObjects.Physics.Engine.AddLandblock(
            destinationLandblock,
            new TerrainSurface(heights, heightTable),
            [],
            [],
            worldOffsetX: 0f,
            worldOffsetY: 0f);
        runtime.EntityObjects.Physics.SetPosition.CommitCollisionGeneration(
            destinationLandblock, 1UL, ready: true);
        runtime.EntityObjects.Physics.ObserveLocalWorldFrame(
            destinationLandblock | 0x0001u,
            teleportAdvanced: false);
        while (runtime.EntityObjects.Physics.SetPosition.TryPeekProjection(
                out RuntimePlacementProjectionSnapshot head))
        {
            if (!runtime.EntityObjects.Physics.SetPosition
                    .AcknowledgeProjection(head.Token))
            {
                break;
            }
        }
        acceptedPositionDrive.Advance();

        RuntimeDestinationReadiness committed =
            projection.PrepareDestination(
                revealGeneration: 7, destination, projectionToken);
        Assert.True(committed.IsCollisionReady);
        Assert.Equal(PlayerState.InWorld, controller.State);
        Assert.Equal(10f, controller.Position.X);
        Assert.Equal(10f, controller.Position.Y);
        Assert.Equal(destinationLandblock, controller.CellId & 0xFFFF0000u);
    }

    [Fact]
    public void HeadlessPortalPrepareDestinationForgottenByOrdinaryMergeDoesNotLatchAsCommitted()
    {
        var operations = new FixtureSessionOperations();
        using var credential = new HeadlessCredentialSecret(
            "fixture",
            "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations);
        GameRuntime runtime = host.Runtime;
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            host.Start().Status);
        const uint player = 0x50000009u;
        const uint destinationLandblock = 0xAAB40000u;
        runtime.PlayerIdentity.ServerGuid = player;
        runtime.EntityObjects.Physics.SetPosition.BeginCollisionGeneration(
            0xA9B40000u, 1UL);
        AddFlatLandblock(runtime.EntityObjects.Physics.Engine);
        runtime.EntityObjects.Physics.SetPosition.CommitCollisionGeneration(
            0xA9B40000u, 1UL, ready: true);
        AcDream.Runtime.Session.RuntimeFirstEntryDriveController firstEntry =
            CreateFirstEntryDrive(runtime);
        RuntimeEntityRecord record = runtime.EntityObjects
            .RegisterEntityWithInitialResidence(Spawn(player), isLocalPlayer: true)
            .Canonical!;
        Assert.True(runtime.EntityObjects.ApplyAcceptedSpawn(
            record,
            record.CreateIntegrationVersion,
            record.Snapshot,
            replaceGeneration: false));
        var collision = new FixtureCollisionNeighborhood();
        RuntimeAcceptedPositionDriveController acceptedPositionDrive =
            CreateAcceptedPositionDrive(runtime);
        var projection = new HeadlessSessionWorldProjection(
            runtime,
            collision,
            firstEntry,
            acceptedPositionDrive);

        projection.ProjectSpawn(record, isLocalPlayer: true);
        PlayerMovementController controller =
            Assert.IsType<PlayerMovementController>(
                runtime.MovementOwner.Controller);
        controller.SeedPlacementForTest(
            new Vector3(48f, 49f, 50f),
            0xA9B40001u, new Vector3(48f, 49f, 50f));
        projection.ProjectPosition(
            record,
            isLocalPlayer: true,
            PositionTimestampDisposition.Apply);
        projection.BeginTeleport();

        var destination = new RuntimeTeleportDestination(
            player,
            InstanceSequence: 1,
            PositionSequence: 2,
            TeleportSequence: 1,
            ForcePositionSequence: 0,
            new Position(
                destinationLandblock | 0x0001u,
                new Vector3(10f, 10f, 50f),
                Quaternion.Identity));
        var projectionToken = new RuntimeWorldHostProjectionToken(
            7, destinationLandblock | 0x0001u);

        Assert.True(runtime.EntityObjects.TryApplyPosition(
            new WorldSession.EntityPositionUpdate(
                player,
                new CreateObject.ServerPosition(
                    destination.Position.ObjCellId,
                    destination.Position.Frame.Origin.X,
                    destination.Position.Frame.Origin.Y,
                    destination.Position.Frame.Origin.Z,
                    destination.Position.Frame.Orientation.W,
                    destination.Position.Frame.Orientation.X,
                    destination.Position.Frame.Orientation.Y,
                    destination.Position.Frame.Orientation.Z),
                Velocity: null,
                PlacementId: null,
                IsGrounded: true,
                InstanceSequence: 1,
                PositionSequence: 3,
                TeleportSequence: destination.TeleportSequence,
                ForcePositionSequence: 0),
            isLocalPlayer: true,
            forcePositionRotation: Quaternion.Identity,
            currentLocalVelocity: Vector3.Zero,
            acknowledgeProjection: null,
            out _,
            out _,
            out _));

        RuntimeDestinationReadiness parked = projection.PrepareDestination(
            revealGeneration: 7, destination, projectionToken);
        Assert.False(parked.IsCollisionReady);
        Assert.Equal(PlayerState.PortalSpace, controller.State);
        Assert.Equal(1, acceptedPositionDrive.PendingCount);

        Assert.True(runtime.EntityObjects.TryApplyPosition(
            new WorldSession.EntityPositionUpdate(
                player,
                new CreateObject.ServerPosition(
                    0x20210001u, 48f, 49f, 50f, 1f, 0f, 0f, 0f),
                Velocity: null,
                PlacementId: null,
                IsGrounded: true,
                InstanceSequence: 1,
                PositionSequence: 4,
                TeleportSequence: destination.TeleportSequence,
                ForcePositionSequence: 0),
            isLocalPlayer: true,
            forcePositionRotation: Quaternion.Identity,
            currentLocalVelocity: Vector3.Zero,
            acknowledgeProjection: null,
            out _,
            out _,
            out _));
        acceptedPositionDrive.Advance();
        Assert.Equal(0, acceptedPositionDrive.PendingCount);

        for (int i = 0; i < 10; i++)
        {
            RuntimeDestinationReadiness stillNotReady =
                projection.PrepareDestination(
                    revealGeneration: 7, destination, projectionToken);
            Assert.False(stillNotReady.IsCollisionReady);
        }

        Assert.Equal(PlayerState.PortalSpace, controller.State);
    }

    [Fact]
    public void WorldProjectionIgnoresNormalEchoButBlipsForcePosition()
    {
        var operations = new FixtureSessionOperations();
        using var credential = new HeadlessCredentialSecret(
            "fixture",
            "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations);
        GameRuntime runtime = host.Runtime;
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            host.Start().Status);
        const uint player = 0x50000003u;
        runtime.PlayerIdentity.ServerGuid = player;
        runtime.EntityObjects.Physics.SetPosition.BeginCollisionGeneration(
            0xA9B40000u, 1UL);
        AddFlatLandblock(runtime.EntityObjects.Physics.Engine);
        runtime.EntityObjects.Physics.SetPosition.CommitCollisionGeneration(
            0xA9B40000u, 1UL, ready: true);
        AcDream.Runtime.Session.RuntimeFirstEntryDriveController firstEntry =
            CreateFirstEntryDrive(runtime);
        RuntimeEntityRecord record = runtime.EntityObjects
            .RegisterEntityWithInitialResidence(Spawn(player), isLocalPlayer: true)
            .Canonical!;
        Assert.True(runtime.EntityObjects.ApplyAcceptedSpawn(
            record,
            record.CreateIntegrationVersion,
            record.Snapshot,
            replaceGeneration: false));
        var collision = new FixtureCollisionNeighborhood();
        var projection = new HeadlessSessionWorldProjection(
            runtime,
            collision,
            firstEntry);
        projection.ProjectSpawn(record, isLocalPlayer: true);
        PlayerMovementController controller =
            Assert.IsType<PlayerMovementController>(
                runtime.MovementOwner.Controller);

        controller.SeedPlacementForTest(
            new Vector3(48f, 49f, 50f),
            0xA9B40001u, new Vector3(48f, 49f, 50f));
        projection.ProjectPosition(
            record,
            isLocalPlayer: true,
            PositionTimestampDisposition.Apply);

        Assert.Equal(new Vector3(48f, 49f, 50f), controller.Position);

        var force = new WorldSession.EntityPositionUpdate(
            player,
            record.Snapshot.Position!.Value with
            {
                PositionX = 72f,
                PositionY = 73f,
            },
            Velocity: null,
            PlacementId: null,
            IsGrounded: true,
            InstanceSequence: 1,
            PositionSequence: 2,
            TeleportSequence: 0,
            ForcePositionSequence: 1);
        Assert.True(runtime.EntityObjects.TryApplyPosition(
            force,
            isLocalPlayer: true,
            forcePositionRotation: Quaternion.Identity,
            currentLocalVelocity: controller.BodyVelocity,
            acknowledgeProjection: null,
            out PositionTimestampDisposition disposition,
            out _,
            out AcceptedPhysicsTimestamps timestamps));
        Assert.Equal(
            PositionTimestampDisposition.ForcePosition,
            disposition);

        projection.CenterOnAcceptedForcePosition(record);
        RuntimeAcceptedPositionDriveController acceptedPositionDrive =
            CreateAcceptedPositionDrive(runtime);
        RuntimeAcceptedPositionExecutionStatus forceStatus =
            acceptedPositionDrive.TryExecuteAcceptedLocalPosition(
                record,
                force,
                disposition,
                timestamps,
                timestamps.PreviousTeleport);

        Assert.Equal(
            RuntimeAcceptedPositionExecutionStatus.Committed,
            forceStatus);
        Assert.Equal(new Vector3(72f, 73f, 50.005f), controller.Position);
        Assert.Equal(2, collision.CenterCount);
    }

    [Fact]
    public void FarRemoteCreateCompletesCelllessWithoutPinningItsResidence()
    {
        var operations = new FixtureSessionOperations();
        using var credential = new HeadlessCredentialSecret(
            "fixture",
            "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations);
        GameRuntime runtime = host.Runtime;
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            host.Start().Status);
        const uint player = 0x5000000Bu;
        runtime.PlayerIdentity.ServerGuid = player;
        runtime.EntityObjects.Physics.SetPosition.BeginCollisionGeneration(
            0xA9B40000u, 1UL);
        AddFlatLandblock(runtime.EntityObjects.Physics.Engine);
        runtime.EntityObjects.Physics.SetPosition.CommitCollisionGeneration(
            0xA9B40000u, 1UL, ready: true);
        AcDream.Runtime.Session.RuntimeFirstEntryDriveController firstEntry =
            CreateFirstEntryDrive(runtime);
        var collision = new FixtureCollisionNeighborhood();
        var projection = new HeadlessSessionWorldProjection(
            runtime,
            collision,
            firstEntry);
        RuntimeEntityRecord playerRecord = runtime.EntityObjects
            .RegisterEntityWithInitialResidence(Spawn(player), isLocalPlayer: true)
            .Canonical!;
        Assert.True(runtime.EntityObjects.ApplyAcceptedSpawn(
            playerRecord,
            playerRecord.CreateIntegrationVersion,
            playerRecord.Snapshot,
            replaceGeneration: false));
        projection.ProjectSpawn(playerRecord, isLocalPlayer: true);

        const uint farRemote = 0x70000010u;
        const uint farCell = 0x00010001u;
        Assert.False(collision.IsWithinServiceWindow(farCell));
        RuntimeEntityRecord remote = runtime.EntityObjects
            .RegisterEntityWithInitialResidence(
                Spawn(farRemote, farCell),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(runtime.EntityObjects.ApplyAcceptedSpawn(
            remote,
            remote.CreateIntegrationVersion,
            remote.Snapshot,
            replaceGeneration: false));

        projection.ProjectSpawn(remote, isLocalPlayer: false);

        Assert.False(runtime.EntityObjects.TryGetInitialCreateResidence(
            remote,
            out _));
        Assert.Equal(0u, remote.FullCellId);
        Assert.Equal(
            farCell,
            remote.Snapshot.Position!.Value.LandblockId);
        RuntimeEntityObjectOwnershipSnapshot ownership =
            runtime.EntityObjects.CaptureOwnership();
        Assert.Equal(0, ownership.InitialCreateResidenceLeaseCount);
        Assert.Equal(0, ownership.FirstEntryDrivePendingCount);
        Assert.Equal(0, firstEntry.PendingCount);

        while (runtime.EntityObjects.Physics.SetPosition.TryPeekProjection(
                out RuntimePlacementProjectionSnapshot head))
        {
            if (!runtime.EntityObjects.Physics.SetPosition
                    .AcknowledgeProjection(head.Token))
            {
                break;
            }
        }
        Assert.Equal(
            0,
            runtime.EntityObjects.CaptureOwnership()
                .PendingCompletionReceiptCount);
    }

    [Fact]
    public void PlacementReceiptValidationDoesNotRegainMovementOrPhysicsAuthority()
    {
        var operations = new FixtureSessionOperations();
        using var credential = new HeadlessCredentialSecret(
            "fixture",
            "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations);
        GameRuntime runtime = host.Runtime;
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            host.Start().Status);
        const uint player = 0x50000004u;
        runtime.PlayerIdentity.ServerGuid = player;
        runtime.EntityObjects.Physics.SetPosition.BeginCollisionGeneration(
            0xA9B40000u, 1UL);
        AddFlatLandblock(runtime.EntityObjects.Physics.Engine);
        runtime.EntityObjects.Physics.SetPosition.CommitCollisionGeneration(
            0xA9B40000u, 1UL, ready: true);
        AcDream.Runtime.Session.RuntimeFirstEntryDriveController firstEntry =
            CreateFirstEntryDrive(runtime);
        RuntimeEntityRecord record = runtime.EntityObjects
            .RegisterEntityWithInitialResidence(Spawn(player), isLocalPlayer: true)
            .Canonical!;
        Assert.True(runtime.EntityObjects.ApplyAcceptedSpawn(
            record,
            record.CreateIntegrationVersion,
            record.Snapshot,
            replaceGeneration: false));
        var directProjection = new HeadlessSessionWorldProjection(
            runtime,
            new FixtureCollisionNeighborhood(),
            firstEntry);
        directProjection.ProjectSpawn(record, isLocalPlayer: true);
        PlayerMovementController controller = Assert.IsType<
            PlayerMovementController>(runtime.MovementOwner.Controller);
        Vector3 positionBefore = controller.Position;
        Quaternion orientationBefore = controller.BodyOrientation;
        RuntimePhysicsOwnershipSnapshot physicsBefore =
            runtime.EntityObjects.Physics.CaptureOwnership();
        RuntimePlacementProjectionSnapshot receipt = Placement(
            runtime,
            record,
            RuntimePlacementProjectionKind.Place,
            new Vector3(600f, 601f, 602f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.2f));

        var receiptSink = new HeadlessRuntimePlacementProjectionSink(runtime);
        Assert.True(receiptSink.TryApply(in receipt));

        Assert.Equal(positionBefore, controller.Position);
        Assert.Equal(orientationBefore, controller.BodyOrientation);
        Assert.Equal(
            physicsBefore,
            runtime.EntityObjects.Physics.CaptureOwnership());
    }

    [Fact]
    public void PlacementReceiptUsesExactIncarnationAndDiscardIsAckOnly()
    {
        var operations = new FixtureSessionOperations();
        using var credential = new HeadlessCredentialSecret(
            "fixture",
            "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations);
        GameRuntime runtime = host.Runtime;
        RuntimeEntityRecord record = runtime.EntityObjects
            .RegisterEntity(Spawn(0x50000005u))
            .Canonical!;
        Assert.True(runtime.EntityObjects.ApplyAcceptedSpawn(
            record,
            record.CreateIntegrationVersion,
            record.Snapshot,
            replaceGeneration: false));
        var projection = new HeadlessRuntimePlacementProjectionSink(runtime);
        RuntimePlacementProjectionSnapshot place = Placement(
            runtime,
            record,
            RuntimePlacementProjectionKind.Place,
            Vector3.One,
            Quaternion.Identity);
        RuntimePlacementProjectionSnapshot stale = place with
        {
            Token = place.Token with
            {
                Entity = place.Token.Entity with
                {
                    Incarnation = unchecked((ushort)(
                        place.Token.Entity.Incarnation + 1)),
                },
            },
        };
        RuntimePlacementProjectionSnapshot discard = stale with
        {
            Kind = RuntimePlacementProjectionKind.Discard,
            Token = stale.Token with
            {
                SessionLifetimeVersion = ulong.MaxValue,
            },
        };

        Assert.False(projection.TryApply(in stale));
        Assert.True(projection.TryApply(in discard));
    }

    [Fact]
    public void ExecutorCompletedReceiptIsAcknowledgeOnlyRegardlessOfRecordValidity()
    {
        var operations = new FixtureSessionOperations();
        using var credential = new HeadlessCredentialSecret(
            "fixture",
            "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations);
        GameRuntime runtime = host.Runtime;
        RuntimeEntityRecord record = runtime.EntityObjects
            .RegisterEntity(Spawn(0x50000006u))
            .Canonical!;
        Assert.True(runtime.EntityObjects.ApplyAcceptedSpawn(
            record,
            record.CreateIntegrationVersion,
            record.Snapshot,
            replaceGeneration: false));
        var sink = new HeadlessRuntimePlacementProjectionSink(runtime);
        RuntimePlacementProjectionSnapshot completion = Placement(
            runtime,
            record,
            RuntimePlacementProjectionKind.ExecutorCompleted,
            Vector3.One,
            Quaternion.Identity);
        RuntimePlacementProjectionSnapshot stale = completion with
        {
            Token = completion.Token with
            {
                Entity = completion.Token.Entity with
                {
                    Incarnation = unchecked((ushort)(
                        completion.Token.Entity.Incarnation + 1)),
                },
                SessionLifetimeVersion = ulong.MaxValue,
            },
        };

        Assert.True(sink.TryApply(in completion));
        Assert.True(sink.TryApply(in stale));
    }

    [Fact]
    public void SessionEventRouteOwnsOneObserverAndUnsubscribesBeforeNetworkDetach()
    {
        var operations = new FixtureSessionOperations();
        using var credential = new HeadlessCredentialSecret(
            "fixture",
            "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations);
        GameRuntime runtime = host.Runtime;
        int subscriberCountDuringDetach = -1;
        var inner = new FixtureEventRoute(
            onDispose: () => subscriberCountDuringDetach =
                runtime.EntityObjects.Events.PlacementSubscriberCount);
        var placements = new HeadlessRuntimePlacementProjectionSink(runtime);
        var route = new HeadlessSessionEventRoute(
            inner,
            runtime,
            placements);

        Assert.Equal(
            0,
            runtime.EntityObjects.Events.PlacementSubscriberCount);
        route.Attach();
        route.Attach();
        Assert.Equal(
            1,
            runtime.EntityObjects.Events.PlacementSubscriberCount);
        Assert.Equal(1, inner.AttachCount);

        route.Dispose();
        route.Dispose();

        Assert.Equal(0, subscriberCountDuringDetach);
        Assert.Equal(
            0,
            runtime.EntityObjects.Events.PlacementSubscriberCount);
        Assert.Equal(1, inner.DisposeCount);
    }

    [Fact]
    public void ReplacementSessionEventRouteGetsOneFreshObserver()
    {
        var operations = new FixtureSessionOperations();
        using var credential = new HeadlessCredentialSecret(
            "fixture",
            "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations);
        GameRuntime runtime = host.Runtime;
        var placements = new HeadlessRuntimePlacementProjectionSink(runtime);

        var first = new HeadlessSessionEventRoute(
            new FixtureEventRoute(),
            runtime,
            placements);
        first.Attach();
        Assert.Equal(
            1,
            runtime.EntityObjects.Events.PlacementSubscriberCount);
        first.Dispose();
        Assert.Equal(
            0,
            runtime.EntityObjects.Events.PlacementSubscriberCount);

        var replacement = new HeadlessSessionEventRoute(
            new FixtureEventRoute(),
            runtime,
            placements);
        replacement.Attach();
        Assert.Equal(
            1,
            runtime.EntityObjects.Events.PlacementSubscriberCount);
        replacement.Dispose();
        Assert.Equal(
            0,
            runtime.EntityObjects.Events.PlacementSubscriberCount);
    }

    [Fact]
    public void SessionEventRouteRetryDoesNotRestorePlacementObserver()
    {
        var operations = new FixtureSessionOperations();
        using var credential = new HeadlessCredentialSecret(
            "fixture",
            "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations);
        GameRuntime runtime = host.Runtime;
        var inner = new FixtureEventRoute
        {
            DisposeFailuresRemaining = 1,
        };
        var route = new HeadlessSessionEventRoute(
            inner,
            runtime,
            new HeadlessRuntimePlacementProjectionSink(runtime));
        route.Attach();

        Assert.Throws<IOException>(route.Dispose);
        Assert.Equal(
            0,
            runtime.EntityObjects.Events.PlacementSubscriberCount);
        Assert.Equal(1, inner.DisposeCount);

        route.Dispose();

        Assert.Equal(2, inner.DisposeCount);
        Assert.Equal(
            0,
            runtime.EntityObjects.Events.PlacementSubscriberCount);
    }

    [Fact]
    public void CollisionTransactionCancelsPostAdmissionFaultWithoutWithdrawingActiveWorld()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimePhysicsState physics = lifetime.Physics;
        const uint landblockId = 0xA9B4FFFFu;
        CompleteCollisionGeneration(
            physics,
            landblockId,
            afterAdmission: null,
            (admission, prepared) =>
                physics.StageCollisionAssets(
                    admission,
                    prepared,
                    CollisionAssets(landblockId, 10f)));

        Assert.Throws<FixtureCollisionPublicationException>(() =>
            CompleteCollisionGeneration(
                physics,
                landblockId,
                _ => throw new FixtureCollisionPublicationException(),
                (_, _) => throw new InvalidOperationException(
                    "Staging must not run after the injected admission fault.")));

        Assert.Equal(10f, physics.Engine.SampleTerrainZ(1f, 1f));
        RuntimePhysicsOwnershipSnapshot ownership = physics.CaptureOwnership();
        Assert.Equal(1, ownership.LandblockCount);
        Assert.Equal(0, ownership.CollisionAdmissionCount);
    }

    [Fact]
    public void CollisionTransactionCompletesDebtFreeWithoutSyntheticYield()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimePhysicsState physics = lifetime.Physics;
        const uint landblockId = 0xA9B4FFFFu;
        HeadlessCollisionGenerationTransaction transaction =
            HeadlessCollisionGenerationTransaction.Begin(
                physics,
                landblockId,
                afterAdmission: null,
                (admission, prepared) =>
                    physics.StageCollisionAssets(
                        admission,
                        prepared,
                        CollisionAssets(landblockId, 10f)));

        HeadlessCollisionGenerationAdvance advance;
        do
        {
            advance = transaction.Advance();
            Assert.True(advance.Progressed);
            Assert.False(advance.YieldToCaller);
        }
        while (!advance.Completed);

        Assert.True(advance.Completed);
        Assert.False(advance.WaitingForProjectionAcknowledgement);
        Assert.True(transaction.EngineMutationCommitted);
        Assert.True(transaction.CompletionCommitted);
        Assert.Equal(0, physics.CaptureOwnership().CollisionAdmissionCount);
    }

    [Fact]
    public void CollisionTransactionRetainsPostEngineCancellationUntilPlaceAck()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimePhysicsState physics = lifetime.Physics;
        const uint landblockId = 0xA9B4FFFFu;
        CompleteCollisionGeneration(
            physics,
            landblockId,
            afterAdmission: null,
            (admission, prepared) =>
                physics.StageCollisionAssets(
                    admission,
                    prepared,
                    CollisionAssets(landblockId, 10f)));

        const uint guid = 0x70004201u;
        const uint cell = 0xA9B40001u;
        Vector3 position = new(10f, 10f, 0f);
        RuntimeEntityRecord record = lifetime.RegisterEntity(
            Spawn(guid)).Canonical!;
        lifetime.Entities.SetFinalPhysicsState(record, PhysicsStateFlags.Gravity);
        lifetime.Entities.SetFullCell(record, cell, landblockId);
        var body = new PhysicsBody
        {
            Position = position,
            Orientation = Quaternion.Identity,
            LastUpdateTime = 1d,
            State = PhysicsStateFlags.Gravity,
            TransientState = TransientStateFlags.Active,
        };
        body.SnapToCell(cell, position, position);
        lifetime.Entities.SetPhysicsBody(record, body);
        record.ObjectClock.Activate();
        physics.AcknowledgeSpatialProjection(record, spatial: true);
        RuntimePlacementProjectionToken seeded = SeedRuntimePlacement(
            physics,
            record,
            cell,
            position);
        Assert.True(physics.SetPosition.AcknowledgeProjection(seeded));

        HeadlessCollisionGenerationTransaction transaction =
            HeadlessCollisionGenerationTransaction.Begin(
                physics,
                landblockId,
                afterAdmission: null,
                (admission, prepared) =>
                    physics.StageCollisionAssets(
                        admission,
                        prepared,
                        CollisionAssets(landblockId, 20f)));
        HeadlessCollisionGenerationAdvance advance;
        do
        {
            advance = transaction.Advance();
        }
        while (!advance.WaitingForProjectionAcknowledgement);
        Assert.True(advance.YieldToCaller);
        Assert.False(advance.Completed);
        Assert.False(transaction.EngineMutationCommitted);
        Assert.True(physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot withdrawal));
        Assert.Equal(RuntimePlacementProjectionKind.Withdraw, withdrawal.Kind);
        Assert.Equal(record.Key, withdrawal.Token.Entity);
        Assert.Equal(cell, withdrawal.Token.ExactCellId);
        Assert.True(physics.SetPosition.AcknowledgeProjection(withdrawal.Token));

        do
        {
            advance = transaction.Advance();
        }
        while (!advance.WaitingForProjectionAcknowledgement);
        Assert.True(advance.YieldToCaller);
        Assert.False(advance.Completed);
        Assert.True(transaction.EngineMutationCommitted);
        Assert.True(physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot placement));
        Assert.Equal(RuntimePlacementProjectionKind.Place, placement.Kind);
        Assert.Equal(withdrawal.Token.Entity, placement.Token.Entity);
        Assert.True(placement.Token.Sequence > withdrawal.Token.Sequence);
        Assert.NotEqual(withdrawal.Token, placement.Token);
        Assert.Equal(withdrawal.Token.ExactCellId, placement.Token.ExactCellId);

        Assert.False(transaction.TryCancel());
        Assert.True(physics.SetPosition.AcknowledgeProjection(placement.Token));
        Assert.True(transaction.TryCancel());
        RuntimePhysicsOwnershipSnapshot ownership = physics.CaptureOwnership();
        Assert.Equal(0, ownership.CollisionPrefixMutationCount);
        Assert.Equal(0, ownership.CollisionAdmissionCount);
    }

    [Fact]
    public void CollisionTransactionCancelsStagingFaultWithoutWithdrawingActiveWorld()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimePhysicsState physics = lifetime.Physics;
        const uint landblockId = 0xA9B4FFFFu;
        CompleteCollisionGeneration(
            physics,
            landblockId,
            afterAdmission: null,
            (admission, prepared) =>
                physics.StageCollisionAssets(
                    admission,
                    prepared,
                    CollisionAssets(landblockId, 10f)));

        Assert.Throws<FixtureCollisionPublicationException>(() =>
            CompleteCollisionGeneration(
                physics,
                landblockId,
                afterAdmission: null,
                (admission, prepared) =>
                {
                    physics.StageCollisionAssets(
                        admission,
                        prepared,
                        CollisionAssets(landblockId, 25f));
                    throw new FixtureCollisionPublicationException();
                }));

        Assert.Equal(10f, physics.Engine.SampleTerrainZ(1f, 1f));
        RuntimePhysicsOwnershipSnapshot ownership = physics.CaptureOwnership();
        Assert.Equal(1, ownership.LandblockCount);
        Assert.Equal(0, ownership.CollisionAdmissionCount);
    }

    [Fact]
    public void RealAdmissionNeverDrivesTheConductorWhileOpenAndHydratesOnceReleased()
    {
        var operations = new FixtureSessionOperations();
        using var credential = new HeadlessCredentialSecret(
            "fixture",
            "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations);
        GameRuntime runtime = host.Runtime;
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            host.Start().Status);
        const uint player = 0x50000012u;
        const uint neighborLandblockId = 0xAAB4FFFFu;
        runtime.PlayerIdentity.ServerGuid = player;
        runtime.EntityObjects.Physics.SetPosition.BeginCollisionGeneration(
            0xA9B40000u, 1UL);
        AddFlatLandblock(runtime.EntityObjects.Physics.Engine);
        runtime.EntityObjects.Physics.SetPosition.CommitCollisionGeneration(
            0xA9B40000u, 1UL, ready: true);
        AcDream.Runtime.Session.RuntimeFirstEntryDriveController firstEntry =
            CreateFirstEntryDrive(runtime);
        RuntimeEntityRecord record = runtime.EntityObjects
            .RegisterEntityWithInitialResidence(
                Spawn(player),
                isLocalPlayer: true)
            .Canonical!;
        Assert.True(runtime.EntityObjects.ApplyAcceptedSpawn(
            record,
            record.CreateIntegrationVersion,
            record.Snapshot,
            replaceGeneration: false));

        var collision = new NeighborAdmissionHeldOpenCollisionNeighborhood(
            runtime.EntityObjects.Physics,
            neighborLandblockId);
        collision.OpenHeldAdmission();
        var projection = new HeadlessSessionWorldProjection(
            runtime,
            collision,
            firstEntry);

        projection.ProjectSpawn(record, isLocalPlayer: true);
        Assert.False(collision.IsQuiescent);
        Assert.Null(runtime.MovementOwner.Controller);

        for (int tick = 0; tick < 50; tick++)
        {
            projection.PumpFirstEntry();
            Assert.False(collision.IsQuiescent);
            Assert.Null(runtime.MovementOwner.Controller);
        }

        collision.ReleaseHeldAdmission();
        Assert.True(collision.IsQuiescent);
        const int boundedTicks = 200;
        bool published = false;
        for (int tick = 0; tick < boundedTicks; tick++)
        {
            projection.PumpFirstEntry();
            if (runtime.MovementOwner.Controller is { IsRuntimePublished: true })
            {
                published = true;
                break;
            }
        }

        Assert.True(
            published,
            "the local player never reached RuntimePublished within the "
            + "bounded tick budget after the unrelated admission cleared.");
        PlayerMovementController controller = Assert.IsType<
            PlayerMovementController>(runtime.MovementOwner.Controller);
        Assert.True(controller.IsRuntimePublished);
        Assert.Equal(0, firstEntry.PendingCount);
    }

    /// <summary>
    /// A host that plays no animations still has to finish the motions it
    /// dispatches, otherwise a motion stays outstanding forever and suspends
    /// the whole move-to layer - a turn-to-heading is accepted and then never
    /// starts, so a route that turns before it walks stands still. See the
    /// research note on the headless navigation slice.
    /// </summary>
    [Fact]
    public void PublishedHeadlessPlayer_TurnToHeadingReachesTheRequestedHeading()
    {
        var operations = new FixtureSessionOperations();
        using var credential = new HeadlessCredentialSecret(
            "fixture",
            "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations);
        GameRuntime runtime = host.Runtime;
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            host.Start().Status);

        PlayerMovementController controller = PublishFlatGroundPlayer(
            host,
            runtime,
            0x50000015u);
        Assert.Equal(0f, Heading(controller), 1);

        // Driven by hand because the fixture session has no socket to send the
        // movement event on.
        var frame = new RuntimeLocalPlayerFrameController(
            new HeadlessLocalPlayerFrameHost(
                runtime,
                CreateInertLiveSessionHost()),
            new HeadlessMovementInputSource(runtime.MovementOwner));

        Assert.True(host.Commands.Movement.TurnToHeading(
            runtime.Generation,
            90f).Accepted);
        for (int tick = 0; tick < 120 && Heading(controller) < 89.5f; tick++)
        {
            frame.AdvanceBeforeNetwork(0.05f);
        }

        Assert.Equal(90f, Heading(controller), 1);
        Assert.False(
            controller.Motion.MotionsPending(),
            "the turn finished but a dispatched motion is still outstanding, "
                + "so the next move-to operation would never start.");

        static float Heading(PlayerMovementController controller) =>
            AcDream.Core.Physics.Motion.MoveToMath.GetHeading(
                controller.CurrentCellPosition.Frame.Orientation);
    }

    private static PlayerMovementController PublishFlatGroundPlayer(
        HeadlessSessionHost host,
        GameRuntime runtime,
        uint player)
    {
        runtime.PlayerIdentity.ServerGuid = player;
        runtime.EntityObjects.Physics.SetPosition.BeginCollisionGeneration(
            0xA9B40000u,
            1UL);
        AddFlatLandblock(runtime.EntityObjects.Physics.Engine);
        runtime.EntityObjects.Physics.SetPosition.CommitCollisionGeneration(
            0xA9B40000u,
            1UL,
            ready: true);
        RuntimeFirstEntryDriveController firstEntry =
            CreateFirstEntryDrive(runtime);
        RuntimeEntityRecord record = runtime.EntityObjects
            .RegisterEntityWithInitialResidence(
                Spawn(player),
                isLocalPlayer: true)
            .Canonical!;
        Assert.True(runtime.EntityObjects.ApplyAcceptedSpawn(
            record,
            record.CreateIntegrationVersion,
            record.Snapshot,
            replaceGeneration: false));

        var projection = new HeadlessSessionWorldProjection(
            runtime,
            new GateControllableCollisionNeighborhood
            {
                QuiescentOverride = true,
            },
            firstEntry);
        projection.ProjectSpawn(record, isLocalPlayer: true);
        for (int tick = 0;
             tick < 200
             && runtime.MovementOwner.Controller is not
                 { IsRuntimePublished: true };
             tick++)
        {
            projection.PumpFirstEntry();
        }

        return Assert.IsType<PlayerMovementController>(
            runtime.MovementOwner.Controller);
    }

    [Fact]
    public void PublishedHeadlessPlayer_ChargedCommandJumpBecomesAirborne()
    {
        var operations = new FixtureSessionOperations();
        using var credential = new HeadlessCredentialSecret(
            "fixture",
            "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations);
        GameRuntime runtime = host.Runtime;
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            host.Start().Status);

        const uint player = 0x50000014u;
        runtime.PlayerIdentity.ServerGuid = player;
        runtime.EntityObjects.Physics.SetPosition.BeginCollisionGeneration(
            0xA9B40000u,
            1UL);
        AddFlatLandblock(runtime.EntityObjects.Physics.Engine);
        runtime.EntityObjects.Physics.SetPosition.CommitCollisionGeneration(
            0xA9B40000u,
            1UL,
            ready: true);
        AcDream.Runtime.Session.RuntimeFirstEntryDriveController firstEntry =
            CreateFirstEntryDrive(runtime);
        RuntimeEntityRecord record = runtime.EntityObjects
            .RegisterEntityWithInitialResidence(
                Spawn(player),
                isLocalPlayer: true)
            .Canonical!;
        Assert.True(runtime.EntityObjects.ApplyAcceptedSpawn(
            record,
            record.CreateIntegrationVersion,
            record.Snapshot,
            replaceGeneration: false));

        var projection = new HeadlessSessionWorldProjection(
            runtime,
            new GateControllableCollisionNeighborhood
            {
                QuiescentOverride = true,
            },
            firstEntry);
        projection.ProjectSpawn(record, isLocalPlayer: true);
        for (int tick = 0;
             tick < 200
             && runtime.MovementOwner.Controller is not
                 { IsRuntimePublished: true };
             tick++)
        {
            projection.PumpFirstEntry();
        }

        PlayerMovementController controller = Assert.IsType<
            PlayerMovementController>(runtime.MovementOwner.Controller);
        Assert.True(controller.IsRuntimePublished);
        Assert.False(runtime.MovementOwner.Snapshot.IsAirborne);
        var frame = new RuntimeLocalPlayerFrameController(
            new HeadlessLocalPlayerFrameHost(
                runtime,
                CreateInertLiveSessionHost()),
            new HeadlessMovementInputSource(runtime.MovementOwner));

        Assert.True(host.Commands.Movement.SetIntent(
            runtime.Generation,
            new MovementInput(Jump: true)).Accepted);
        for (int tick = 0; tick < 12; tick++)
            frame.AdvanceBeforeNetwork(0.05f);

        Assert.True(runtime.MovementOwner.JumpCharge.IsCharging);
        Assert.True(host.Commands.Movement.SetIntent(
            runtime.Generation,
            new MovementInput(Jump: false)).Accepted);
        frame.AdvanceBeforeNetwork(0.016f);

        RuntimeMovementSnapshot movement = runtime.MovementOwner.Snapshot;
        Assert.True(movement.IsAirborne);
        Assert.True(movement.Velocity.Z > 0f);
    }

    [Fact]
    public void PumpFirstEntryWithholdsDriveAllUntilQuiescentThenDrivesImmediately()
    {
        var operations = new FixtureSessionOperations();
        using var credential = new HeadlessCredentialSecret(
            "fixture",
            "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations);
        GameRuntime runtime = host.Runtime;
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            host.Start().Status);
        const uint player = 0x50000013u;
        runtime.PlayerIdentity.ServerGuid = player;
        runtime.EntityObjects.Physics.SetPosition.BeginCollisionGeneration(
            0xA9B40000u, 1UL);
        AddFlatLandblock(runtime.EntityObjects.Physics.Engine);
        runtime.EntityObjects.Physics.SetPosition.CommitCollisionGeneration(
            0xA9B40000u, 1UL, ready: true);
        AcDream.Runtime.Session.RuntimeFirstEntryDriveController firstEntry =
            CreateFirstEntryDrive(runtime);
        RuntimeEntityRecord record = runtime.EntityObjects
            .RegisterEntityWithInitialResidence(Spawn(player), isLocalPlayer: true)
            .Canonical!;
        Assert.True(runtime.EntityObjects.ApplyAcceptedSpawn(
            record,
            record.CreateIntegrationVersion,
            record.Snapshot,
            replaceGeneration: false));

        var collision = new GateControllableCollisionNeighborhood
        {
            QuiescentOverride = false,
        };
        var projection = new HeadlessSessionWorldProjection(
            runtime,
            collision,
            firstEntry);

        projection.ProjectSpawn(record, isLocalPlayer: true);
        Assert.Null(runtime.MovementOwner.Controller);
        Assert.Equal(1, firstEntry.PendingCount);

        projection.PumpFirstEntry();
        Assert.Null(runtime.MovementOwner.Controller);
        Assert.Equal(1, firstEntry.PendingCount);

        collision.QuiescentOverride = true;
        projection.PumpFirstEntry();

        Assert.NotNull(runtime.MovementOwner.Controller);
    }

    /// <summary>
    /// The spawn that opens the local player's initial-Create placement is the
    /// same spawn that asks the neighbourhood to publish collision for the
    /// landblock that placement targets. A publication takes quiescence over
    /// that prefix by cancelling every placement still waiting for its mover,
    /// and the residence that owns such a placement cannot come back from the
    /// cancellation - so the conductor has to reach its preparation before the
    /// publication runs. This pins that a spawn followed by a real publication
    /// still ends with a published local player.
    /// </summary>
    [Fact]
    public void ASpawnFollowedByItsOwnLandblockPublicationStillHydratesThePlayer()
    {
        var operations = new FixtureSessionOperations();
        using var credential = new HeadlessCredentialSecret(
            "fixture",
            "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations);
        GameRuntime runtime = host.Runtime;
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            host.Start().Status);
        const uint player = 0x50000014u;
        runtime.PlayerIdentity.ServerGuid = player;
        AcDream.Runtime.Session.RuntimeFirstEntryDriveController firstEntry =
            CreateFirstEntryDrive(runtime);
        RuntimeEntityRecord record = runtime.EntityObjects
            .RegisterEntityWithInitialResidence(Spawn(player), isLocalPlayer: true)
            .Canonical!;
        Assert.True(runtime.EntityObjects.ApplyAcceptedSpawn(
            record,
            record.CreateIntegrationVersion,
            record.Snapshot,
            replaceGeneration: false));

        var collision = new PublishOnCenterCollisionNeighborhood(
            runtime.EntityObjects.Physics);
        var projection = new HeadlessSessionWorldProjection(
            runtime,
            collision,
            firstEntry);

        projection.ProjectSpawn(record, isLocalPlayer: true);
        Assert.Equal(1, collision.PublicationCount);

        bool published = false;
        for (int tick = 0; tick < 200 && !published; tick++)
        {
            projection.PumpFirstEntry();
            published = runtime.MovementOwner.Controller
                is { IsRuntimePublished: true };
        }

        Assert.True(
            published,
            "the local player never hydrated after its own landblock "
                + "published: residence="
                + runtime.EntityObjects.TryGetInitialCreateResidence(
                    record,
                    out _)
                + $" body={record.PhysicsBody is not null}"
                + $" pending={firstEntry.PendingCount}");
    }

    private sealed class PublishOnCenterCollisionNeighborhood(
        RuntimePhysicsState physics) : IHeadlessCollisionNeighborhood
    {
        private const uint PublishedLandblockId = 0xA9B4FFFFu;

        internal int PublicationCount { get; private set; }

        public void CenterOn(uint fullCellId)
        {
            PublicationCount++;
            CompleteCollisionGeneration(
                physics,
                PublishedLandblockId,
                afterAdmission: null,
                (admission, prepared) => physics.StageCollisionAssets(
                    admission,
                    prepared,
                    CollisionAssets(PublishedLandblockId, 50f)));
        }

        public bool IsReady(uint fullCellId) => PublicationCount > 0;

        public bool IsWithinServiceWindow(uint fullCellId) => true;

        public bool IsCollisionPublished(uint fullCellId) => true;

        public bool IsQuiescent => true;

        public void Follow(uint fullCellId)
        {
        }
    }

    [Fact]
    public void ProjectSpawnCommittingCollisionGenerationDoesNotCancelTheLocalPlayersOwnFreshPlacement()
    {
        var operations = new FixtureSessionOperations();
        using var credential = new HeadlessCredentialSecret(
            "fixture",
            "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations);
        GameRuntime runtime = host.Runtime;
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            host.Start().Status);
        const uint player = 0x50000014u;
        runtime.PlayerIdentity.ServerGuid = player;
        AcDream.Runtime.Session.RuntimeFirstEntryDriveController firstEntry =
            CreateFirstEntryDrive(runtime);
        RuntimeEntityRecord record = runtime.EntityObjects
            .RegisterEntityWithInitialResidence(Spawn(player), isLocalPlayer: true)
            .Canonical!;
        Assert.True(runtime.EntityObjects.ApplyAcceptedSpawn(
            record,
            record.CreateIntegrationVersion,
            record.Snapshot,
            replaceGeneration: false));

        var collision = new CollisionGenerationCommittingNeighborhood(runtime);
        var projection = new HeadlessSessionWorldProjection(
            runtime,
            collision,
            firstEntry);

        // CenterOn commits the destination landblock's collision generation, the
        // same as production; that commit races the local player's own just-begun
        // placement while it is still unprepared.
        projection.ProjectSpawn(record, isLocalPlayer: true);

        Assert.NotNull(runtime.MovementOwner.Controller);
    }

    [Fact]
    public void ProjectPositionCommittingCollisionGenerationDoesNotCancelTheLocalPlayersOwnFreshPlacement()
    {
        var operations = new FixtureSessionOperations();
        using var credential = new HeadlessCredentialSecret(
            "fixture",
            "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations);
        GameRuntime runtime = host.Runtime;
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            host.Start().Status);
        const uint player = 0x50000015u;
        runtime.PlayerIdentity.ServerGuid = player;
        AcDream.Runtime.Session.RuntimeFirstEntryDriveController firstEntry =
            CreateFirstEntryDrive(runtime);
        RuntimeEntityRecord record = runtime.EntityObjects
            .RegisterEntityWithInitialResidence(Spawn(player), isLocalPlayer: true)
            .Canonical!;
        Assert.True(runtime.EntityObjects.ApplyAcceptedSpawn(
            record,
            record.CreateIntegrationVersion,
            record.Snapshot,
            replaceGeneration: false));

        var collision = new CollisionGenerationCommittingNeighborhood(runtime);
        var projection = new HeadlessSessionWorldProjection(
            runtime,
            collision,
            firstEntry);

        // ProjectPosition's own CenterOn call races the same still-unprepared
        // placement while the movement controller has not yet been created.
        projection.ProjectPosition(
            record,
            isLocalPlayer: true,
            PositionTimestampDisposition.Apply);

        Assert.NotNull(runtime.MovementOwner.Controller);
    }

    internal sealed class CollisionGenerationCommittingNeighborhood(
        GameRuntime runtime) : IHeadlessCollisionNeighborhood
    {
        public void CenterOn(uint fullCellId) =>
            CommitSyntheticCollisionGeneration(
                runtime,
                (fullCellId & 0xFFFF0000u) | 0xFFFFu);

        public bool IsReady(uint fullCellId) => true;

        public bool IsWithinServiceWindow(uint fullCellId) => true;

        public bool IsCollisionPublished(uint fullCellId) => true;

        public bool IsQuiescent => true;

        public void Follow(uint fullCellId)
        {
        }
    }

    internal static void CommitSyntheticCollisionGeneration(
        GameRuntime runtime,
        uint landblockId)
    {
        HeadlessCollisionGenerationTransaction transaction =
            HeadlessCollisionGenerationTransaction.Begin(
                runtime.EntityObjects.Physics,
                landblockId,
                afterAdmission: null,
                (admission, prepared) =>
                {
                    prepared.SetAssetClosure([], []);
                    runtime.EntityObjects.Physics.StageCollisionAssets(
                        admission,
                        prepared,
                        new RuntimeLandblockCollisionAssets(
                            landblockId,
                            new TerrainSurface(new byte[81], new float[256]),
                            [],
                            [],
                            WorldOffsetX: 0f,
                            WorldOffsetY: 0f,
                            CurrentCellId: landblockId));
                });
        HeadlessCollisionGenerationAdvance advance;
        do
        {
            advance = transaction.Advance();
        } while (!advance.Completed && advance.Progressed);
        Assert.True(advance.Completed);
    }

    [Fact]
    public void CanAdvancePlayerReflectsControllerPublicationLifecycle()
    {
        var operations = new FixtureSessionOperations();
        using var credential = new HeadlessCredentialSecret(
            "fixture",
            "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations);
        GameRuntime runtime = host.Runtime;
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            host.Start().Status);
        Assert.True(runtime.Session.IsInWorld);

        var inertSession = CreateInertLiveSessionHost();
        var frameHost = new HeadlessLocalPlayerFrameHost(runtime, inertSession);

        PlayerMovementController candidate =
            PlayerMovementController.CreatePublicationCandidate(
                new PhysicsEngine(),
                PlayerMovementConstructionOptions.Fallback);
        candidate.SealPublicationCandidate();
        candidate.CommitRuntimeOwnership(new RetailObjectQuantumClock());
        runtime.MovementOwner.Controller = candidate;
        Assert.True(candidate.IsRuntimeOwnedDormant);
        Assert.False(frameHost.CanAdvancePlayer);

        candidate.ActivateRuntimePublication();
        Assert.True(candidate.IsRuntimePublished);
        Assert.True(frameHost.CanAdvancePlayer);

        candidate.RetireRuntimePublication();
        Assert.False(frameHost.CanAdvancePlayer);
    }

    [Fact]
    public void CommandMovementFrameIsMarkedPersistentWithoutMutatingOwner()
    {
        using var movement = new RuntimeLocalPlayerMovementState();
        var command = new MovementInput(TurnLeft: true);
        movement.SetCommandInput(command);
        var source = new HeadlessMovementInputSource(movement);

        MovementInput captured = source.Capture();

        Assert.True(captured.TurnLeft);
        Assert.True(captured.IsPersistentCommand);
        Assert.Equal(command, movement.CommandInput);
    }

    internal static LiveSessionHost CreateInertLiveSessionHost()
    {
        var controller = new LiveSessionController(
            new ThrowingLiveSessionOperations());
        return new LiveSessionHost(
            controller,
            new LiveSessionHostBindings(
                new LiveSessionRoutingFactories(
                    _ => throw new NotSupportedException(),
                    _ => throw new NotSupportedException()),
                _ => { },
                new LiveSessionSelectionBindings(
                    _ => { },
                    _ => { },
                    _ => { },
                    _ => { },
                    _ => { },
                    () => { }),
                new LiveSessionEnteredWorldBindings(
                    _ => { },
                    () => { },
                    () => { },
                    _ => { },
                    () => { }),
                (_, _, _) => { },
                () => { },
                _ => { },
                _ => { }));
    }

    private sealed class ThrowingLiveSessionOperations : ILiveSessionOperations
    {
        public IPEndPoint ResolveEndpoint(string host, int port) =>
            throw new NotSupportedException();
        public WorldSession CreateSession(IPEndPoint endpoint) =>
            throw new NotSupportedException();
        public void Connect(
            WorldSession session,
            string user,
            string password) =>
            throw new NotSupportedException();
        public CharacterList.Parsed? GetCharacters(WorldSession session) =>
            throw new NotSupportedException();
        public void EnterWorld(
            WorldSession session,
            int activeCharacterIndex) =>
            throw new NotSupportedException();
        public void Tick(WorldSession session) =>
            throw new NotSupportedException();
        public void DisposeSession(WorldSession session) =>
            throw new NotSupportedException();
    }

    internal static HeadlessSessionDescriptor Descriptor(
        HeadlessCredentialProviderKind provider =
            HeadlessCredentialProviderKind.Environment,
        string credentialReference = "BOT_PASSWORD",
        Dictionary<string, bool>? characterOptions = null,
        string? statusFile = null,
        IReadOnlyList<string>? loginCommands = null,
        int loginCommandDelayMs = 500) => new()
    {
        Id = "bot",
        Endpoint = new HeadlessEndpointDescriptor
        {
            Host = "127.0.0.1",
            Port = 9000,
        },
        Account = "account",
        Character = new HeadlessCharacterSelector
        {
            Name = "headless",
        },
        Policy = new HeadlessBotPolicyDescriptor
        {
            Id = "idle",
        },
        Credential = new HeadlessCredentialReference
        {
            Provider = provider,
            Reference = credentialReference,
        },
        CharacterOptions = characterOptions,
        StatusFile = statusFile,
        LoginCommands = loginCommands is null ? null : [.. loginCommands],
        LoginCommandDelayMs = loginCommandDelayMs,
    };

    private static HeadlessSessionDescriptor ProbeDescriptor(
        string id = "probe-bot",
        HeadlessCredentialProviderKind provider =
            HeadlessCredentialProviderKind.Environment,
        string credentialReference = "PROBE_PASSWORD",
        string? statusFile = null) => new()
    {
        Id = id,
        Endpoint = new HeadlessEndpointDescriptor
        {
            Host = "127.0.0.1",
            Port = 9000,
        },
        Account = "account",
        Mode = HeadlessSessionMode.Probe,
        Credential = new HeadlessCredentialReference
        {
            Provider = provider,
            Reference = credentialReference,
        },
        StatusFile = statusFile,
    };

    private static string[] ReadStatusEventNames(string path) =>
        LiveStatusFile.ReadAllLines(path)
            .Select(static line =>
            {
                using JsonDocument document = JsonDocument.Parse(line);
                return document.RootElement.GetProperty("e").GetString()!;
            })
            .ToArray();

    // A default HeadlessPathOverrides() resolves to the real machine data
    // directory, whose plugins/ folder a developer may have populated for
    // manual testing. A test that asserts the EXACT status-event shape must
    // not let a real installed plugin add an event the fixture host never
    // produces, so it resolves paths against a private, empty temp
    // directory instead.
    private static string CreateIsolatedDataDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"acdream-headless-data-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteIsolatedDataDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private static void HydrateGroundedPlayer(GameRuntime runtime)
    {
        const uint player = 0x50000002u;
        PhysicsEngine engine = runtime.EntityObjects.Physics.Engine;
        AddFlatLandblock(engine);

        RuntimeEntityRecord record = runtime.EntityObjects
            .RegisterEntity(Spawn(player))
            .Canonical!;
        Assert.True(runtime.EntityObjects.ApplyAcceptedSpawn(
            record,
            record.CreateIntegrationVersion,
            record.Snapshot,
            replaceGeneration: false));
        var controller = new PlayerMovementController(engine);
        controller.SeedPlacementForTest(
            new Vector3(96f, 97f, 50f),
            0xA9B40001u,
            new Vector3(96f, 97f, 50f));
        runtime.MovementOwner.Controller = controller;
    }

    private static void CompleteCollisionGeneration(
        RuntimePhysicsState physics,
        uint landblockId,
        Action<RuntimeCollisionAdmission>? afterAdmission,
        Action<RuntimeCollisionAdmission,
            PreparedLandblockCollisionGeneration> stage)
    {
        HeadlessCollisionGenerationTransaction transaction =
            HeadlessCollisionGenerationTransaction.Begin(
                physics,
                landblockId,
                afterAdmission,
                stage);
        while (true)
        {
            HeadlessCollisionGenerationAdvance advance = transaction.Advance();
            if (advance.Completed)
                return;
            Assert.True(advance.Progressed);
            Assert.False(advance.WaitingForProjectionAcknowledgement);
        }
    }

    private static RuntimePlacementProjectionToken SeedRuntimePlacement(
        RuntimePhysicsState physics,
        RuntimeEntityRecord record,
        uint cell,
        Vector3 position)
    {
        Type coreMarker = typeof(PhysicsEngine);
        Type requestType = coreMarker.Assembly.GetType(
            "AcDream.Core.Physics.PhysicsSetPositionRequest",
            throwOnError: true)!;
        Type flagsType = coreMarker.Assembly.GetType(
            "AcDream.Core.Physics.PhysicsSetPositionFlags",
            throwOnError: true)!;
        Type placementClassType = coreMarker.Assembly.GetType(
            "AcDream.Core.Physics.PhysicsPlacementClass",
            throwOnError: true)!;
        object request = Activator.CreateInstance(
            requestType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args:
            [
                position,
                Quaternion.Identity,
                cell,
                position,
                ImmutableArray<FlatCollisionSphere>.Empty,
                1f,
                0.4f,
                0.4f,
                PhysicsStateFlags.None,
                ObjectInfoState.None,
                0u,
                Enum.ToObject(placementClassType, 0),
                Enum.ToObject(flagsType, 0x011u),
                Vector3.Zero,
                0f,
                0f,
                0u,
                cell,
            ],
            culture: null)!;
        Type runtimeMarker = typeof(RuntimePhysicsState);
        Type commandType = runtimeMarker.Assembly.GetType(
            "AcDream.Runtime.Physics.RuntimeSetPositionCommand",
            throwOnError: true)!;
        Type kindType = runtimeMarker.Assembly.GetType(
            "AcDream.Runtime.Physics.RuntimeSetPositionOperationKind",
            throwOnError: true)!;
        object command = Activator.CreateInstance(
            commandType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args:
            [
                request,
                Enum.ToObject(kindType, 2),
                10d,
                0UL,
                0f,
                0f,
                default(RuntimePortalPlacementAuthority),
            ],
            culture: null)!;
        MethodInfo apply = physics.SetPosition.GetType().GetMethod(
            "Apply",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("Runtime SetPosition.Apply");
        object outcome = apply.Invoke(
            physics.SetPosition,
            [record, record.PositionAuthorityVersion, command])!;
        return (RuntimePlacementProjectionToken)(outcome.GetType().GetProperty(
            "Projection",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(outcome)
            ?? throw new MissingMemberException("Runtime placement projection"));
    }

    private static RuntimeLandblockCollisionAssets CollisionAssets(
        uint landblockId,
        float terrainHeight)
    {
        var heights = new byte[81];
        var table = new float[256];
        table[0] = terrainHeight;
        return new RuntimeLandblockCollisionAssets(
            landblockId,
            new TerrainSurface(heights, table),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            0f,
            0f,
            0u);
    }

    private sealed class FixtureCollisionPublicationException : Exception;

    [Fact]
    public void MissingPreparedCollisionYieldsTypedRetryAndCompletesWhenAvailable()
    {
        var operations = new FixtureSessionOperations();
        using var credential = new HeadlessCredentialSecret(
            "fixture",
            "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations);
        GameRuntime runtime = host.Runtime;
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            host.Start().Status);
        const uint player = 0x50000021u;
        runtime.PlayerIdentity.ServerGuid = player;
        runtime.EntityObjects.Physics.SetPosition.BeginCollisionGeneration(
            0xA9B40000u, 1UL);
        AddFlatLandblock(runtime.EntityObjects.Physics.Engine);
        runtime.EntityObjects.Physics.SetPosition.CommitCollisionGeneration(
            0xA9B40000u, 1UL, ready: true);
        var source = new FlakySetupCollisionSource();
        var firstEntry = new AcDream.Runtime.Session
            .RuntimeFirstEntryDriveController(
                runtime.EntityObjects,
                runtime.Clock,
                source,
                () => PlayerMovementConstructionOptions.From(
                    runtime.CharacterOwner.MovementSkills.Snapshot),
                static _ => new RuntimeLocalPlayerPhysicsActivationPreparation(
                    Radius: 0.48f,
                    Height: 1.835f,
                    RuntimeLocalPlayerShadowDisposition.ProvenShapeless));
        RuntimeEntityRecord record = runtime.EntityObjects
            .RegisterEntityWithInitialResidence(Spawn(player), isLocalPlayer: true)
            .Canonical!;
        Assert.True(runtime.EntityObjects.ApplyAcceptedSpawn(
            record,
            record.CreateIntegrationVersion,
            record.Snapshot,
            replaceGeneration: false));
        var collision = new FixtureCollisionNeighborhood();
        var projection = new HeadlessSessionWorldProjection(
            runtime,
            collision,
            firstEntry);

        projection.ProjectSpawn(record, isLocalPlayer: true);

        Assert.True(source.SetupReadAttempts >= 1);
        Assert.Null(runtime.MovementOwner.Controller);
        Assert.Equal(1, firstEntry.PendingCount);
        Assert.Equal(0u, record.FullCellId);

        source.Available = true;
        // The session tick's retry pump.
        firstEntry.DriveAll();

        Assert.IsType<PlayerMovementController>(
            runtime.MovementOwner.Controller);
        Assert.Equal(0, firstEntry.PendingCount);
        Assert.Equal(0xA9B40000u, record.FullCellId & 0xFFFF0000u);
        Assert.NotEqual(0u, record.FullCellId);
    }

    private sealed class FlakySetupCollisionSource
        : AcDream.Content.IPreparedCollisionSource
    {
        internal bool Available { get; set; }
        internal int SetupReadAttempts { get; private set; }

        public AcDream.Content.PreparedAssetPresence ProbeCollision(
            AcDream.Content.Pak.PakAssetType type,
            uint sourceFileId) =>
            AcDream.Content.PreparedAssetPresence.Available;

        public AcDream.Content.PreparedCollisionReadResult<FlatSetupCollision>
            ReadSetupCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default)
        {
            SetupReadAttempts++;
            if (!Available)
            {
                return AcDream.Content.PreparedCollisionReadResult<
                    FlatSetupCollision>.Missing;
            }
            return AcDream.Content.PreparedCollisionReadResult<
                FlatSetupCollision>.Loaded(new FlatSetupCollision(
                    System.Collections.Immutable.ImmutableArray<
                        FlatCollisionCylinder>.Empty,
                    [new FlatCollisionSphere(Vector3.Zero, 0.48f)],
                    height: 0f,
                    radius: 0f,
                    stepUpHeight: 0.4f,
                    stepDownHeight: 0.4f));
        }

        public AcDream.Content.PreparedCollisionReadResult<
            FlatGfxObjCollisionAsset> ReadGfxObjCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public AcDream.Content.PreparedCollisionReadResult<
            FlatCellStructureCollisionAsset> ReadCellStructureCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public AcDream.Content.PreparedCollisionReadResult<FlatEnvCellTopology>
            ReadEnvCellTopology(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public AcDream.Content.PreparedCollisionSourceStats CollisionStats =>
            default;

        public void Dispose()
        {
        }
    }

    internal static AcDream.Runtime.Session.RuntimeFirstEntryDriveController
        CreateFirstEntryDrive(GameRuntime runtime) => new(
            runtime.EntityObjects,
            runtime.Clock,
            new LoadedSetupCollisionSource(),
            () => PlayerMovementConstructionOptions.From(
                runtime.CharacterOwner.MovementSkills.Snapshot),
            static _ => new RuntimeLocalPlayerPhysicsActivationPreparation(
                Radius: 0.48f,
                Height: 1.835f,
                RuntimeLocalPlayerShadowDisposition.ProvenShapeless));

    private static RuntimeAcceptedPositionDriveController
        CreateAcceptedPositionDrive(GameRuntime runtime) => new(
            runtime.EntityObjects,
            runtime.Clock,
            new LoadedSetupCollisionSource(),
            new LocalPlayerOutboundController((_, _, _, _, _, _) => { }),
            () => runtime.Generation,
            () => runtime.PlayerIdentity.ServerGuid,
            () => runtime.MovementOwner.Controller,
            () => runtime.CharacterOwner.UsePositionFromServer,
            () => null);

    internal sealed class LoadedSetupCollisionSource
        : AcDream.Content.IPreparedCollisionSource
    {
        public AcDream.Content.PreparedAssetPresence ProbeCollision(
            AcDream.Content.Pak.PakAssetType type,
            uint sourceFileId) =>
            AcDream.Content.PreparedAssetPresence.Available;

        public AcDream.Content.PreparedCollisionReadResult<FlatSetupCollision>
            ReadSetupCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            AcDream.Content.PreparedCollisionReadResult<FlatSetupCollision>
                .Loaded(new FlatSetupCollision(
                    System.Collections.Immutable.ImmutableArray<
                        FlatCollisionCylinder>.Empty,
                    [
                        new FlatCollisionSphere(
                            new Vector3(0f, 0f, 0.475f), 0.48f),
                        new FlatCollisionSphere(
                            new Vector3(0f, 0f, 1.350f), 0.48f),
                    ],
                    height: 0f,
                    radius: 0f,
                    stepUpHeight: 0.4f,
                    stepDownHeight: 0.4f));

        public AcDream.Content.PreparedCollisionReadResult<
            FlatGfxObjCollisionAsset> ReadGfxObjCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public AcDream.Content.PreparedCollisionReadResult<
            FlatCellStructureCollisionAsset> ReadCellStructureCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public AcDream.Content.PreparedCollisionReadResult<FlatEnvCellTopology>
            ReadEnvCellTopology(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public AcDream.Content.PreparedCollisionSourceStats CollisionStats =>
            default;

        public void Dispose()
        {
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LocalForcePosition_CommitsTheWireCellOnlyWhenTheDriveDeclined(
        bool driveHandlesIt)
    {
        var operations = new FixtureSessionOperations();
        using var credential = new HeadlessCredentialSecret(
            "fixture",
            "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations);
        GameRuntime runtime = host.Runtime;
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            host.Start().Status);
        const uint player = 0x50000004u;
        runtime.PlayerIdentity.ServerGuid = player;
        runtime.EntityObjects.Physics.SetPosition.BeginCollisionGeneration(
            0xA9B40000u, 1UL);
        AddFlatLandblock(runtime.EntityObjects.Physics.Engine);
        runtime.EntityObjects.Physics.SetPosition.CommitCollisionGeneration(
            0xA9B40000u, 1UL, ready: true);
        RuntimeFirstEntryDriveController firstEntry =
            CreateFirstEntryDrive(runtime);
        RuntimeEntityRecord record = runtime.EntityObjects
            .RegisterEntityWithInitialResidence(
                Spawn(player),
                isLocalPlayer: true)
            .Canonical!;
        Assert.True(runtime.EntityObjects.ApplyAcceptedSpawn(
            record,
            record.CreateIntegrationVersion,
            record.Snapshot,
            replaceGeneration: false));
        var collision = new FixtureCollisionNeighborhood();
        var projection = new HeadlessSessionWorldProjection(
            runtime,
            collision,
            firstEntry);
        projection.ProjectSpawn(record, isLocalPlayer: true);
        PlayerMovementController controller =
            Assert.IsType<PlayerMovementController>(
                runtime.MovementOwner.Controller);
        controller.SeedPlacementForTest(
            new Vector3(48f, 49f, 50f),
            0xA9B40001u,
            new Vector3(48f, 49f, 50f));
        Assert.False(runtime.EntityObjects.TryGetInitialCreateResidence(
            record,
            out _));

        using var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 9000));
        var entities = new RuntimeLiveEntitySessionController(
            runtime,
            session,
            log: null,
            projection,
            driveHandlesIt ? CreateAcceptedPositionDrive(runtime) : null);
        LiveEntitySessionSink sink = entities.CreateSink();

        const uint wireCell = 0xA9B40002u;
        sink.PositionUpdated(new WorldSession.EntityPositionUpdate(
            player,
            new CreateObject.ServerPosition(
                wireCell, 72f, 73f, 50f, 1f, 0f, 0f, 0f),
            Velocity: null,
            PlacementId: null,
            IsGrounded: true,
            InstanceSequence: 1,
            PositionSequence: 2,
            TeleportSequence: 0,
            ForcePositionSequence: 1));

        if (driveHandlesIt)
        {
            Assert.Equal(new Vector3(72f, 73f, 50.005f), controller.Position);
            Assert.Equal(0xA9B4001Cu, record.FullCellId);
            Assert.NotEqual(wireCell, record.FullCellId);
        }
        else
        {
            Assert.Equal(wireCell, record.FullCellId);
            Assert.Equal(0xA9B4FFFFu, record.CanonicalLandblockId);
        }
    }

    /// <summary>
    /// A movement the server drives at this character is an order to walk, and
    /// it is the only answer a use issued from beyond the server's reach ever
    /// gets: obey it or the use is answered, seconds later, as done with
    /// nothing done. The character's own movement, echoed back, is not an
    /// order.
    /// <para>
    /// This pins the seam, not the decode: the whole of the defect was a
    /// missing call on this route, so a test that stops at the decode cannot
    /// tell the fixed host from the broken one.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void AServerDrivenMovementAtTheLocalPlayerStartsTheWalk(
        bool autonomous,
        bool expectWalk)
    {
        var operations = new FixtureSessionOperations();
        using var credential = new HeadlessCredentialSecret(
            "fixture",
            "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations);
        GameRuntime runtime = host.Runtime;
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            host.Start().Status);
        const uint player = 0x50000005u;
        const uint corpse = 0x80000ABCu;
        runtime.PlayerIdentity.ServerGuid = player;
        runtime.EntityObjects.Physics.SetPosition.BeginCollisionGeneration(
            0xA9B40000u, 1UL);
        AddFlatLandblock(runtime.EntityObjects.Physics.Engine);
        runtime.EntityObjects.Physics.SetPosition.CommitCollisionGeneration(
            0xA9B40000u, 1UL, ready: true);
        RuntimeFirstEntryDriveController firstEntry =
            CreateFirstEntryDrive(runtime);
        RuntimeEntityRecord record = runtime.EntityObjects
            .RegisterEntityWithInitialResidence(
                Spawn(player),
                isLocalPlayer: true)
            .Canonical!;
        Assert.True(runtime.EntityObjects.ApplyAcceptedSpawn(
            record,
            record.CreateIntegrationVersion,
            record.Snapshot,
            replaceGeneration: false));
        var collision = new FixtureCollisionNeighborhood();
        var projection = new HeadlessSessionWorldProjection(
            runtime,
            collision,
            firstEntry);
        projection.ProjectSpawn(record, isLocalPlayer: true);
        PlayerMovementController controller =
            Assert.IsType<PlayerMovementController>(
                runtime.MovementOwner.Controller);
        controller.SeedPlacementForTest(
            new Vector3(48f, 49f, 50f),
            0xA9B40001u,
            new Vector3(48f, 49f, 50f));

        using var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 9000));
        var entities = new RuntimeLiveEntitySessionController(
            runtime,
            session,
            log: null,
            projection);
        LiveEntitySessionSink sink = entities.CreateSink();
        Assert.False(controller.MoveTo!.IsMovingTo());
        // Every movement so far has been the character's own.
        Assert.True(controller.PhysicsBody.LastMoveWasAutonomous);

        sink.MotionUpdated(new WorldSession.EntityMotionUpdate(
            player,
            new CreateObject.ServerMotionState(
                Stance: (ushort)0x3Fu,
                ForwardCommand: null,
                MovementType: 6,
                MoveToSpeed: 1f,
                MoveToRunRate: 1.75f,
                MoveToPath: new CreateObject.MoveToPathData(
                    TargetGuid: corpse,
                    OriginCellId: 0xA9B40001u,
                    OriginX: 60f,
                    OriginY: 61f,
                    OriginZ: 50f,
                    DistanceToObject: 1.8f,
                    MinDistance: 0f,
                    FailDistance: 0f,
                    WalkRunThreshold: 15f,
                    DesiredHeading: 0f,
                    Bitfield: 0x403u)),
            InstanceSequence: 1,
            MovementSequence: 2,
            ServerControlSequence: 2,
            IsAutonomous: autonomous));

        Assert.Equal(expectWalk, controller.MoveTo!.IsMovingTo());
        if (!expectWalk)
        {
            // An echo is not an order, so nothing about who is steering
            // changes either.
            Assert.True(controller.PhysicsBody.LastMoveWasAutonomous);
            return;
        }

        // Nothing in this world has a body to follow, so the walk is to the
        // place the order named — the original's own fallback.
        Assert.Equal(
            MovementType.MoveToPosition,
            controller.MoveTo!.MovementTypeState);
        Assert.Equal(1.75f, controller.Movement.Minterp.MyRunRate);
        // The movement owner has to know this move was not the character's
        // own, or the next key press cannot take control back from it.
        Assert.False(controller.PhysicsBody.LastMoveWasAutonomous);
        // And the stance the order was written in is applied before the
        // movement it asks for, which is the step the original takes ahead of
        // every kind of movement it unpacks.
        Assert.Equal(
            0x8000003Fu,
            controller.Movement.Minterp.InterpretedState.CurrentStyle);
    }

    /// <summary>
    /// An ordinary motion — the kind that says which animation is playing —
    /// is not an order to go anywhere, and must not start a walk on a route
    /// that now acts on movements.
    /// </summary>
    [Fact]
    public void AnOrdinaryMotionAtTheLocalPlayerStartsNoWalk()
    {
        var operations = new FixtureSessionOperations();
        using var credential = new HeadlessCredentialSecret(
            "fixture",
            "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations);
        GameRuntime runtime = host.Runtime;
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            host.Start().Status);
        const uint player = 0x50000006u;
        runtime.PlayerIdentity.ServerGuid = player;
        runtime.EntityObjects.Physics.SetPosition.BeginCollisionGeneration(
            0xA9B40000u, 1UL);
        AddFlatLandblock(runtime.EntityObjects.Physics.Engine);
        runtime.EntityObjects.Physics.SetPosition.CommitCollisionGeneration(
            0xA9B40000u, 1UL, ready: true);
        RuntimeFirstEntryDriveController firstEntry =
            CreateFirstEntryDrive(runtime);
        RuntimeEntityRecord record = runtime.EntityObjects
            .RegisterEntityWithInitialResidence(
                Spawn(player),
                isLocalPlayer: true)
            .Canonical!;
        Assert.True(runtime.EntityObjects.ApplyAcceptedSpawn(
            record,
            record.CreateIntegrationVersion,
            record.Snapshot,
            replaceGeneration: false));
        var projection = new HeadlessSessionWorldProjection(
            runtime,
            new FixtureCollisionNeighborhood(),
            firstEntry);
        projection.ProjectSpawn(record, isLocalPlayer: true);
        PlayerMovementController controller =
            Assert.IsType<PlayerMovementController>(
                runtime.MovementOwner.Controller);
        controller.SeedPlacementForTest(
            new Vector3(48f, 49f, 50f),
            0xA9B40001u,
            new Vector3(48f, 49f, 50f));

        using var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 9000));
        LiveEntitySessionSink sink = new RuntimeLiveEntitySessionController(
            runtime,
            session,
            log: null,
            projection).CreateSink();

        sink.MotionUpdated(new WorldSession.EntityMotionUpdate(
            player,
            new CreateObject.ServerMotionState(
                Stance: (ushort)0x3Du,
                ForwardCommand: (ushort)0x45,
                MovementType: 0),
            InstanceSequence: 1,
            MovementSequence: 2,
            ServerControlSequence: 2,
            IsAutonomous: false));

        Assert.False(controller.MoveTo!.IsMovingTo());
    }

    private static void AddFlatLandblock(PhysicsEngine engine)
    {
        var heights = new byte[81];
        Array.Fill(heights, (byte)50);
        var heightTable = new float[256];
        for (int index = 0; index < heightTable.Length; index++)
            heightTable[index] = index;
        engine.AddLandblock(
            0xA9B4FFFFu,
            new TerrainSurface(heights, heightTable),
            [],
            [],
            worldOffsetX: 0f,
            worldOffsetY: 0f);
    }

    internal static WorldSession.EntitySpawn Spawn(
        uint guid,
        uint cellId = 0xA9B40001u)
    {
        var position = new CreateObject.ServerPosition(
            cellId,
            96f,
            97f,
            50f,
            1f,
            0f,
            0f,
            0f);
        var timestamps = new PhysicsTimestamps(
            Position: 1,
            Movement: 1,
            State: 1,
            Vector: 1,
            Teleport: 0,
            ServerControlledMove: 1,
            ForcePosition: 0,
            ObjDesc: 1,
            Instance: 1);
        var physics = new PhysicsSpawnData(
            RawState: (uint)PhysicsStateFlags.ReportCollisions,
            Position: position,
            Movement: null,
            AnimationFrame: null,
            SetupTableId: 0x02000001u,
            MotionTableId: null,
            SoundTableId: null,
            PhysicsScriptTableId: null,
            Parent: null,
            Children: null,
            Scale: null,
            Friction: null,
            Elasticity: null,
            Translucency: null,
            Velocity: null,
            Acceleration: null,
            AngularVelocity: null,
            DefaultScriptType: null,
            DefaultScriptIntensity: null,
            Timestamps: timestamps);
        return new WorldSession.EntitySpawn(
            guid,
            position,
            0x02000001u,
            [],
            [],
            [],
            null,
            null,
            "Headless",
            null,
            null,
            null,
            PhysicsState: physics.RawState,
            InstanceSequence: 1,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }

    private static RuntimePlacementProjectionSnapshot Placement(
        GameRuntime runtime,
        RuntimeEntityRecord record,
        RuntimePlacementProjectionKind kind,
        Vector3 position,
        Quaternion orientation)
    {
        RuntimeEntityKey key = Assert.IsType<RuntimeEntityKey>(record.Key);
        var token = new RuntimePlacementProjectionToken(
            Sequence: 1,
            Revision: 1,
            Entity: key,
            PositionAuthorityVersion: record.PositionAuthorityVersion,
            SpatialAuthorityVersion: record.SpatialAuthorityVersion,
            PlacementCommitVersion: record.PlacementCommitVersion,
            SessionLifetimeVersion:
                runtime.EntityObjects.Entities.SessionLifetimeVersion,
            ExactCellId: record.FullCellId,
            CollisionGeneration: 1,
            Portal: default);
        return new RuntimePlacementProjectionSnapshot(
            token,
            kind,
            position,
            orientation,
            CellLocalPosition: position,
            InContact: false,
            OnWalkable: false);
    }

    private static uint ActionOpcode(byte[] body) =>
        BinaryPrimitives.ReadUInt32LittleEndian(
            body.AsSpan(8, sizeof(uint)));

    /// <summary>
    /// What the session's login commands put on the wire. Arriving in the
    /// world is itself something the client says -- it asks the server to
    /// state the allegiance, which nothing else will make it say -- and that
    /// belongs to the arrival rather than to the configured commands.
    /// </summary>
    private static IReadOnlyList<byte[]> LoginCommandSends(
        IEnumerable<byte[]> captured) =>
        [.. captured.Where(static body =>
            ActionOpcode(body)
                != AllegianceRequests.AllegianceUpdateRequestOpcode)];

    private static string TalkText(byte[] body)
    {
        Assert.Equal(ChatRequests.TalkOpcode, ActionOpcode(body));
        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(
            body.AsSpan(12, sizeof(ushort)));
        return System.Text.Encoding.ASCII.GetString(body, 14, length);
    }

    private static string StringActionArgument(byte[] body)
    {
        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(
            body.AsSpan(12, sizeof(ushort)));
        return System.Text.Encoding.ASCII.GetString(body, 14, length);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        internal void Advance(TimeSpan duration) =>
            _timestamp = checked(_timestamp + duration.Ticks);
    }

    private static byte[] WrapRegisterTradeEnvelope(
        uint initiator, uint partner, ulong stamp)
    {
        byte[] payload = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, initiator);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), partner);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(8), stamp);

        byte[] body = new byte[GameEventEnvelope.HeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(body,            GameEventEnvelope.Opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),  0u);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),  0u);
        BinaryPrimitives.WriteUInt32LittleEndian(
            body.AsSpan(12), (uint)GameEventType.RegisterTrade);
        Array.Copy(payload, 0, body, GameEventEnvelope.HeaderSize, payload.Length);
        return body;
    }
    private static byte[] WrapCharacterConfirmationDoneEnvelope(uint type, uint contextId)
    {
        byte[] payload = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, type);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), contextId);

        byte[] body = new byte[GameEventEnvelope.HeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(body,            GameEventEnvelope.Opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),  0u);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),  0u);
        BinaryPrimitives.WriteUInt32LittleEndian(
            body.AsSpan(12), (uint)GameEventType.CharacterConfirmationDone);
        Array.Copy(payload, 0, body, GameEventEnvelope.HeaderSize, payload.Length);
        return body;
    }

    private static byte[] WrapPlayerDescriptionEnvelope(
        uint options1,
        uint options2)
    {
        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(
            stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0u);       // property flags
            writer.Write(0x52u);    // player weenie type
            writer.Write(0u);       // vector flags
            writer.Write(0u);       // has health
            writer.Write(0x40u);
            writer.Write(options1);
            writer.Write(0u);
            writer.Write(0u);       // spellbook filters
            writer.Write(options2);
            writer.Write(0u);
            writer.Write(0u);
        }

        byte[] payload = stream.ToArray();
        byte[] body = new byte[GameEventEnvelope.HeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(body,            GameEventEnvelope.Opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),  0u);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),  0u);
        BinaryPrimitives.WriteUInt32LittleEndian(
            body.AsSpan(12), (uint)GameEventType.PlayerDescription);
        Array.Copy(payload, 0, body, GameEventEnvelope.HeaderSize, payload.Length);
        return body;
    }

    internal sealed class FixtureSessionOperations : ILiveSessionOperations
    {
        private int _enterWorldCallCount;
        private int _tickCallCount;

        public List<WorldSession> Sessions { get; } = [];
        public int CreatedSessionCount { get; private set; }
        public int DisposedSessionCount { get; private set; }
        public string? LastUser { get; private set; }
        public string? LastPassword { get; private set; }
        public Action<byte[]>? GameActionCapture { get; init; }
        public bool ServerListensFromTheStart { get; init; } = true;
        public bool ThrowOnDisposeSession { get; set; }
        public int EnterWorldCallCount =>
            Volatile.Read(ref _enterWorldCallCount);
        public int TickCallCount => Volatile.Read(ref _tickCallCount);
        public CharacterList.Parsed? Characters { get; init; } = new(
            0u,
            [
                new CharacterList.Character(
                    0x50000001u,
                    "Other",
                    0u),
                new CharacterList.Character(
                    0x50000002u,
                    "Headless",
                    0u),
            ],
            [],
            11,
            "account",
            true,
            true);

        public IPEndPoint ResolveEndpoint(string host, int port) =>
            new(IPAddress.Loopback, port);

        public WorldSession CreateSession(IPEndPoint endpoint)
        {
            CreatedSessionCount++;
            var session = new WorldSession(endpoint).TakingItsSends();
            session.GameActionCapture = GameActionCapture;
            // This scripted server never sends the character's own object, so
            // the client is never prompted to complete its login. A test that
            // is about that wait turns this off.
            if (ServerListensFromTheStart)
                session.AssumeLoginCompleteForTesting();
            Sessions.Add(session);
            return session;
        }

        public void Connect(
            WorldSession session,
            string user,
            string password)
        {
            LastUser = user;
            LastPassword = password;
        }

        public CharacterList.Parsed? GetCharacters(
            WorldSession session) => Characters;

        public void EnterWorld(
            WorldSession session,
            int activeCharacterIndex)
        {
            Interlocked.Increment(ref _enterWorldCallCount);
        }

        public Action? OnTick { get; set; }

        public void Tick(WorldSession session)
        {
            Interlocked.Increment(ref _tickCallCount);
            OnTick?.Invoke();
        }

        public void DisposeSession(WorldSession session)
        {
            DisposedSessionCount++;
            if (ThrowOnDisposeSession)
                throw new InvalidOperationException("fixture teardown failure");
            session.Dispose();
        }

        public int RequestCharacterLogOffCount { get; private set; }
        public int ReturnToCharacterSelectCount { get; private set; }

        public void RequestCharacterLogOff(WorldSession session) =>
            RequestCharacterLogOffCount++;

        public void ReturnToCharacterSelect(WorldSession session) =>
            ReturnToCharacterSelectCount++;
    }

    private sealed class FailOnceTextWriter : StringWriter
    {
        public bool FailNextWrite { get; set; }

        public override void WriteLine(string? value)
        {
            if (FailNextWrite)
            {
                FailNextWrite = false;
                throw new IOException("fixture write failure");
            }
            base.WriteLine(value);
        }
    }

    private sealed class FixtureCollisionNeighborhood
        : IHeadlessCollisionNeighborhood
    {
        public int CenterCount { get; private set; }
        public uint LastCell { get; private set; }
        public List<uint> Followed { get; } = [];

        public void Follow(uint fullCellId) => Followed.Add(fullCellId);

        public void CenterOn(uint fullCellId)
        {
            CenterCount++;
            LastCell = fullCellId;
        }

        public bool IsReady(uint fullCellId) =>
            fullCellId == LastCell;

        public bool IsQuiescent => true;

        public bool IsCollisionPublished(uint fullCellId) =>
            IsWithinServiceWindow(fullCellId);

        public bool IsWithinServiceWindow(uint fullCellId)
        {
            if (LastCell == 0u)
                return true;
            int dx = Math.Abs(
                (int)((fullCellId >> 24) & 0xFFu)
                - (int)((LastCell >> 24) & 0xFFu));
            int dy = Math.Abs(
                (int)((fullCellId >> 16) & 0xFFu)
                - (int)((LastCell >> 16) & 0xFFu));
            return dx <= 1 && dy <= 1;
        }
    }

    private sealed class NeighborAdmissionHeldOpenCollisionNeighborhood(
        RuntimePhysicsState physics,
        uint heldLandblockId) : IHeadlessCollisionNeighborhood
    {
        private RuntimeCollisionAdmission? _admission;
        private PreparedLandblockCollisionGeneration? _prepared;

        internal void OpenHeldAdmission()
        {
            _admission = physics.BeginCollisionAdmission(heldLandblockId);
            _prepared = physics.PrepareCollisionGeneration(_admission);
        }

        internal void ReleaseHeldAdmission()
        {
            if (_admission is null)
                return;
            bool cancelled = physics.CancelCollisionGeneration(
                _admission,
                _prepared);
            Assert.True(
                cancelled,
                "the held-open neighbor admission did not cancel in one call.");
            _admission = null;
            _prepared = null;
        }

        public bool IsQuiescent => _admission is null;

        public void Follow(uint fullCellId)
        {
        }

        public void CenterOn(uint fullCellId)
        {
        }

        public bool IsReady(uint fullCellId) => true;

        public bool IsWithinServiceWindow(uint fullCellId) => true;

        public bool IsCollisionPublished(uint fullCellId) => true;
    }

    private sealed class GateControllableCollisionNeighborhood
        : IHeadlessCollisionNeighborhood
    {
        private uint _lastCell;

        internal bool QuiescentOverride { get; set; } = true;

        public void CenterOn(uint fullCellId) => _lastCell = fullCellId;

        public bool IsReady(uint fullCellId) => fullCellId == _lastCell;

        public bool IsWithinServiceWindow(uint fullCellId) => true;

        public bool IsCollisionPublished(uint fullCellId) => true;

        public bool IsQuiescent => QuiescentOverride;

        public void Follow(uint fullCellId)
        {
        }
    }

    private sealed class FixtureEventRoute(
        Action? onDispose = null) : ILiveSessionEventRouting
    {
        public int AttachCount { get; private set; }
        public int DisposeCount { get; private set; }
        public int DisposeFailuresRemaining { get; set; }

        public void Attach() => AttachCount++;

        public void Dispose()
        {
            DisposeCount++;
            onDispose?.Invoke();
            if (DisposeFailuresRemaining > 0)
            {
                DisposeFailuresRemaining--;
                throw new IOException("fixture route detach failure");
            }
        }
    }

    private static HeadlessContentDescriptor ContentDescriptor() => new()
    {
        DatDirectory = "fixture-dats",
        PreparedAssetPath = "fixture.pak",
    };

    private sealed class ChargenFixtureContentFactory : IHeadlessProcessContentFactory
    {
        internal const uint HeritageId = 1u;

        public HeadlessOpenedProcessContent Open(
            HeadlessContentDescriptor descriptor,
            Action<string> diagnostic) =>
            new(
                new FixtureChargenDatReaderWriter(),
                DispatchProxy.Create<ITestPreparedSource, TestResourceProxy>(),
                MagicCatalog.Empty,
                ImmutableArray.CreateRange(new float[256]));
    }

    private sealed class FixtureChargenDatReaderWriter : IDatReaderWriter
    {
        private readonly StubDatabase _db = new();
        private readonly DatCharGen _chargenTable = BuildChargenTable();

        public string SourceDirectory => string.Empty;
        public IDatDatabase Portal => _db;
        public IDatDatabase Cell => _db;
        public ReadOnlyDictionary<uint, IDatDatabase> CellRegions { get; } =
            new(new Dictionary<uint, IDatDatabase>());
        public IDatDatabase HighRes => _db;
        public IDatDatabase Language => _db;
        public IDatDatabase Local => _db;
        public ReadOnlyDictionary<uint, uint> RegionFileMap { get; } =
            new(new Dictionary<uint, uint>());
        public int PortalIteration => 0;
        public int CellIteration => 0;
        public int HighResIteration => 0;
        public int LanguageIteration => 0;

        public bool TryGetFileBytes(
            uint regionId,
            uint fileId,
            ref byte[] bytes,
            out int bytesRead)
        {
            bytesRead = 0;
            return false;
        }

        public IEnumerable<uint> GetAllIdsOfType<T>() where T : DatIDBObj =>
            Array.Empty<uint>();

        public IEnumerable<IDatReaderWriter.IdResolution> ResolveId(uint id) =>
            Array.Empty<IDatReaderWriter.IdResolution>();

        public bool TrySave<T>(T obj, int iteration = 0) where T : DatIDBObj =>
            throw new NotSupportedException();

        public bool TrySave<T>(uint regionId, T obj, int iteration = 0) where T : DatIDBObj =>
            throw new NotSupportedException();

        [return: MaybeNull]
        public T Get<T>(uint fileId) where T : DatIDBObj =>
            fileId == ChargenTableReader.ChargenTableDid
            && typeof(T) == typeof(DatCharGen)
                ? (T)(object)_chargenTable
                : default;

        public bool TryGet<T>(uint fileId, [MaybeNullWhen(false)] out T value)
            where T : DatIDBObj
        {
            value = Get<T>(fileId);
            return value is not null;
        }

        public void Dispose() { }

        private static DatCharGen BuildChargenTable()
        {
            var table = new DatCharGen();
            var heritage = new DatHeritageGroupCG
            {
                Name = Str("FixtureHeritage"),
                AttributeCredits = 60u,
                SkillCredits = 50u,
            };
            table.HeritageGroups.Add(ChargenFixtureContentFactory.HeritageId, heritage);
            return table;
        }

        private static DatPStringBaseByte Str(string value)
        {
            var s = new DatPStringBaseByte();
            s.Value = value;
            return s;
        }

        private sealed class StubDatabase : IDatDatabase
        {
            public DatDatabaseImpl Db => null!;
            public int Iteration => 0;
            public IEnumerable<uint> GetAllIdsOfType<T>() where T : DatIDBObj =>
                Array.Empty<uint>();
            public bool TryGet<T>(uint fileId, [MaybeNullWhen(false)] out T value)
                where T : DatIDBObj
            {
                value = default;
                return false;
            }
            public bool TryGetFileBytes(uint fileId, [MaybeNullWhen(false)] out byte[] value)
            {
                value = default;
                return false;
            }
            public bool TryGetFileBytes(uint fileId, ref byte[] bytes, out int bytesRead)
            {
                bytesRead = 0;
                return false;
            }
            public bool TrySave<T>(T obj, int iteration = 0) where T : DatIDBObj => false;
            public void Dispose() { }
        }
    }
}
