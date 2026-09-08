using System.Buffers.Binary;
using System.Net;
using System.Reflection;
using System.Text;
using AcDream.Core.CharGen;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Net.Packets;
using AcDream.Launcher.Core.Status;
using AcDream.Runtime;
using AcDream.Runtime.Session;
using AcDream.Runtime.Tests.CharGen;

namespace AcDream.Runtime.Tests.Session;

public sealed class LiveSessionControllerCharacterCreationTests
{
    private sealed class TestTransport : IWorldSessionTransport
    {
        public void Send(ReadOnlySpan<byte> datagram) { }
        public void Send(IPEndPoint remote, ReadOnlySpan<byte> datagram) { }
        public int Receive(Span<byte> destination, TimeSpan timeout, out IPEndPoint? from)
        {
            from = null;
            return -1;
        }
        public ValueTask<NetReceiveResult> ReceiveAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken) =>
            throw new OperationCanceledException(cancellationToken);
        public void Dispose() { }
    }

    private sealed class TestOperations : ILiveSessionOperations
    {
        public List<WorldSession> Sessions { get; } = [];
        public int EnterWorldCount { get; private set; }

        public List<(uint Guid, string AccountName)> EnterWorldByGuidCalls { get; } = [];

        public IPEndPoint ResolveEndpoint(string host, int port) =>
            new(IPAddress.Loopback, port);

        public WorldSession CreateSession(IPEndPoint endpoint)
        {
            var session = new WorldSession(endpoint, new TestTransport());
            Sessions.Add(session);
            return session;
        }

        public void Connect(WorldSession session, string user, string password) { }

        public void StartCharacterSelectionReceive(WorldSession session) { }

        public CharacterList.Parsed? GetCharacters(WorldSession session) => new(
            0u,
            [
                new CharacterList.Character(0x50000002u, "Zed", 0u),
                new CharacterList.Character(0x50000003u, "Amy", 0u),
            ],
            [],
            SlotCount: 11,
            AccountName: "testaccount",
            true,
            true);

        public void EnterWorld(WorldSession session, int activeCharacterIndex) =>
            EnterWorldCount++;

        public int EnterWorldByGuidRejectionsRemaining { get; set; }

        public void EnterWorldByGuid(
            WorldSession session,
            uint characterGuid,
            string accountName)
        {
            EnterWorldByGuidCalls.Add((characterGuid, accountName));
            if (EnterWorldByGuidRejectionsRemaining > 0)
            {
                EnterWorldByGuidRejectionsRemaining--;
                throw new CharacterSelectionRejectedException(
                    new CharacterError.Parsed(0x0000000Bu));
            }
        }

        public void Tick(WorldSession session) { }

        public void DisposeSession(WorldSession session) { }
    }

    private sealed class TestHost : ILiveSessionLifecycleHost
    {
        public List<LiveSessionRosterReport> Rosters { get; } = [];
        public List<LiveSessionCharacterSelection> EnteredWorld { get; } = [];
        public List<RuntimeCharacterCreationIdentity> Created { get; } = [];
        public List<RuntimeCharacterCreationRejection> Failed { get; } = [];

        public SessionStatusWriter? Writer { get; set; }
        public string SessionId { get; set; } = "s1";

        public LiveSessionBinding BindSession(WorldSession session) =>
            new(session, activateCommands: () => { }, deactivateCommands: () => { }, detachEvents: () => { });
        public void ResetSessionState(RuntimeGenerationToken retiringGeneration) { }
        public void ReportConnecting(string host, int port, string user) { }
        public void ReportConnected() { }
        public void ReportRoster(LiveSessionRosterReport roster) => Rosters.Add(roster);
        public void ApplySelectedCharacter(LiveSessionCharacterSelection selection) { }
        public void ApplyEnteredWorld(LiveSessionCharacterSelection selection) =>
            EnteredWorld.Add(selection);
        public void DetachSession(WorldSession session) { }
        public void ApplyCharacterCreated(RuntimeCharacterCreationIdentity identity)
        {
            Created.Add(identity);
            Writer?.CharacterCreated(SessionId, identity.Guid, identity.Name);
        }
        public void ApplyCreationFailed(RuntimeCharacterCreationRejection rejection)
        {
            Failed.Add(rejection);
            Writer?.CreationFailed(
                SessionId, rejection.RawCode, rejection.Reason, rejection.AttemptedName);
        }
    }

    private static LiveSessionConnectOptions LiveOptions() => new(
        Enabled: true,
        "127.0.0.1",
        9000,
        "testaccount",
        "password",
        Character: null,
        Probe: false,
        AwaitCharacterSelection: true);

    private static (LiveSessionController Controller, TestOperations Operations, TestHost Host, RuntimeGenerationToken Generation)
        StartAwaitingSelection()
    {
        var operations = new TestOperations();
        var host = new TestHost();
        var controller = new LiveSessionController(
            operations,
            timeProvider: null,
            RuntimeCharacterCreationStateFixture.Build());

        LiveSessionStartResult result = controller.Start(LiveOptions(), host);
        Assert.Equal(LiveSessionStartStatus.AwaitingCharacterSelection, result.Status);

        return (controller, operations, host, controller.Generation);
    }

    private static void BuildReadyCharacter(LiveSessionController controller, RuntimeGenerationToken generation)
    {
        Assert.True(controller.SelectHeritage(generation, RuntimeCharacterCreationStateFixture.AluvianId).Accepted);
        Assert.True(controller.SelectGender(generation, RuntimeCharacterCreationStateFixture.MaleGenderKey).Accepted);
        Assert.True(controller.SelectTemplate(generation, RuntimeCharacterCreationStateFixture.PresetTemplateIndex).Accepted);
        Assert.True(controller.SetName(generation, "NewChar").Accepted);
    }

    [Fact]
    public void Finish_SendsExactly55SkillSlotsAndTheCorrectAttributesAndName()
    {
        (LiveSessionController controller, TestOperations operations, _, RuntimeGenerationToken generation) =
            StartAwaitingSelection();
        BuildReadyCharacter(controller, generation);

        WorldSession session = operations.Sessions[0];
        byte[]? captured = null;
        GameMessageGroup? capturedGroup = null;
        session.GameMessageCapture = (body, group) =>
        {
            captured = body;
            capturedGroup = group;
        };

        RuntimeCommandResult result = controller.Finish(generation);

        Assert.True(result.Accepted);
        Assert.NotNull(captured);
        Assert.Equal(GameMessageGroup.LoginQueue, capturedGroup);

        CapturedCreateRequest decoded = DecodeCreateRequest(captured!);
        Assert.Equal("testaccount", decoded.AccountName);
        Assert.Equal(RuntimeCharacterCreationStateFixture.AluvianId, decoded.Heritage);
        Assert.Equal(RuntimeCharacterCreationStateFixture.MaleGenderKey, decoded.Gender);
        Assert.Equal(RuntimeCharacterCreationStateFixture.PresetTemplateIndex, decoded.Template);
        Assert.Equal(16u, decoded.Strength);
        Assert.Equal("NewChar", decoded.Name);
        Assert.Equal((uint)CharacterCreate.SkillAdvancementClassCount, decoded.NumSkills);
        Assert.Equal(CharacterCreate.SkillAdvancementClassCount, decoded.SkillAdvancementClasses.Length);
        Assert.Equal(
            (uint)ChargenSkillAdvancementClass.Trained,
            decoded.SkillAdvancementClasses[RuntimeCharacterCreationStateFixture.SkillTrainSpecialize]);
        Assert.Equal(
            (uint)ChargenSkillAdvancementClass.Specialized,
            decoded.SkillAdvancementClasses[RuntimeCharacterCreationStateFixture.SkillPresetPrimary]);
    }

    [Fact]
    public void Finish_ThenOkResponse_AppendsToRosterAndLogsStraightIn()
    {
        (LiveSessionController controller, TestOperations operations, TestHost host, RuntimeGenerationToken generation) =
            StartAwaitingSelection();
        BuildReadyCharacter(controller, generation);
        WorldSession session = operations.Sessions[0];
        session.GameMessageCapture = (_, _) => { };

        Assert.True(controller.Finish(generation).Accepted);

        InvokeProcessDatagram(session, BuildResponsePacket(
            (uint)CharGenVerificationResponse.Code.Ok, 0x50001234u, "NewChar"));

        Assert.Single(host.Created);
        Assert.Equal(0x50001234u, host.Created[0].Guid);
        Assert.Equal("NewChar", host.Created[0].Name);
        Assert.Empty(host.Failed);

        LiveSessionRosterReport lastReport = host.Rosters[^1];
        Assert.Contains(lastReport.Entries, e => e.Id == 0x50001234u && e.Name == "NewChar");
        Assert.Contains(lastReport.Entries, e => e.Id == 0x50000002u && e.Name == "Zed");
        Assert.Contains(lastReport.Entries, e => e.Id == 0x50000003u && e.Name == "Amy");

        Assert.True(controller.CharacterSelectionState.View.TryGet(0x50000002u, out RuntimeCharacterSelectionEntry zed));
        Assert.Equal(0, zed.ActiveIndex);
        Assert.True(controller.CharacterSelectionState.View.TryGet(0x50000003u, out RuntimeCharacterSelectionEntry amy));
        Assert.Equal(1, amy.ActiveIndex);
        Assert.True(controller.CharacterSelectionState.View.TryGet(0x50001234u, out RuntimeCharacterSelectionEntry newChar));
        Assert.Equal(2, newChar.ActiveIndex);

        Assert.True(controller.IsInWorld);
        Assert.Equal(0, operations.EnterWorldCount);
        Assert.Single(operations.EnterWorldByGuidCalls);
        Assert.Equal(0x50001234u, operations.EnterWorldByGuidCalls[0].Guid);
        Assert.Equal("testaccount", operations.EnterWorldByGuidCalls[0].AccountName);
        Assert.Single(host.EnteredWorld);
        Assert.Equal(0x50001234u, host.EnteredWorld[0].CharacterId);
    }

    [Fact]
    public void SecondCreate_AfterRejectedEnter_GetsTheNextWireSlot()
    {
        (LiveSessionController controller, TestOperations operations, TestHost host, RuntimeGenerationToken generation) =
            StartAwaitingSelection();
        BuildReadyCharacter(controller, generation);
        WorldSession session = operations.Sessions[0];
        session.GameMessageCapture = (_, _) => { };
        operations.EnterWorldByGuidRejectionsRemaining = 1;

        Assert.True(controller.Finish(generation).Accepted);
        InvokeProcessDatagram(session, BuildResponsePacket(
            (uint)CharGenVerificationResponse.Code.Ok, 0x50001234u, "NewChar"));

        Assert.False(controller.IsInWorld);
        Assert.Single(operations.EnterWorldByGuidCalls);
        Assert.True(controller.CharacterSelectionState.View.TryGet(0x50001234u, out RuntimeCharacterSelectionEntry firstCreated));
        Assert.Equal(2, firstCreated.ActiveIndex);

        Assert.True(controller.SetName(generation, "SecondChar").Accepted);
        Assert.True(controller.Finish(generation).Accepted);
        InvokeProcessDatagram(session, BuildResponsePacket(
            (uint)CharGenVerificationResponse.Code.Ok, 0x50005678u, "SecondChar"));

        Assert.True(controller.CharacterSelectionState.View.TryGet(0x50005678u, out RuntimeCharacterSelectionEntry secondCreated));
        Assert.Equal(3, secondCreated.ActiveIndex);
        // Pre-existing wire indices still intact after both appends.
        Assert.True(controller.CharacterSelectionState.View.TryGet(0x50000002u, out RuntimeCharacterSelectionEntry zed));
        Assert.Equal(0, zed.ActiveIndex);
        Assert.True(controller.CharacterSelectionState.View.TryGet(0x50000003u, out RuntimeCharacterSelectionEntry amy));
        Assert.Equal(1, amy.ActiveIndex);
        Assert.Equal(2, host.Created.Count);
    }

    [Fact]
    public void Finish_ThenNameInUseResponse_SurfacesRejectionAndStaysAwaitingSelection()
    {
        (LiveSessionController controller, TestOperations operations, TestHost host, RuntimeGenerationToken generation) =
            StartAwaitingSelection();
        BuildReadyCharacter(controller, generation);
        WorldSession session = operations.Sessions[0];
        session.GameMessageCapture = (_, _) => { };

        Assert.True(controller.Finish(generation).Accepted);

        InvokeProcessDatagram(session, BuildResponsePacket(
            (uint)CharGenVerificationResponse.Code.NameInUse, 0u, string.Empty));

        Assert.Single(host.Failed);
        Assert.Equal(CharGenVerificationResponse.Code.NameInUse, host.Failed[0].Code);
        Assert.Equal("NewChar", host.Failed[0].AttemptedName);
        Assert.Empty(host.Created);
        Assert.False(controller.IsInWorld);
        Assert.Equal(0, operations.EnterWorldCount);
        Assert.Empty(operations.EnterWorldByGuidCalls);
        Assert.Empty(host.EnteredWorld);
        // No roster append on a rejection.
        Assert.DoesNotContain(host.Rosters, r => r.Entries.Any(e => e.Name == "NewChar"));
    }

    [Fact]
    public void Finish_RefusedLocallyWithUnspentAttributeCredits_NeverTouchesTheWire()
    {
        (LiveSessionController controller, TestOperations operations, _, RuntimeGenerationToken generation) =
            StartAwaitingSelection();
        Assert.True(controller.SelectHeritage(generation, RuntimeCharacterCreationStateFixture.AluvianId).Accepted);
        Assert.True(controller.SelectGender(generation, RuntimeCharacterCreationStateFixture.MaleGenderKey).Accepted);
        Assert.True(controller.SelectTemplate(generation, RuntimeCharacterCreationStateFixture.CustomTemplateIndex).Accepted);
        Assert.True(controller.SetName(generation, "NewChar").Accepted);
        WorldSession session = operations.Sessions[0];
        bool sent = false;
        session.GameMessageCapture = (_, _) => sent = true;

        RuntimeCommandResult result = controller.Finish(generation);

        Assert.False(result.Accepted);
        Assert.False(sent);
    }

    [Fact]
    public void Finish_WithUnspentCreditsAndConfirmed_SendsAnyway()
    {
        (LiveSessionController controller, TestOperations operations, _, RuntimeGenerationToken generation) =
            StartAwaitingSelection();
        Assert.True(controller.SelectHeritage(generation, RuntimeCharacterCreationStateFixture.AluvianId).Accepted);
        Assert.True(controller.SelectGender(generation, RuntimeCharacterCreationStateFixture.MaleGenderKey).Accepted);
        Assert.True(controller.SelectTemplate(generation, RuntimeCharacterCreationStateFixture.CustomTemplateIndex).Accepted);
        Assert.True(controller.SetName(generation, "NewChar").Accepted);
        WorldSession session = operations.Sessions[0];
        byte[]? captured = null;
        session.GameMessageCapture = (body, _) => captured = body;

        // Unconfirmed: refused, matching the sibling test above — the
        // warning-dialog gate.
        Assert.False(controller.Finish(generation).Accepted);
        Assert.Null(captured);

        RuntimeCommandResult confirmed = controller.Finish(generation, confirmUnspentCredits: true);

        Assert.True(confirmed.Accepted);
        Assert.NotNull(captured);
        CapturedCreateRequest decoded = DecodeCreateRequest(captured!);
        Assert.Equal("NewChar", decoded.Name);
        Assert.Equal(10u, decoded.Strength);
    }

    [Fact]
    public void Finish_SendsEveryWireFieldByteExactAgainstACEsUnpackShape()
    {
        (LiveSessionController controller, TestOperations operations, _, RuntimeGenerationToken generation) =
            StartAwaitingSelection();

        Assert.True(controller.SelectHeritage(generation, RuntimeCharacterCreationStateFixture.AluvianId).Accepted);
        Assert.True(controller.SelectGender(generation, RuntimeCharacterCreationStateFixture.MaleGenderKey).Accepted);
        Assert.True(controller.SetAppearanceIndex(generation, ChargenAppearanceSlot.EyesStrip, 0u).Accepted);
        Assert.True(controller.SetAppearanceIndex(generation, ChargenAppearanceSlot.NoseStrip, 0u).Accepted);
        Assert.True(controller.SetAppearanceIndex(generation, ChargenAppearanceSlot.MouthStrip, 0u).Accepted);
        Assert.True(controller.SetAppearanceIndex(generation, ChargenAppearanceSlot.HairStyle, 1u).Accepted);
        Assert.True(controller.SetAppearanceIndex(generation, ChargenAppearanceSlot.HairColor, 1u).Accepted);
        Assert.True(controller.SetAppearanceIndex(generation, ChargenAppearanceSlot.EyeColor, 1u).Accepted);
        Assert.True(controller.SetAppearanceIndex(generation, ChargenAppearanceSlot.HeadgearStyle, 0u).Accepted);
        Assert.True(controller.SetAppearanceIndex(generation, ChargenAppearanceSlot.HeadgearColor, 2u).Accepted);
        Assert.True(controller.SetAppearanceIndex(generation, ChargenAppearanceSlot.ShirtStyle, 0u).Accepted);
        Assert.True(controller.SetAppearanceIndex(generation, ChargenAppearanceSlot.ShirtColor, 1u).Accepted);
        Assert.True(controller.SetAppearanceIndex(generation, ChargenAppearanceSlot.TrousersStyle, 0u).Accepted);
        Assert.True(controller.SetAppearanceIndex(generation, ChargenAppearanceSlot.TrousersColor, 0u).Accepted);
        Assert.True(controller.SetAppearanceIndex(generation, ChargenAppearanceSlot.FootwearStyle, 0u).Accepted);
        Assert.True(controller.SetAppearanceIndex(generation, ChargenAppearanceSlot.FootwearColor, 2u).Accepted);
        Assert.True(controller.SetShade(generation, ChargenShadeSlot.Skin, 0.25).Accepted);
        Assert.True(controller.SetShade(generation, ChargenShadeSlot.Hair, 0.5).Accepted);
        Assert.True(controller.SetShade(generation, ChargenShadeSlot.Headgear, 0.75).Accepted);
        Assert.True(controller.SetShade(generation, ChargenShadeSlot.Shirt, 0.1).Accepted);
        Assert.True(controller.SetShade(generation, ChargenShadeSlot.Trousers, 0.9).Accepted);
        Assert.True(controller.SetShade(generation, ChargenShadeSlot.Footwear, 0.6).Accepted);
        Assert.True(controller.SelectTemplate(generation, RuntimeCharacterCreationStateFixture.PresetTemplateIndex).Accepted);
        Assert.True(controller.TrainSkill(generation, RuntimeCharacterCreationStateFixture.SkillFreeTrained).Accepted);
        // Town: the fixture's global starter-area list is [Holtburg(0), Yaraq(1)].
        Assert.True(controller.SelectStartArea(generation, 1).Accepted);
        Assert.True(controller.SetName(generation, "FullChar").Accepted);

        WorldSession session = operations.Sessions[0];
        byte[]? captured = null;
        session.GameMessageCapture = (body, _) => captured = body;

        Assert.True(controller.Finish(generation).Accepted);
        Assert.NotNull(captured);

        DecodedFullRequest decoded = DecodeCreateRequestFull(captured!);

        Assert.Equal("testaccount", decoded.AccountName);
        Assert.Equal(1u, decoded.Constant);
        CharacterCreate.Request r = decoded.Request;
        Assert.Equal(RuntimeCharacterCreationStateFixture.AluvianId, r.Heritage);
        Assert.Equal(RuntimeCharacterCreationStateFixture.MaleGenderKey, r.Gender);
        Assert.Equal(0u, r.Appearance.EyesStrip);
        Assert.Equal(0u, r.Appearance.NoseStrip);
        Assert.Equal(0u, r.Appearance.MouthStrip);
        Assert.Equal(1u, r.Appearance.HairColor);
        Assert.Equal(1u, r.Appearance.EyeColor);
        Assert.Equal(1u, r.Appearance.HairStyle);
        Assert.Equal(0u, r.Appearance.HeadgearStyle);
        Assert.Equal(2u, r.Appearance.HeadgearColor);
        Assert.Equal(0u, r.Appearance.ShirtStyle);
        Assert.Equal(1u, r.Appearance.ShirtColor);
        Assert.Equal(0u, r.Appearance.TrousersStyle);
        Assert.Equal(0u, r.Appearance.TrousersColor);
        Assert.Equal(0u, r.Appearance.FootwearStyle);
        Assert.Equal(2u, r.Appearance.FootwearColor);
        Assert.Equal(0.25, r.Appearance.SkinShade);
        Assert.Equal(0.5, r.Appearance.HairShade);
        Assert.Equal(0.75, r.Appearance.HeadgearShade);
        Assert.Equal(0.1, r.Appearance.ShirtShade);
        Assert.Equal(0.9, r.Appearance.TrousersShade);
        Assert.Equal(0.6, r.Appearance.FootwearShade);
        Assert.Equal(RuntimeCharacterCreationStateFixture.PresetTemplateIndex, r.Template);
        Assert.Equal(16u, r.Attributes.Strength);
        Assert.Equal(10u, r.Attributes.Endurance);
        Assert.Equal(10u, r.Attributes.Coordination);
        Assert.Equal(10u, r.Attributes.Quickness);
        Assert.Equal(10u, r.Attributes.Focus);
        Assert.Equal(10u, r.Attributes.Self);
        Assert.Equal(0u, r.Slot);
        Assert.Equal(0u, r.ClassId);
        Assert.Equal(
            (uint)CharacterCreate.SkillAdvancementClassCount,
            (uint)decoded.SkillAdvancementClasses.Length);
        Assert.Equal(
            (uint)ChargenSkillAdvancementClass.Trained,
            decoded.SkillAdvancementClasses[RuntimeCharacterCreationStateFixture.SkillTrainSpecialize]);
        Assert.Equal(
            (uint)ChargenSkillAdvancementClass.Specialized,
            decoded.SkillAdvancementClasses[RuntimeCharacterCreationStateFixture.SkillPresetPrimary]);
        Assert.Equal(
            (uint)ChargenSkillAdvancementClass.Trained,
            decoded.SkillAdvancementClasses[RuntimeCharacterCreationStateFixture.SkillFreeTrained]);
        Assert.Equal("FullChar", r.Name);
        Assert.Equal(1u, r.StartArea);
        Assert.False(r.IsAdmin);
        Assert.False(r.IsEnvoy);

        Assert.Equal(CharacterCreate.ComputeChecksum(r), decoded.Checksum);
    }

    [Theory]
    [InlineData(CharGenVerificationResponse.Code.Pending)]
    [InlineData(CharGenVerificationResponse.Code.NameBanned)]
    [InlineData(CharGenVerificationResponse.Code.Corrupt)]
    [InlineData(CharGenVerificationResponse.Code.DatabaseDown)]
    [InlineData(CharGenVerificationResponse.Code.AdminPrivilegeDenied)]
    [InlineData(CharGenVerificationResponse.Code.Undef)]
    public void Finish_ThenEachOtherRejectionCode_ProducesTheMappedFailureWithNoRosterOrEnterSideEffect(
        CharGenVerificationResponse.Code code)
    {
        (LiveSessionController controller, TestOperations operations, TestHost host, RuntimeGenerationToken generation) =
            StartAwaitingSelection();
        BuildReadyCharacter(controller, generation);
        WorldSession session = operations.Sessions[0];
        session.GameMessageCapture = (_, _) => { };

        Assert.True(controller.Finish(generation).Accepted);

        InvokeProcessDatagram(session, BuildResponsePacket((uint)code, 0u, string.Empty));

        Assert.Single(host.Failed);
        Assert.Equal((uint)code, host.Failed[0].RawCode);
        Assert.Equal(code, host.Failed[0].Code);
        Assert.Equal(code.ToString(), host.Failed[0].Reason);
        Assert.Equal("NewChar", host.Failed[0].AttemptedName);
        Assert.Empty(host.Created);
        Assert.False(controller.IsInWorld);
        Assert.Empty(operations.EnterWorldByGuidCalls);
        Assert.Empty(host.EnteredWorld);
        Assert.DoesNotContain(host.Rosters, r => r.Entries.Any(e => e.Name == "NewChar"));
    }

    [Fact]
    public void Finish_ThenOkResponse_WritesCharacterCreatedEvent_ParsedByTheRealLauncherTailer()
    {
        string path = Path.Combine(
            Path.GetTempPath(), $"acdream-cc7-status-{Guid.NewGuid():N}.jsonl");
        try
        {
            (LiveSessionController controller, TestOperations operations, TestHost host, RuntimeGenerationToken generation) =
                StartAwaitingSelection();
            host.Writer = new SessionStatusWriter(path);
            host.SessionId = "cc7-session";
            BuildReadyCharacter(controller, generation);
            WorldSession session = operations.Sessions[0];
            session.GameMessageCapture = (_, _) => { };

            Assert.True(controller.Finish(generation).Accepted);
            InvokeProcessDatagram(session, BuildResponsePacket(
                (uint)CharGenVerificationResponse.Code.Ok, 0x50001234u, "NewChar"));

            Assert.Single(host.Created);

            var tailer = new StatusFileTailer(path);
            IReadOnlyList<StatusEvent> events = tailer.ReadNewEvents();
            CharacterCreatedStatusEvent created =
                Assert.Single(events.OfType<CharacterCreatedStatusEvent>());
            Assert.Equal("cc7-session", created.SessionId);
            Assert.Equal(0x50001234u, created.Guid);
            Assert.Equal("NewChar", created.Name);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Finish_ThenNameInUseResponse_WritesCreationFailedEvent_ParsedByTheRealLauncherTailer()
    {
        string path = Path.Combine(
            Path.GetTempPath(), $"acdream-cc7-status-{Guid.NewGuid():N}.jsonl");
        try
        {
            (LiveSessionController controller, TestOperations operations, TestHost host, RuntimeGenerationToken generation) =
                StartAwaitingSelection();
            host.Writer = new SessionStatusWriter(path);
            host.SessionId = "cc7-session";
            BuildReadyCharacter(controller, generation);
            WorldSession session = operations.Sessions[0];
            session.GameMessageCapture = (_, _) => { };

            Assert.True(controller.Finish(generation).Accepted);
            InvokeProcessDatagram(session, BuildResponsePacket(
                (uint)CharGenVerificationResponse.Code.NameInUse, 0u, string.Empty));

            Assert.Single(host.Failed);

            var tailer = new StatusFileTailer(path);
            IReadOnlyList<StatusEvent> events = tailer.ReadNewEvents();
            CreationFailedStatusEvent failed =
                Assert.Single(events.OfType<CreationFailedStatusEvent>());
            Assert.Equal("cc7-session", failed.SessionId);
            Assert.Equal((uint)CharGenVerificationResponse.Code.NameInUse, failed.Code);
            Assert.Equal("NameInUse", failed.Reason);
            Assert.Equal("NewChar", failed.Name);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static void InvokeProcessDatagram(WorldSession session, byte[] datagram)
    {
        MethodInfo method = typeof(WorldSession).GetMethod(
            "ProcessDatagram",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(session, [new ReadOnlyMemory<byte>(datagram), null, true]);
    }

    private static byte[] BuildResponseBody(uint code, uint guid, string name)
    {
        var w = new PacketWriter();
        w.WriteUInt32(CharGenVerificationResponse.ResponseOpcode);
        w.WriteUInt32(code);
        if (code == (uint)CharGenVerificationResponse.Code.Ok)
        {
            w.WriteUInt32(guid);
            w.WriteString16L(name);
            w.WriteUInt32(0u);
        }
        return w.ToArray();
    }

    private static byte[] BuildResponsePacket(uint code, uint guid, string name)
    {
        byte[] message = BuildResponseBody(code, guid, name);
        var fragments = new byte[MessageFragmentHeader.Size + message.Length];
        GameMessageFragment.WriteSingleFragment(fragments.AsSpan(), fragmentSequence: 1u, GameMessageGroup.UIQueue, message);
        return PacketCodec.Encode(
            new PacketHeader { Sequence = 1u, Flags = PacketHeaderFlags.BlobFragments },
            fragments,
            outboundIsaac: null);
    }

    private readonly record struct CapturedCreateRequest(
        string AccountName,
        uint Heritage,
        uint Gender,
        uint Template,
        uint Strength,
        string Name,
        uint NumSkills,
        uint[] SkillAdvancementClasses);

    private static CapturedCreateRequest DecodeCreateRequest(ReadOnlySpan<byte> body)
    {
        int pos = 0;
        uint opcode = ReadU32(body, ref pos);
        Assert.Equal(CharacterCreate.Opcode, opcode);
        string accountName = ReadString16L(body, ref pos);
        uint constant = ReadU32(body, ref pos);
        Assert.Equal(1u, constant);
        uint heritage = ReadU32(body, ref pos);
        uint gender = ReadU32(body, ref pos);
        _ = ReadU32(body, ref pos); // eyesStrip
        _ = ReadU32(body, ref pos); // noseStrip
        _ = ReadU32(body, ref pos); // mouthStrip
        _ = ReadU32(body, ref pos); // hairColor
        _ = ReadU32(body, ref pos); // eyeColor
        _ = ReadU32(body, ref pos); // hairStyle
        _ = ReadU32(body, ref pos); // headgearStyle
        _ = ReadU32(body, ref pos); // headgearColor
        _ = ReadU32(body, ref pos); // shirtStyle
        _ = ReadU32(body, ref pos); // shirtColor
        _ = ReadU32(body, ref pos); // trousersStyle
        _ = ReadU32(body, ref pos); // trousersColor
        _ = ReadU32(body, ref pos); // footwearStyle
        _ = ReadU32(body, ref pos); // footwearColor
        for (int i = 0; i < 6; i++)
            _ = ReadF64(body, ref pos); // six shades
        uint template = ReadU32(body, ref pos);
        uint strength = ReadU32(body, ref pos);
        _ = ReadU32(body, ref pos); // endurance
        _ = ReadU32(body, ref pos);
        _ = ReadU32(body, ref pos); // quickness
        _ = ReadU32(body, ref pos); // focus
        _ = ReadU32(body, ref pos); // self
        _ = ReadU32(body, ref pos); // slot
        _ = ReadU32(body, ref pos);
        uint numSkills = ReadU32(body, ref pos);
        var skills = new uint[numSkills];
        for (int i = 0; i < numSkills; i++)
            skills[i] = ReadU32(body, ref pos);
        string name = ReadString16L(body, ref pos);
        return new CapturedCreateRequest(
            accountName, heritage, gender, template, strength, name, numSkills, skills);
    }

    private readonly record struct DecodedFullRequest(
        string AccountName,
        uint Constant,
        CharacterCreate.Request Request,
        uint[] SkillAdvancementClasses,
        uint Checksum);

    private static DecodedFullRequest DecodeCreateRequestFull(ReadOnlySpan<byte> body)
    {
        int pos = 0;
        uint opcode = ReadU32(body, ref pos);
        Assert.Equal(CharacterCreate.Opcode, opcode);
        string accountName = ReadString16L(body, ref pos);
        uint constant = ReadU32(body, ref pos);
        uint heritage = ReadU32(body, ref pos);
        uint gender = ReadU32(body, ref pos);
        uint eyesStrip = ReadU32(body, ref pos);
        uint noseStrip = ReadU32(body, ref pos);
        uint mouthStrip = ReadU32(body, ref pos);
        uint hairColor = ReadU32(body, ref pos);
        uint eyeColor = ReadU32(body, ref pos);
        uint hairStyle = ReadU32(body, ref pos);
        uint headgearStyle = ReadU32(body, ref pos);
        uint headgearColor = ReadU32(body, ref pos);
        uint shirtStyle = ReadU32(body, ref pos);
        uint shirtColor = ReadU32(body, ref pos);
        uint trousersStyle = ReadU32(body, ref pos);
        uint trousersColor = ReadU32(body, ref pos);
        uint footwearStyle = ReadU32(body, ref pos);
        uint footwearColor = ReadU32(body, ref pos);
        double skinShade = ReadF64(body, ref pos);
        double hairShade = ReadF64(body, ref pos);
        double headgearShade = ReadF64(body, ref pos);
        double shirtShade = ReadF64(body, ref pos);
        double trousersShade = ReadF64(body, ref pos);
        double footwearShade = ReadF64(body, ref pos);
        uint template = ReadU32(body, ref pos);
        uint strength = ReadU32(body, ref pos);
        uint endurance = ReadU32(body, ref pos);
        uint coordination = ReadU32(body, ref pos);
        uint quickness = ReadU32(body, ref pos);
        uint focus = ReadU32(body, ref pos);
        uint self = ReadU32(body, ref pos);
        uint slot = ReadU32(body, ref pos);
        uint classId = ReadU32(body, ref pos);
        uint numSkills = ReadU32(body, ref pos);
        var skills = new uint[numSkills];
        for (int i = 0; i < numSkills; i++)
            skills[i] = ReadU32(body, ref pos);
        string name = ReadString16L(body, ref pos);
        uint startArea = ReadU32(body, ref pos);
        uint isAdmin = ReadU32(body, ref pos);
        uint isEnvoy = ReadU32(body, ref pos);
        uint checksum = ReadU32(body, ref pos);

        // Nothing left over, nothing missing — the layout is exhaustive.
        Assert.Equal(body.Length, pos);

        var request = new CharacterCreate.Request(
            heritage,
            gender,
            new CharacterCreate.Appearance(
                eyesStrip,
                noseStrip,
                mouthStrip,
                hairColor,
                eyeColor,
                hairStyle,
                headgearStyle,
                headgearColor,
                shirtStyle,
                shirtColor,
                trousersStyle,
                trousersColor,
                footwearStyle,
                footwearColor,
                skinShade,
                hairShade,
                headgearShade,
                shirtShade,
                trousersShade,
                footwearShade),
            template,
            new CharacterCreate.Attributes(
                strength, endurance, coordination, quickness, focus, self),
            slot,
            classId,
            name,
            startArea,
            isAdmin != 0u,
            isEnvoy != 0u);

        return new DecodedFullRequest(accountName, constant, request, skills, checksum);
    }

    private static uint ReadU32(ReadOnlySpan<byte> body, ref int pos)
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos));
        pos += 4;
        return value;
    }

    private static double ReadF64(ReadOnlySpan<byte> body, ref int pos)
    {
        double value = BinaryPrimitives.ReadDoubleLittleEndian(body.Slice(pos));
        pos += 8;
        return value;
    }

    private static string ReadString16L(ReadOnlySpan<byte> body, ref int pos)
    {
        ushort len = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(pos));
        string value = Encoding.ASCII.GetString(body.Slice(pos + 2, len));
        int recordSize = 2 + len;
        int padding = (4 - (recordSize & 3)) & 3;
        pos += recordSize + padding;
        return value;
    }
}
