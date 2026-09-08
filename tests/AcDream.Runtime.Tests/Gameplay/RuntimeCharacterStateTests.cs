using AcDream.Core.Items;
using AcDream.Core.Net.Messages;
using AcDream.Core.Properties;
using AcDream.Core.Spells;
using AcDream.Core.Player;
using AcDream.Runtime;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class RuntimeCharacterStateTests
{

    [Fact]
    public void IsOlthoiPlayer_FalseByDefault_NoHeritageParsedYet()
    {
        using var state = new RuntimeCharacterState();

        Assert.False(state.IsOlthoiPlayer);
    }

    [Theory]
    [InlineData(12)] // HeritageGroup.Olthoi
    [InlineData(13)] // HeritageGroup.OlthoiAcid
    public void IsOlthoiPlayer_TrueForOlthoiHeritageGroups(int heritageGroup)
    {
        using var state = new RuntimeCharacterState();
        var properties = new PropertyBundle();
        properties.Ints[(uint)PropertyInt.HeritageGroup] = heritageGroup;

        state.LocalPlayer.OnProperties(properties);

        Assert.True(state.IsOlthoiPlayer);
    }

    [Fact]
    public void IsOlthoiPlayer_FalseForNonOlthoiHeritage()
    {
        using var state = new RuntimeCharacterState();
        var properties = new PropertyBundle();
        properties.Ints[(uint)PropertyInt.HeritageGroup] = 1; // Aluvian

        state.LocalPlayer.OnProperties(properties);

        Assert.False(state.IsOlthoiPlayer);
    }

    [Fact]
    public void OwnsOneCoupledSpellbookAndLocalPlayerGraph()
    {
        SpellTable table = SpellTable.Create(
        [
            new SpellMetadata(
                SpellId: 1u,
                Name: "Test",
                School: "Life",
                Family: 0u,
                IconId: 0u,
                SpellWords: "",
                Duration: 60f,
                ManaCost: 0,
                IsDebuff: false,
                IsFellowship: false,
                Description: "",
                SortKey: 0,
                Difficulty: 0,
                Flags: 0u,
                Generation: 1,
                IsFastWindup: false,
                IsOffensive: false,
                IsUntargeted: false,
                Speed: 0f,
                CasterEffect: 0u,
                TargetEffect: 0u,
                TargetMask: 0u,
                SpellType: 0)
        ]);
        using var state = new RuntimeCharacterState(table);
        state.LocalPlayer.OnAttributeUpdate(
            atType: 2u,
            ranks: 90u,
            start: 10u,
            xp: 0u);
        state.LocalPlayer.OnVitalUpdate(
            vitalId: 7u,
            ranks: 50u,
            start: 50u,
            xp: 0u,
            current: 150u);

        state.Spellbook.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            SpellId: 1u,
            LayerId: 1u,
            Duration: 60f,
            CasterGuid: 2u,
            Bucket: 2u,
            StatModType: (uint)EnchantmentMath.EnchantmentTypeFlag.SecondAtt,
            StatModKey: EnchantmentMath.StatKey.MaxHealth,
            StatModValue: 25f));
        state.Spellbook.SetDesiredComponent(0x68000001u, 12u);

        Assert.Equal(175u, state.LocalPlayer.GetMaxApprox(
            AcDream.Core.Player.LocalPlayerState.VitalKind.Health));
        Assert.Equal(12u, state.Spellbook.DesiredComponents[0x68000001u]);
    }

    [Fact]
    public void InstallsImmutableMetadataOnceOnTheCanonicalSpellbook()
    {
        using var state = new RuntimeCharacterState();
        SpellTable table = SpellTable.Create(Array.Empty<SpellMetadata>());

        state.InstallSpellMetadata(table);
        state.InstallSpellMetadata(table);

        Assert.Same(table, state.Spellbook.Metadata);
        Assert.Throws<InvalidOperationException>(
            () => state.InstallSpellMetadata(SpellTable.Empty));
    }

    [Fact]
    public void ResetMethodsPreserveTheOtherHalfOfTheLifetimeGroup()
    {
        using var state = new RuntimeCharacterState();
        state.Spellbook.OnSpellLearned(7u);
        state.LocalPlayer.OnVitalUpdate(7u, 1u, 9u, 0u, 10u);

        state.ResetSpellbook();

        Assert.False(state.Spellbook.Knows(7u));
        Assert.NotNull(state.LocalPlayer.Get(
            AcDream.Core.Player.LocalPlayerState.VitalKind.Health));

        state.Spellbook.OnSpellLearned(8u);
        state.ResetLocalPlayer();

        Assert.True(state.Spellbook.Knows(8u));
        Assert.Null(state.LocalPlayer.Get(
            AcDream.Core.Player.LocalPlayerState.VitalKind.Health));
    }

    [Fact]
    public void IndependentRuntimeInstancesNeverShareCharacterState()
    {
        using var first = new RuntimeCharacterState();
        using var second = new RuntimeCharacterState();

        first.Spellbook.OnSpellLearned(7u);
        first.LocalPlayer.OnVitalUpdate(7u, 1u, 9u, 0u, 10u);

        Assert.False(second.Spellbook.Knows(7u));
        Assert.Null(second.LocalPlayer.Get(
            AcDream.Core.Player.LocalPlayerState.VitalKind.Health));
    }

    [Fact]
    public void DisposalReportsObserverFailuresAfterTerminalConvergence()
    {
        var state = new RuntimeCharacterState();
        state.Spellbook.OnSpellLearned(7u);
        state.LocalPlayer.OnVitalUpdate(7u, 1u, 9u, 0u, 10u);
        bool failSpellbook = true;
        bool failCharacter = true;
        state.Spellbook.SpellbookChanged += () =>
        {
            if (failSpellbook)
            {
                failSpellbook = false;
                throw new InvalidOperationException("spellbook");
            }
        };
        state.LocalPlayer.CharacterChanged += () =>
        {
            if (failCharacter)
            {
                failCharacter = false;
                throw new InvalidOperationException("character");
            }
        };

        AggregateException error = Assert.Throws<AggregateException>(
            state.Dispose);

        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.True(state.IsDisposed);
        Assert.False(state.Spellbook.Knows(7u));
        Assert.Null(state.LocalPlayer.Get(
            AcDream.Core.Player.LocalPlayerState.VitalKind.Health));
        Assert.True(state.CaptureOwnership().IsConverged);

        state.Dispose();

        Assert.True(state.IsDisposed);
    }

    [Fact]
    public void OwnsRetailCharacterOptionsAndMovementSkillProjection()
    {
        using var state = new RuntimeCharacterState();

        Assert.Equal(
            RuntimeCharacterOptionsState.DefaultOptions1,
            state.Options.Options1);
        Assert.Equal(
            RuntimeCharacterOptionsState.DefaultOptions2,
            state.Options.Options2);
        Assert.False(state.MovementSkills.IsComplete);

        state.Options.Replace(0x04000000u, 0x12345678u);
        state.MovementSkills.Update(runSkill: 210, jumpSkill: -1);
        state.MovementSkills.Update(runSkill: -1, jumpSkill: 165);

        Assert.True(state.Options.DragItemOnPlayerOpensSecureTrade);
        Assert.Equal(0x12345678u, state.Options.Options2);
        Assert.Equal(
            new RuntimeMovementSkillSnapshot(
                210,
                165,
                state.MovementSkills.Revision),
            state.MovementSkills.Snapshot);
        Assert.True(state.MovementSkills.IsComplete);

        state.ResetSession();

        Assert.Equal(
            RuntimeCharacterOptionsState.DefaultOptions1,
            state.Options.Options1);
        Assert.Equal(
            RuntimeCharacterOptionsState.DefaultOptions2,
            state.Options.Options2);
        Assert.Equal(-1, state.MovementSkills.RunSkill);
        Assert.Equal(-1, state.MovementSkills.JumpSkill);
    }


    [Theory]
    [InlineData(CharacterOptionId.ListenToGeneralChat, PlayerDescriptionParser.CharacterOptions2.HearGeneralChat)]
    [InlineData(CharacterOptionId.ListenToTradeChat, PlayerDescriptionParser.CharacterOptions2.HearTradeChat)]
    [InlineData(CharacterOptionId.ListenToLFGChat, PlayerDescriptionParser.CharacterOptions2.HearLFGChat)]
    [InlineData(CharacterOptionId.ListenToRoleplayChat, PlayerDescriptionParser.CharacterOptions2.HearRoleplayChat)]
    [InlineData(CharacterOptionId.ListenToSocietyChat, PlayerDescriptionParser.CharacterOptions2.HearSocietyChat)]
    public void SetOptionBit_Options2Ids_ToggleOnlyTheirOwnBit(
        CharacterOptionId optionId, PlayerDescriptionParser.CharacterOptions2 bit)
    {
        var options = new RuntimeCharacterOptionsState();
        options.Replace(options.Options1, 0u);

        options.SetOptionBit((uint)optionId, true);
        Assert.Equal((uint)bit, options.Options2 & (uint)bit);
        Assert.Equal(RuntimeCharacterOptionsState.DefaultOptions1, options.Options1);

        options.SetOptionBit((uint)optionId, false);
        Assert.Equal(0u, options.Options2 & (uint)bit);
    }

    [Fact]
    public void SetOptionBit_AllegianceId_TogglesOptions1NotOptions2()
    {
        var options = new RuntimeCharacterOptionsState();
        options.Replace(0u, options.Options2); // HearAllegianceChat off

        options.SetOptionBit((uint)CharacterOptionId.ListenToAllegianceChat, true);
        Assert.Equal(
            (uint)PlayerDescriptionParser.CharacterOptions1.HearAllegianceChat,
            options.Options1 & (uint)PlayerDescriptionParser.CharacterOptions1.HearAllegianceChat);

        options.SetOptionBit((uint)CharacterOptionId.ListenToAllegianceChat, false);
        Assert.Equal(
            0u,
            options.Options1 & (uint)PlayerDescriptionParser.CharacterOptions1.HearAllegianceChat);
    }

    [Fact]
    public void SetOptionBit_UnrecognizedId_IsANoOp()
    {
        var options = new RuntimeCharacterOptionsState();
        uint before1 = options.Options1;
        uint before2 = options.Options2;
        long beforeRevision = options.Revision;

        options.SetOptionBit(0xFFFFu, true);

        Assert.Equal(before1, options.Options1);
        Assert.Equal(before2, options.Options2);
        Assert.Equal(beforeRevision, options.Revision);
    }


    [Theory]
    [InlineData(CharacterOptionId.AutoTarget)]
    [InlineData(CharacterOptionId.ViewCombatTarget)]
    [InlineData(CharacterOptionId.ListenToGeneralChat)]
    [InlineData(CharacterOptionId.DisableDistanceFog)]
    public void GetOptionBit_RoundTripsWithSetOptionBit(CharacterOptionId id)
    {
        var options = new RuntimeCharacterOptionsState();

        options.SetOptionBit((uint)id, true);
        Assert.True(options.GetOptionBit(id));
        Assert.True(options.GetOptionBit((uint)id));

        options.SetOptionBit((uint)id, false);
        Assert.False(options.GetOptionBit(id));
    }

    [Fact]
    public void GetOptionBit_UnrecognizedId_ReturnsFalse()
    {
        var options = new RuntimeCharacterOptionsState();
        Assert.False(options.GetOptionBit(0xFFFFu));
    }

    [Fact]
    public void GetOptionBit_ReflectsReplace_NotJustSetOptionBit()
    {
        var options = new RuntimeCharacterOptionsState();
        Assert.True(options.GetOptionBit(CharacterOptionId.ListenToGeneralChat)); // default ON

        options.Replace(options.Options1, 0u); // every Options2 bit off, incl. ListenToGeneralChat

        Assert.False(options.GetOptionBit(CharacterOptionId.ListenToGeneralChat));
    }


    [Fact]
    public void TrySetOption_AutoSaveId_WritesLocallyThenSendsImmediately_NeverDirties()
    {
        var options = new RuntimeCharacterOptionsState();
        options.Replace(options.Options1, 0u);
        var sent = new List<(uint OptionId, bool Value)>();

        bool accepted = options.TrySetOption(
            (uint)CharacterOptionId.ListenToGeneralChat,
            true,
            sendAutoSave: (id, value) => sent.Add((id, value)));

        Assert.True(accepted);
        Assert.Equal(
            (uint)PlayerDescriptionParser.CharacterOptions2.HearGeneralChat,
            options.Options2
                & (uint)PlayerDescriptionParser.CharacterOptions2.HearGeneralChat);
        Assert.Equal([((uint)CharacterOptionId.ListenToGeneralChat, true)], sent);
        Assert.False(options.IsDirty);
        Assert.Null(options.FirstDirtiedAt);
    }

    [Fact]
    public void TrySetOption_BatchedId_WritesLocallyAndMarksDirty_NeverSends()
    {
        var options = new RuntimeCharacterOptionsState();
        var sent = new List<(uint OptionId, bool Value)>();

        bool accepted = options.TrySetOption(
            (uint)CharacterOptionId.AutoTarget,
            false,
            sendAutoSave: (id, value) => sent.Add((id, value)));

        Assert.True(accepted);
        Assert.Equal(0u, options.Options1 & 0x00002000u);
        Assert.Empty(sent);
        Assert.True(options.IsDirty);
        Assert.NotNull(options.FirstDirtiedAt);
    }

    [Fact]
    public void TrySetOption_UnchangedValue_IsANoOp_MatchingRetailEarlyReturn()
    {
        var options = new RuntimeCharacterOptionsState();
        var sent = new List<(uint, bool)>();
        bool accepted = options.TrySetOption(
            (uint)CharacterOptionId.AutoTarget,
            true,
            sendAutoSave: (id, value) => sent.Add((id, value)));

        Assert.True(accepted);
        Assert.Empty(sent);
        Assert.False(options.IsDirty);
    }

    [Fact]
    public void TrySetOption_UnknownId_ReturnsFalse_NeverInvokesCallback()
    {
        var options = new RuntimeCharacterOptionsState();
        bool invoked = false;

        bool accepted = options.TrySetOption(0x35u, true, (_, _) => invoked = true);

        Assert.False(accepted);
        Assert.False(invoked);
        Assert.False(options.IsDirty);
    }


    [Fact]
    public void TrySetOption_TurningOnIgnoreFellowshipRequests_ClearsAutoAccept_ClearSendsBeforePrimary()
    {
        var options = new RuntimeCharacterOptionsState();
        options.TrySetOption(
            (uint)CharacterOptionId.FellowshipAutoAcceptRequests, true, (_, _) => { });
        var sent = new List<(uint OptionId, bool Value)>();

        bool accepted = options.TrySetOption(
            (uint)CharacterOptionId.IgnoreFellowshipRequests,
            true,
            sendAutoSave: (id, value) => sent.Add((id, value)));

        Assert.True(accepted);
        Assert.Equal(
            [
                ((uint)CharacterOptionId.FellowshipAutoAcceptRequests, false),
                ((uint)CharacterOptionId.IgnoreFellowshipRequests, true),
            ],
            sent);
        Assert.NotEqual(0u, options.Options1 & 0x00000008u); // IgnoreFellowshipRequests set
        Assert.Equal(0u, options.Options1 & 0x20000000u);
    }

    [Fact]
    public void TrySetOption_TurningOnAutoAcceptFellowship_ClearsIgnoreRequests_ClearSendsBeforePrimary()
    {
        var options = new RuntimeCharacterOptionsState();
        options.TrySetOption(
            (uint)CharacterOptionId.IgnoreFellowshipRequests, true, (_, _) => { });
        var sent = new List<(uint OptionId, bool Value)>();

        bool accepted = options.TrySetOption(
            (uint)CharacterOptionId.FellowshipAutoAcceptRequests,
            true,
            sendAutoSave: (id, value) => sent.Add((id, value)));

        Assert.True(accepted);
        Assert.Equal(
            [
                ((uint)CharacterOptionId.IgnoreFellowshipRequests, false),
                ((uint)CharacterOptionId.FellowshipAutoAcceptRequests, true),
            ],
            sent);
        Assert.NotEqual(0u, options.Options1 & 0x20000000u); // AutoAccept set
        Assert.Equal(0u, options.Options1 & 0x00000008u);
    }

    [Fact]
    public void TrySetOption_TurningOnFellowshipOption_WhenTheOtherIsAlreadyOff_SendsOnlyThePrimary()
    {
        var options = new RuntimeCharacterOptionsState();
        options.TrySetOption(
            (uint)CharacterOptionId.IgnoreFellowshipRequests, false, (_, _) => { });
        var sent = new List<(uint OptionId, bool Value)>();

        bool accepted = options.TrySetOption(
            (uint)CharacterOptionId.IgnoreFellowshipRequests,
            true,
            sendAutoSave: (id, value) => sent.Add((id, value)));

        Assert.True(accepted);
        Assert.Equal([((uint)CharacterOptionId.IgnoreFellowshipRequests, true)], sent);
    }

    [Fact]
    public void TrySetOption_TurningOffAFellowshipOption_NeverTriggersTheClear()
    {
        var options = new RuntimeCharacterOptionsState();
        options.SetOptionBit((uint)CharacterOptionId.IgnoreFellowshipRequests, true);
        options.SetOptionBit((uint)CharacterOptionId.FellowshipAutoAcceptRequests, true);
        var sent = new List<(uint OptionId, bool Value)>();

        bool accepted = options.TrySetOption(
            (uint)CharacterOptionId.IgnoreFellowshipRequests,
            false,
            sendAutoSave: (id, value) => sent.Add((id, value)));

        Assert.True(accepted);
        Assert.Equal([((uint)CharacterOptionId.IgnoreFellowshipRequests, false)], sent);
        Assert.NotEqual(0u, options.Options1 & 0x20000000u); // AutoAccept untouched (still on)
    }

    [Fact]
    public void MarkDirty_OnlySecondCallDoesNotPushOutFirstDirtiedAt()
    {
        var clock = new ManualTimeProvider();
        var options = new RuntimeCharacterOptionsState(clock);

        options.TrySetOption(
            (uint)CharacterOptionId.AutoTarget, false, (_, _) => { });
        DateTimeOffset? firstStamp = options.FirstDirtiedAt;
        Assert.NotNull(firstStamp);

        clock.Advance(TimeSpan.FromSeconds(10));
        options.TrySetOption(
            (uint)CharacterOptionId.ShowTooltips, false, (_, _) => { });

        Assert.Equal(firstStamp, options.FirstDirtiedAt);
    }


    [Fact]
    public void TryFlush_RefusesBeforeServerSeed_EvenWhenDirty_ThenSucceedsAfterSeed()
    {
        var options = new RuntimeCharacterOptionsState();
        Assert.False(options.HasServerSeed);

        options.TrySetOption(
            (uint)CharacterOptionId.AutoTarget, false, (_, _) => { });
        Assert.True(options.IsDirty);

        int flushes = 0;
        Assert.False(options.TryFlush(() => flushes++));
        Assert.Equal(0, flushes);
        Assert.True(options.IsDirty);

        options.Replace(options.Options1, options.Options2);
        Assert.True(options.HasServerSeed);
        Assert.False(options.IsDirty);

        options.TrySetOption(
            (uint)CharacterOptionId.AutoTarget, true, (_, _) => { });
        Assert.True(options.TryFlush(() => flushes++));
        Assert.Equal(1, flushes);
    }

    [Fact]
    public void TryFlushIfAutoSaveDue_RefusesBeforeServerSeed_EvenAtThreshold()
    {
        var clock = new ManualTimeProvider();
        var options = new RuntimeCharacterOptionsState(clock);
        options.TrySetOption(
            (uint)CharacterOptionId.AutoTarget, false, (_, _) => { });

        clock.Advance(RuntimeCharacterOptionsState.AutoSaveDelay + TimeSpan.FromSeconds(1));

        int flushes = 0;
        Assert.False(options.TryFlushIfAutoSaveDue(() => flushes++));
        Assert.Equal(0, flushes);
        Assert.True(options.IsDirty);
    }

    [Fact]
    public void ReconnectSequence_ResetSessionClearsSeed_NewReplaceUnblocksFlushAgain()
    {
        var options = new RuntimeCharacterOptionsState();
        options.Replace(options.Options1, options.Options2); // first session's seed
        options.TrySetOption(
            (uint)CharacterOptionId.AutoTarget, false, (_, _) => { });

        int flushes = 0;
        Assert.True(options.TryFlush(() => flushes++));
        Assert.Equal(1, flushes);

        options.ResetSession();
        Assert.False(options.HasServerSeed);

        // Anything that dirties the module BEFORE the new session's
        // PlayerDescription arrives must not be flushable yet.
        options.TrySetOption(
            (uint)CharacterOptionId.AutoTarget, false, (_, _) => { });
        Assert.True(options.IsDirty);
        Assert.False(options.TryFlush(() => flushes++));
        Assert.Equal(1, flushes);
        Assert.True(options.IsDirty);

        options.Replace(options.Options1, options.Options2);
        Assert.True(options.HasServerSeed);
        Assert.False(options.IsDirty);
        options.TrySetOption(
            (uint)CharacterOptionId.ShowTooltips, false, (_, _) => { });
        Assert.True(options.TryFlush(() => flushes++));
        Assert.Equal(2, flushes);
    }


    [Fact]
    public void Replace_ClearsDirtyState_ServerTruthSupersedesPendingLocalIntent()
    {
        var options = new RuntimeCharacterOptionsState();
        options.TrySetOption(
            (uint)CharacterOptionId.AutoTarget, false, (_, _) => { });
        Assert.True(options.IsDirty);
        Assert.NotNull(options.FirstDirtiedAt);

        options.Replace(0x11111111u, 0x22222222u);

        Assert.False(options.IsDirty);
        Assert.Null(options.FirstDirtiedAt);
        Assert.Equal(0x11111111u, options.Options1);
        Assert.Equal(0x22222222u, options.Options2);
    }


    [Fact]
    public async Task TryFlush_ReleasesTheDirtyGate_DuringTheCallback_SoAConcurrentMarkDirtyDoesNotBlock()
    {
        var options = new RuntimeCharacterOptionsState();
        options.Replace(options.Options1, options.Options2);
        options.TrySetOption(
            (uint)CharacterOptionId.AutoTarget, false, (_, _) => { });

        using var callbackEntered = new ManualResetEventSlim(false);
        using var releaseCallback = new ManualResetEventSlim(false);

        Task<bool> flushTask = Task.Run(() =>
            options.TryFlush(() =>
            {
                callbackEntered.Set();
                releaseCallback.Wait(TimeSpan.FromSeconds(10));
            }));

        Assert.True(callbackEntered.Wait(TimeSpan.FromSeconds(5)));

        Task probe = Task.Run(options.MarkDirty);
        Task probeCompletion = await Task.WhenAny(probe, Task.Delay(TimeSpan.FromSeconds(2)));
        bool probeCompletedPromptly = ReferenceEquals(probeCompletion, probe);

        releaseCallback.Set();
        bool flushed = await flushTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(flushed);
        Assert.True(probeCompletedPromptly);

        Assert.True(options.IsDirty);
    }

    [Fact]
    public void TryFlush_KeepsModuleDirty_WhenADirtyingChangeLandsInsideTheCallback()
    {
        var options = new RuntimeCharacterOptionsState();
        options.Replace(options.Options1, options.Options2);
        options.TrySetOption(
            (uint)CharacterOptionId.AutoTarget, false, (_, _) => { });
        Assert.True(options.IsDirty);

        Assert.True(options.TryFlush(options.MarkDirty));
        Assert.True(options.IsDirty);

        // The retained dirty state flushes normally afterwards.
        Assert.True(options.TryFlush(() => { }));
        Assert.False(options.IsDirty);
    }

    [Fact]
    public void Replace_WithoutServerSeedArming_DoesNotAuthorizeFlush()
    {
        var options = new RuntimeCharacterOptionsState();
        options.Replace(0u, 0u, armServerSeed: false);
        Assert.False(options.HasServerSeed);

        options.TrySetOption(
            (uint)CharacterOptionId.AutoTarget, true, (_, _) => { });
        Assert.True(options.IsDirty);
        Assert.False(options.TryFlush(() => throw new InvalidOperationException(
            "a truncated-trailer seed must never authorize a blob flush")));

        options.Replace(0x50C4A54Au, 0x00948700u);
        Assert.True(options.HasServerSeed);
        options.TrySetOption(
            (uint)CharacterOptionId.AutoTarget, false, (_, _) => { });
        Assert.True(options.TryFlush(() => { }));
    }

    [Fact]
    public void TryFlush_PreservesDirtyState_WhenTheCallbackThrows()
    {
        var options = new RuntimeCharacterOptionsState();
        options.Replace(options.Options1, options.Options2);
        options.TrySetOption(
            (uint)CharacterOptionId.AutoTarget, false, (_, _) => { });
        Assert.True(options.IsDirty);

        Assert.Throws<InvalidOperationException>(() =>
            options.TryFlush(() => throw new InvalidOperationException("network down")));

        Assert.True(options.IsDirty);
    }

    [Fact]
    public void TryFlush_NoOpWhenClean_FlushesAndClearsWhenDirty()
    {
        var options = new RuntimeCharacterOptionsState();
        int cleanFlushes = 0;
        Assert.False(options.TryFlush(() => cleanFlushes++));
        Assert.Equal(0, cleanFlushes);

        options.Replace(options.Options1, options.Options2);
        options.TrySetOption(
            (uint)CharacterOptionId.AutoTarget, false, (_, _) => { });
        Assert.True(options.IsDirty);

        int dirtyFlushes = 0;
        Assert.True(options.TryFlush(() => dirtyFlushes++));
        Assert.Equal(1, dirtyFlushes);
        Assert.False(options.IsDirty);
        Assert.Null(options.FirstDirtiedAt);

        Assert.False(options.TryFlush(() => dirtyFlushes++));
        Assert.Equal(1, dirtyFlushes);
    }

    [Fact]
    public void TryFlushIfAutoSaveDue_DoesNotFireBeforeThreshold_FiresAtThreshold()
    {
        var clock = new ManualTimeProvider();
        var options = new RuntimeCharacterOptionsState(clock);
        options.Replace(options.Options1, options.Options2);
        options.TrySetOption(
            (uint)CharacterOptionId.AutoTarget, false, (_, _) => { });

        int flushes = 0;
        clock.Advance(RuntimeCharacterOptionsState.AutoSaveDelay - TimeSpan.FromSeconds(1));
        Assert.False(options.TryFlushIfAutoSaveDue(() => flushes++));
        Assert.True(options.IsDirty);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(options.TryFlushIfAutoSaveDue(() => flushes++));
        Assert.Equal(1, flushes);
        Assert.False(options.IsDirty);
    }

    [Fact]
    public void ResetSession_ClearsDirtyState()
    {
        var options = new RuntimeCharacterOptionsState();
        options.TrySetOption(
            (uint)CharacterOptionId.AutoTarget, false, (_, _) => { });
        Assert.True(options.IsDirty);

        options.ResetSession();

        Assert.False(options.IsDirty);
        Assert.Null(options.FirstDirtiedAt);
    }


    [Fact]
    public void CaptureOwnership_OptionsAreClean_ReflectsOptionsIsDirty_EvenWhenBitsReturnToDefault()
    {
        using var state = new RuntimeCharacterState();
        Assert.True(state.CaptureOwnership().OptionsAreClean);

        state.Options.TrySetOption(
            (uint)CharacterOptionId.AutoTarget, false, (_, _) => { });
        state.Options.TrySetOption(
            (uint)CharacterOptionId.AutoTarget, true, (_, _) => { });

        Assert.Equal(RuntimeCharacterOptionsState.DefaultOptions1, state.Options.Options1);
        Assert.True(state.Options.IsDirty);
        Assert.False(state.CaptureOwnership().OptionsAreClean);

        state.ResetSession();

        Assert.True(state.CaptureOwnership().OptionsAreClean);
    }

    [Fact]
    public void RuntimeCharacterOwnershipSnapshot_IsConverged_RequiresOptionsAreClean()
    {
        var converged = new RuntimeCharacterOwnershipSnapshot(
            IsDisposed: true,
            InternalSubscriptionsAttached: false,
            LearnedSpellCount: 0,
            ActiveEnchantmentCount: 0,
            DesiredComponentCount: 0,
            FavoriteSpellCount: 0,
            VitalCount: 0,
            AttributeCount: 0,
            SkillCount: 0,
            PositionCount: 0,
            PropertyCount: 0,
            OptionsAreDefaults: true,
            MovementSkillsAreReset: true,
            AutonomyIsDefault: true,
            OptionsAreClean: true);
        Assert.True(converged.IsConverged);

        RuntimeCharacterOwnershipSnapshot dirty = converged with { OptionsAreClean = false };
        Assert.False(dirty.IsConverged);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }


    [Fact]
    public void UpdateMovementSkillBase_NoEnchantments_PushesBaseUnchanged()
    {
        using var state = new RuntimeCharacterState();
        state.UpdateMovementSkillBase(runSkillBase: 210, jumpSkillBase: 165);

        Assert.Equal(210, state.MovementSkills.RunSkill);
        Assert.Equal(165, state.MovementSkills.JumpSkill);
    }

    [Fact]
    public void UpdateMovementSkillBase_VitaeActive_AppliesMultiplierToPushedSkill()
    {
        SpellTable table = SpellTableWith((1u, "Vitae", 0u));
        using var state = new RuntimeCharacterState(table);
        state.Spellbook.OnEnchantmentAdded(MakeVitae(spellId: 1u, val: 0.9f));

        state.UpdateMovementSkillBase(runSkillBase: 200, jumpSkillBase: 100);

        Assert.Equal(180, state.MovementSkills.RunSkill);
        Assert.Equal(90, state.MovementSkills.JumpSkill);
    }

    [Fact]
    public void EnchantmentsChanged_AfterBaseAlreadyPushed_RecomputesWithoutFreshBase()
    {
        SpellTable table = SpellTableWith((1u, "Vitae", 0u));
        using var state = new RuntimeCharacterState(table);
        state.UpdateMovementSkillBase(runSkillBase: 200, jumpSkillBase: 100);
        Assert.Equal(200, state.MovementSkills.RunSkill);

        state.Spellbook.OnEnchantmentAdded(MakeVitae(spellId: 1u, val: 0.95f));

        Assert.Equal(190, state.MovementSkills.RunSkill);
    }

    [Fact]
    public void EnchantmentsChanged_SkillSpecificBuff_AppliesToMatchingSkillOnly()
    {
        SpellTable table = SpellTableWith((77u, "Run Buff", 0u));
        using var state = new RuntimeCharacterState(table);
        state.UpdateMovementSkillBase(runSkillBase: 200, jumpSkillBase: 100);

        state.Spellbook.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            SpellId: 77u,
            LayerId: 1u,
            Duration: 60f,
            CasterGuid: 0u,
            StatModType: (uint)EnchantmentMath.EnchantmentTypeFlag.Skill,
            StatModKey: RuntimeCharacterState.RunSkillId,
            StatModValue: 1.5f,
            Bucket: 1u));

        Assert.Equal(300, state.MovementSkills.RunSkill);   // 200 * 1.5
        Assert.Equal(100, state.MovementSkills.JumpSkill);  // untouched
    }

    [Fact]
    public void MovementSkillAugmentations_UseSameRetailChainAsCharacterSheet()
    {
        using var state = new RuntimeCharacterState();
        state.LocalPlayer.OnSkillUpdate(
            RuntimeCharacterState.RunSkillId,
            ranks: 0u,
            status: 3u,
            xp: 0u,
            init: 0u,
            resistance: 0u,
            lastUsed: 0d,
            formulaBonus: 200u);
        state.LocalPlayer.OnSkillUpdate(
            RuntimeCharacterState.JumpSkillId,
            ranks: 0u,
            status: 2u,
            xp: 0u,
            init: 0u,
            resistance: 0u,
            lastUsed: 0d,
            formulaBonus: 100u);
        state.UpdateMovementSkillBase(runSkillBase: 200, jumpSkillBase: 100);

        state.UpdateMovementSkillAugmentations(
            new PlayerSkillMath.AugmentationBonuses(
                AllSkills: 2,
                JackOfAllTrades: true,
                SkilledSpecialized: 3,
                SkilledMelee: false,
                SkilledMissile: false,
                SkilledMagic: false));

        Assert.Equal(213, state.MovementSkills.RunSkill);
        Assert.Equal(107, state.MovementSkills.JumpSkill);
    }

    [Fact]
    public void ResetSession_ClearsBurdenStaminaAndSkillBase()
    {
        using var state = new RuntimeCharacterState();
        state.UpdateMovementSkillBase(runSkillBase: 200, jumpSkillBase: 100);
        state.UpdateMovementSkillAugmentations(
            new PlayerSkillMath.AugmentationBonuses(
                AllSkills: 2,
                JackOfAllTrades: true,
                SkilledSpecialized: 3,
                SkilledMelee: false,
                SkilledMissile: false,
                SkilledMagic: false));
        state.MovementSkills.UpdateBurden(1.5f);
        state.MovementSkills.UpdateStamina(0);

        state.ResetSession();

        Assert.Equal(-1, state.MovementSkills.RunSkill);
        Assert.Equal(-1, state.MovementSkills.JumpSkill);
        Assert.Equal(0f, state.MovementSkills.Burden);
        Assert.Equal(-1, state.MovementSkills.CurrentStamina);
        Assert.True(state.CaptureOwnership().MovementSkillsAreReset);

        state.UpdateMovementSkillBase(runSkillBase: 200, jumpSkillBase: 100);
        Assert.Equal(200, state.MovementSkills.RunSkill);
    }

    [Fact]
    public void CaptureOwnership_BurdenOrStaminaLeftoverBreaksMovementSkillsReset()
    {
        using var state = new RuntimeCharacterState();
        Assert.True(state.CaptureOwnership().MovementSkillsAreReset);

        state.MovementSkills.UpdateBurden(0.5f);
        Assert.False(state.CaptureOwnership().MovementSkillsAreReset);

        state.MovementSkills.UpdateBurden(0f);
        Assert.True(state.CaptureOwnership().MovementSkillsAreReset);

        state.MovementSkills.UpdateStamina(80);
        Assert.False(state.CaptureOwnership().MovementSkillsAreReset);
    }


    [Fact]
    public void AutonomyLevel_DefaultsToFullAndMirrorsRetailUsePositionFromServer()
    {
        using var state = new RuntimeCharacterState();

        Assert.Equal(RuntimeCharacterState.FullAutonomyLevel, state.AutonomyLevel);
        Assert.False(state.UsePositionFromServer);
        Assert.True(state.CaptureOwnership().AutonomyIsDefault);

        Assert.True(state.TrySetAutonomyLevel(0u));
        Assert.Equal(0u, state.AutonomyLevel);
        Assert.True(state.UsePositionFromServer);
        Assert.False(state.CaptureOwnership().AutonomyIsDefault);

        Assert.True(state.TrySetAutonomyLevel(1u));
        Assert.True(state.UsePositionFromServer);

        Assert.False(state.TrySetAutonomyLevel(3u));
        Assert.Equal(1u, state.AutonomyLevel);

        Assert.True(state.TrySetAutonomyLevel(RuntimeCharacterState.FullAutonomyLevel));
        Assert.False(state.UsePositionFromServer);
        Assert.True(state.CaptureOwnership().AutonomyIsDefault);
    }

    [Fact]
    public void ResetSession_RestoresAutonomyLevelToFull()
    {
        using var state = new RuntimeCharacterState();
        Assert.True(state.TrySetAutonomyLevel(0u));
        Assert.True(state.UsePositionFromServer);

        state.ResetSession();

        Assert.Equal(RuntimeCharacterState.FullAutonomyLevel, state.AutonomyLevel);
        Assert.False(state.UsePositionFromServer);
        Assert.True(state.CaptureOwnership().AutonomyIsDefault);
    }


    [Fact]
    public void Titles_IsOwnedAsASiblingOfOptionsAndMovementSkills()
    {
        using var state = new RuntimeCharacterState();

        state.Titles.ReplaceTable(13u, [1u, 5u, 13u]);

        Assert.Equal(13u, state.Titles.DisplayTitleId);
        Assert.Equal(3, state.Titles.EarnedTitleIds.Count);
        Assert.Equal(3, state.CaptureOwnership().TitleCount);
        Assert.False(state.CaptureOwnership().DisplayTitleIsDefault);
    }

    [Fact]
    public void ResetSession_ClearsTitles()
    {
        using var state = new RuntimeCharacterState();
        state.Titles.ReplaceTable(13u, [1u, 5u, 13u]);

        state.ResetSession();

        Assert.Equal(0u, state.Titles.DisplayTitleId);
        Assert.Empty(state.Titles.EarnedTitleIds);
        Assert.True(state.CaptureOwnership().TitleCount == 0);
        Assert.True(state.CaptureOwnership().DisplayTitleIsDefault);
        Assert.True(state.CaptureOwnership().IsConverged is false); // IsDisposed still false
    }

    [Fact]
    public void Dispose_ClearsTitlesAndConverges()
    {
        var state = new RuntimeCharacterState();
        state.Titles.ReplaceTable(13u, [1u, 5u, 13u]);

        state.Dispose();

        Assert.Equal(0u, state.Titles.DisplayTitleId);
        Assert.Empty(state.Titles.EarnedTitleIds);
        Assert.True(state.CaptureOwnership().IsConverged);
    }

    [Fact]
    public void CharacterSnapshot_EmbedsTitlesSnapshot()
    {
        using var state = new RuntimeCharacterState();
        state.Titles.ReplaceTable(13u, [1u, 5u, 13u]);

        RuntimeCharacterSnapshot snapshot = state.View.Snapshot;

        Assert.Equal(13u, snapshot.Titles.DisplayTitleId);
        Assert.Equal(3, snapshot.Titles.TitleCount);
    }

    private static ActiveEnchantmentRecord MakeVitae(uint spellId, float val) =>
        new(
            spellId, LayerId: 0u, Duration: -1f, CasterGuid: 0u,
            StatModType: 0u, StatModKey: 0u, StatModValue: val, Bucket: 4u);

    private static SpellTable SpellTableWith(
        params (uint id, string name, uint family)[] rows)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Spell ID,Spell ID [Hex],Name,SortKey,IconId [Hex],Difficulty,Duration,Family,Flags [Hex],Generation,IsDebuff,IsFastWindup,IsFellowship,IsIrresistible,IsOffensive,IsUntargetted,Mana,School,Speed,Spell Words,CasterEffect,TargetEffect,TargetMask [Hex],Type,Description,Unknown1,Unknown2,Unknown3,Unknown4,Unknown5,Unknown6,Unknown7,Unknown8,Unknown9,Unknown10");
        foreach ((uint id, string name, uint family) in rows)
        {
            sb.Append(id).Append(',').Append("0x").Append(id.ToString("X")).Append(',')
              .Append(name).Append(",0,0x0,1,1,").Append(family).Append(",0x0,1,False,False,False,False,False,False,1,War Magic,0,Words,0,0,0x0,1,Desc,0,0,0,0,0,0,0,0,0,0")
              .AppendLine();
        }
        return SpellTable.LoadFromReader(new System.IO.StringReader(sb.ToString()));
    }

    [Fact]
    public void CharacterViewBorrowsExactOwnersWithoutReconstructedState()
    {
        using var state = new RuntimeCharacterState();
        state.LocalPlayer.OnAttributeUpdate(1u, 40u, 10u, 500u);
        state.LocalPlayer.OnVitalUpdate(7u, 60u, 20u, 700u, 75u);
        state.LocalPlayer.OnSkillUpdate(
            6u,
            30u,
            2u,
            800u,
            10u,
            0u,
            5d,
            12u);
        state.Spellbook.OnSpellLearned(42u);
        state.Spellbook.SetFavorite(0, 0, 42u);
        state.Spellbook.SetDesiredComponent(0x68000001u, 11u);

        RuntimeCharacterSnapshot summary = state.View.Snapshot;

        Assert.Equal(1, summary.LearnedSpellCount);
        Assert.Equal(1, summary.DesiredComponentCount);
        Assert.Equal(1, summary.SkillCount);
        Assert.True(state.View.KnowsSpell(42u));
        Assert.True(state.View.TryGetFavorite(0, 0, out uint favorite));
        Assert.Equal(42u, favorite);
        Assert.True(state.View.TryGetDesiredComponent(
            0x68000001u,
            out uint desired));
        Assert.Equal(11u, desired);
        Assert.True(state.View.TryGetAttribute(
            (int)LocalPlayerState.AttributeKind.Strength,
            out RuntimeAttributeSnapshot attribute));
        Assert.Equal(50u, attribute.Current);
        Assert.True(state.View.TryGetVital(
            (int)LocalPlayerState.VitalKind.Health,
            out RuntimeVitalSnapshot vital));
        Assert.Equal(75u, vital.Current);
        Assert.True(state.View.TryGetSkill(6u, out RuntimeSkillSnapshot skill));
        Assert.Equal(52u, skill.CurrentLevel);
    }

    [Fact]
    public void TwoRuntimeInstancesIsolateOptionsSkillsAndViewRevisions()
    {
        using var first = new RuntimeCharacterState();
        using var second = new RuntimeCharacterState();

        first.Options.Replace(1u, 2u);
        first.MovementSkills.Update(100, 200);
        first.Spellbook.OnSpellLearned(9u);

        Assert.NotEqual(
            first.View.Snapshot.Options,
            second.View.Snapshot.Options);
        Assert.True(first.View.Snapshot.MovementSkills.IsComplete);
        Assert.False(second.View.Snapshot.MovementSkills.IsComplete);
        Assert.True(first.View.Snapshot.SpellbookRevision > 0);
        Assert.Equal(0, second.View.Snapshot.SpellbookRevision);
    }

    [Fact]
    public void SpellbookCommandsFollowRetailLocalAndOutboundOrder()
    {
        using var state = new RuntimeCharacterState();
        var order = new List<string>();
        state.Spellbook.SpellbookChanged += () => order.Add("local");
        state.Spellbook.DesiredComponentsChanged += () => order.Add("local");

        Assert.True(state.TryAddFavorite(
            0,
            0,
            42u,
            () => order.Add("send")));
        Assert.Equal(["local", "send"], order);
        Assert.Equal([42u], state.Spellbook.GetFavorites(0));

        order.Clear();
        state.SetSpellbookFilter(0x3FFEu, () => order.Add("send"));
        Assert.Equal(["local", "send"], order);
        Assert.Equal(0x3FFEu, state.Spellbook.SpellbookFilters);

        order.Clear();
        Assert.True(state.TrySetDesiredComponent(
            0x68000001u,
            0u,
            () => order.Add("send")));
        Assert.Equal(["send", "local"], order);
        Assert.True(state.Spellbook.DesiredComponents.ContainsKey(
            0x68000001u));
        Assert.Equal(0u, state.Spellbook.DesiredComponents[0x68000001u]);

        order.Clear();
        state.ClearDesiredComponents(() => order.Add("send"));
        Assert.Equal(["send", "local"], order);
        Assert.Empty(state.Spellbook.DesiredComponents);

        Assert.Throws<InvalidOperationException>(
            () => state.TrySetDesiredComponent(
                0x68000002u,
                10u,
                () => throw new InvalidOperationException("transport")));
        Assert.Equal(10u, state.Spellbook.DesiredComponents[0x68000002u]);
    }

    [Fact]
    public void InvalidSpellbookCommandsDoNotPublishOrMutate()
    {
        using var state = new RuntimeCharacterState();
        int sends = 0;

        Assert.False(state.TryAddFavorite(
            8,
            0,
            42u,
            () => sends++));
        Assert.False(state.TryRemoveFavorite(
            -1,
            42u,
            () => sends++));
        Assert.False(state.TrySetDesiredComponent(
            0u,
            1u,
            () => sends++));
        Assert.False(state.TrySetDesiredComponent(
            1u,
            5001u,
            () => sends++));

        Assert.Equal(0, sends);
        Assert.Empty(state.Spellbook.GetFavorites(0));
        Assert.Empty(state.Spellbook.DesiredComponents);
    }
}
