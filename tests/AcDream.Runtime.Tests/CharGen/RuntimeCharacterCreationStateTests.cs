using AcDream.Core.CharGen;
using AcDream.Core.Net.Messages;
using AcDream.Runtime.Session;

namespace AcDream.Runtime.Tests.CharGen;

public sealed class RuntimeCharacterCreationStateTests
{
    private static RuntimeCharacterCreationState CreateActive()
    {
        var state = new RuntimeCharacterCreationState(
            RuntimeCharacterCreationStateFixture.Build(),
            new Random(1234));
        state.Begin(new RuntimeGenerationToken(1));
        return state;
    }

    // ── Lifecycle ───────────────────────────────────────────────────────

    [Fact]
    public void Begin_StartsWithNoHeritageOrGenderSelected()
    {
        RuntimeCharacterCreationState state = CreateActive();
        RuntimeCharacterCreationSnapshot snapshot = state.Snapshot;

        Assert.True(snapshot.IsActive);
        Assert.Equal(0u, snapshot.HeritageId);
        Assert.Equal(0u, snapshot.GenderKey);
        Assert.Equal(RuntimeCharacterCreationSnapshot.TemplateUnset, snapshot.Template);
        Assert.Equal(-1, snapshot.StartArea);
        Assert.False(snapshot.VerificationPending);
        Assert.Equal(string.Empty, snapshot.Name);
    }

    [Fact]
    public void Reset_ClearsEveryFieldAndDeactivates()
    {
        RuntimeCharacterCreationState state = CreateActive();
        Assert.True(state.TrySelectHeritage(RuntimeCharacterCreationStateFixture.AluvianId));
        Assert.True(state.TrySelectGender(RuntimeCharacterCreationStateFixture.MaleGenderKey));
        Assert.True(state.TrySetName("Someone"));

        state.Reset(new RuntimeGenerationToken(2));

        RuntimeCharacterCreationSnapshot snapshot = state.Snapshot;
        Assert.False(snapshot.IsActive);
        Assert.Equal(0u, snapshot.HeritageId);
        Assert.Equal(0u, snapshot.GenderKey);
        Assert.Equal(string.Empty, snapshot.Name);
        Assert.Equal(RuntimeCharacterCreationSnapshot.TemplateUnset, snapshot.Template);
        Assert.False(state.TrySelectHeritage(RuntimeCharacterCreationStateFixture.AluvianId));
    }


    [Fact]
    public void InstallOptions_BeforeBegin_ReplacesTheOptionsLaterCommandsUse()
    {
        var state = new RuntimeCharacterCreationState(ChargenOptions.Empty);

        state.InstallOptions(RuntimeCharacterCreationStateFixture.Build());
        state.Begin(new RuntimeGenerationToken(1));

        Assert.True(state.TrySelectHeritage(RuntimeCharacterCreationStateFixture.AluvianId));
        Assert.Equal(
            RuntimeCharacterCreationStateFixture.AluvianId,
            state.Snapshot.HeritageId);
    }

    [Fact]
    public void InstallOptions_WhileActive_ThrowsInsteadOfRacingLiveCommands()
    {
        RuntimeCharacterCreationState state = CreateActive();

        Assert.Throws<InvalidOperationException>(
            () => state.InstallOptions(RuntimeCharacterCreationStateFixture.Build()));
    }

    [Fact]
    public void InstallOptions_NullOptions_Throws()
    {
        var state = new RuntimeCharacterCreationState(ChargenOptions.Empty);

        Assert.Throws<ArgumentNullException>(() => state.InstallOptions(null!));
    }

    [Fact]
    public void InstallOptions_AfterDispose_Throws()
    {
        var state = new RuntimeCharacterCreationState(ChargenOptions.Empty);
        state.Dispose();

        Assert.Throws<ObjectDisposedException>(
            () => state.InstallOptions(RuntimeCharacterCreationStateFixture.Build()));
    }

    // ── Heritage / gender / template ────────────────────────────────────

    [Fact]
    public void TrySelectHeritage_UnknownId_IsRejected()
    {
        RuntimeCharacterCreationState state = CreateActive();
        Assert.False(state.TrySelectHeritage(0xDEADu));
        Assert.Equal(0u, state.Snapshot.HeritageId);
    }

    [Fact]
    public void TrySelectHeritage_RecomputesBudgetsAndRollsARandomPrimaryStartArea()
    {
        RuntimeCharacterCreationState state = CreateActive();

        Assert.True(state.TrySelectHeritage(RuntimeCharacterCreationStateFixture.AluvianId));

        RuntimeCharacterCreationSnapshot snapshot = state.Snapshot;
        Assert.Equal(RuntimeCharacterCreationStateFixture.AluvianId, snapshot.HeritageId);
        Assert.Equal(66u, snapshot.TotalAttributeCredits);
        Assert.Equal(50u, snapshot.TotalSkillCredits);
        Assert.Equal(0, snapshot.Attributes.Total);
        Assert.Equal(66, snapshot.RemainingAttributeCredits);
        Assert.True(snapshot.StartArea is 0 or 1);
    }

    [Fact]
    public void TrySelectGender_RequiresHeritageFirst()
    {
        RuntimeCharacterCreationState state = CreateActive();
        Assert.False(state.TrySelectGender(RuntimeCharacterCreationStateFixture.MaleGenderKey));
    }

    [Fact]
    public void TrySelectGender_UnknownKeyForHeritage_IsRejected()
    {
        RuntimeCharacterCreationState state = CreateActive();
        state.TrySelectHeritage(RuntimeCharacterCreationStateFixture.AluvianId);
        Assert.False(state.TrySelectGender(99u));
    }

    [Fact]
    public void TrySelectTemplate_Custom_AppliesFloorAttributesAndLeavesCreditsUnspent()
    {
        RuntimeCharacterCreationState state = CreateActive();
        state.TrySelectHeritage(RuntimeCharacterCreationStateFixture.AluvianId);
        state.TrySelectGender(RuntimeCharacterCreationStateFixture.MaleGenderKey);

        Assert.True(state.TrySelectTemplate(RuntimeCharacterCreationStateFixture.CustomTemplateIndex));

        RuntimeCharacterCreationSnapshot snapshot = state.Snapshot;
        Assert.Equal(0u, snapshot.Template);
        Assert.Equal(new ChargenAttributeValues(10, 10, 10, 10, 10, 10), snapshot.Attributes);
        Assert.Equal(6, snapshot.RemainingAttributeCredits);
        Assert.Equal(
            ChargenSkillAdvancementClass.Trained,
            state.GetSkillLevel(RuntimeCharacterCreationStateFixture.SkillCustomNormal));
    }

    [Fact]
    public void TrySelectTemplate_Preset_TrainsNormalAndSpecializesPrimarySkills()
    {
        RuntimeCharacterCreationState state = CreateActive();
        state.TrySelectHeritage(RuntimeCharacterCreationStateFixture.AluvianId);
        state.TrySelectGender(RuntimeCharacterCreationStateFixture.MaleGenderKey);

        Assert.True(state.TrySelectTemplate(RuntimeCharacterCreationStateFixture.PresetTemplateIndex));

        RuntimeCharacterCreationSnapshot snapshot = state.Snapshot;
        Assert.Equal(new ChargenAttributeValues(16, 10, 10, 10, 10, 10), snapshot.Attributes);
        Assert.Equal(0, snapshot.RemainingAttributeCredits);
        Assert.Equal(
            ChargenSkillAdvancementClass.Trained,
            state.GetSkillLevel(RuntimeCharacterCreationStateFixture.SkillTrainSpecialize));
        Assert.Equal(
            ChargenSkillAdvancementClass.Specialized,
            state.GetSkillLevel(RuntimeCharacterCreationStateFixture.SkillPresetPrimary));
        Assert.Equal(
            ChargenSkillAdvancementClass.Specialized,
            state.GetSkillLevel(RuntimeCharacterCreationStateFixture.SkillFreeSpecialized));
        Assert.Equal(
            ChargenSkillAdvancementClass.Trained,
            state.GetSkillLevel(RuntimeCharacterCreationStateFixture.SkillFreeTrained));
    }

    [Fact]
    public void TrySelectTemplate_OlthoiHeritage_AlwaysForcesTemplateZero()
    {
        RuntimeCharacterCreationState state = CreateActive();
        state.TrySelectHeritage(RuntimeCharacterCreationStateFixture.OlthoiId);
        state.TrySelectGender(RuntimeCharacterCreationStateFixture.MaleGenderKey);

        Assert.True(state.TrySelectTemplate(1u));

        Assert.Equal(0u, state.Snapshot.Template);
        Assert.Equal(new ChargenAttributeValues(10, 10, 10, 10, 10, 10), state.Snapshot.Attributes);
    }

    [Fact]
    public void TrySelectHeritage_TemplateOutOfRangeForNewHeritage_ClearsToUnset()
    {
        RuntimeCharacterCreationState state = CreateActive();
        state.TrySelectHeritage(RuntimeCharacterCreationStateFixture.AluvianId);
        state.TrySelectGender(RuntimeCharacterCreationStateFixture.MaleGenderKey);
        Assert.True(state.TrySelectTemplate(RuntimeCharacterCreationStateFixture.PresetTemplateIndex));
        Assert.Equal(1u, state.Snapshot.Template);

        Assert.True(state.TrySelectHeritage(RuntimeCharacterCreationStateFixture.ImpoverishedId));

        Assert.Equal(RuntimeCharacterCreationSnapshot.TemplateUnset, state.Snapshot.Template);
    }

    // ── Attributes ──────────────────────────────────────────────────────

    [Fact]
    public void TrySetAttribute_ClampsToTheFloorAndCeiling()
    {
        RuntimeCharacterCreationState state = CreateActive();
        state.TrySelectHeritage(RuntimeCharacterCreationStateFixture.AluvianId);
        state.TrySelectGender(RuntimeCharacterCreationStateFixture.MaleGenderKey);
        state.TrySelectTemplate(RuntimeCharacterCreationStateFixture.CustomTemplateIndex);

        Assert.True(state.TrySetAttribute(ChargenAttributeId.Strength, 5));
        Assert.Equal(10, state.Snapshot.Attributes.Strength);

        Assert.True(state.TrySetAttribute(ChargenAttributeId.Strength, 999));
        Assert.True(state.Snapshot.Attributes.Strength <= ChargenAttributeMath.AttributeMax);
    }

    [Fact]
    public void TrySetAttribute_RaisingOneAttributeRebalancesAnAboveFloorAttributeDownToTheFloor()
    {
        RuntimeCharacterCreationState state = CreateActive();
        state.TrySelectHeritage(RuntimeCharacterCreationStateFixture.AluvianId);
        state.TrySelectGender(RuntimeCharacterCreationStateFixture.MaleGenderKey);
        state.TrySelectTemplate(RuntimeCharacterCreationStateFixture.PresetTemplateIndex);

        Assert.True(state.TrySetAttribute(ChargenAttributeId.Endurance, 16));

        ChargenAttributeValues attrs = state.Snapshot.Attributes;
        Assert.Equal(16, attrs.Endurance);
        Assert.Equal(10, attrs.Strength);
        Assert.Equal(10, attrs.Coordination);
        Assert.Equal(10, attrs.Quickness);
        Assert.Equal(10, attrs.Focus);
        Assert.Equal(10, attrs.Self);
        Assert.Equal(66, attrs.Total);
        Assert.Equal(0, state.Snapshot.RemainingAttributeCredits);
    }

    [Fact]
    public void TrySetAttributeLock_PreventsThatAttributeFromAbsorbingABalance()
    {
        RuntimeCharacterCreationState state = CreateActive();
        state.TrySelectHeritage(RuntimeCharacterCreationStateFixture.AluvianId);
        state.TrySelectGender(RuntimeCharacterCreationStateFixture.MaleGenderKey);
        state.TrySelectTemplate(RuntimeCharacterCreationStateFixture.PresetTemplateIndex);
        Assert.True(state.TrySetAttributeLock(ChargenAttributeId.Strength, true));

        Assert.True(state.TrySetAttribute(ChargenAttributeId.Endurance, 16));

        Assert.Equal(10, state.Snapshot.Attributes.Endurance);
        Assert.Equal(16, state.Snapshot.Attributes.Strength);
    }

    [Fact]
    public void TrySetAttribute_SuccessiveOverspends_AbsorbFromDifferentAttributes()
    {
        RuntimeCharacterCreationState state = CreateActive();
        state.TrySelectHeritage(RuntimeCharacterCreationStateFixture.AluvianId);
        state.TrySelectGender(RuntimeCharacterCreationStateFixture.MaleGenderKey);
        // Str=16, everyone else at the 10 floor, fully spent (66/66) — the
        // fixture's only above-floor attribute at the start.
        state.TrySelectTemplate(RuntimeCharacterCreationStateFixture.PresetTemplateIndex);

        Assert.True(state.TrySetAttribute(ChargenAttributeId.Endurance, 11));
        Assert.Equal(15, state.Snapshot.Attributes.Strength);
        Assert.Equal(11, state.Snapshot.Attributes.Endurance);

        Assert.True(state.TrySetAttribute(ChargenAttributeId.Coordination, 11));
        Assert.Equal(15, state.Snapshot.Attributes.Strength); // untouched this time
        Assert.Equal(10, state.Snapshot.Attributes.Endurance); // donated
        Assert.Equal(11, state.Snapshot.Attributes.Coordination);
    }

    [Fact]
    public void TrySetAttribute_BalanceCursor_WrapsFromSelfBackToStrength()
    {
        RuntimeCharacterCreationState state = CreateActive();
        state.TrySelectHeritage(RuntimeCharacterCreationStateFixture.AluvianId);
        state.TrySelectGender(RuntimeCharacterCreationStateFixture.MaleGenderKey);
        // Str=16, everyone else at the 10 floor, fully spent (66/66).
        state.TrySelectTemplate(RuntimeCharacterCreationStateFixture.PresetTemplateIndex);

        // Lock every attribute except Strength and Self: they stay in the
        // budget total but are excluded from donation, isolating the wrap
        // behavior to exactly the two attributes under test.
        Assert.True(state.TrySetAttributeLock(ChargenAttributeId.Endurance, true));
        Assert.True(state.TrySetAttributeLock(ChargenAttributeId.Coordination, true));
        Assert.True(state.TrySetAttributeLock(ChargenAttributeId.Quickness, true));
        Assert.True(state.TrySetAttributeLock(ChargenAttributeId.Focus, true));

        Assert.True(state.TrySetAttribute(ChargenAttributeId.Self, 16));
        Assert.Equal(10, state.Snapshot.Attributes.Strength);
        Assert.Equal(16, state.Snapshot.Attributes.Self);

        Assert.True(state.TrySetAttribute(ChargenAttributeId.Strength, 11));
        Assert.Equal(11, state.Snapshot.Attributes.Strength);
        Assert.Equal(15, state.Snapshot.Attributes.Self);

        Assert.True(state.TrySetAttribute(ChargenAttributeId.Endurance, 11));
        Assert.Equal(10, state.Snapshot.Attributes.Strength);
        Assert.Equal(15, state.Snapshot.Attributes.Self); // unchanged — proves the wrap
        Assert.Equal(11, state.Snapshot.Attributes.Endurance);
    }

    // ── Skills ──────────────────────────────────────────────────────────

    [Fact]
    public void TrySpecializeSkill_UncostableSkill_IsAlwaysRejected()
    {
        RuntimeCharacterCreationState state = CreateActive();
        state.TrySelectHeritage(RuntimeCharacterCreationStateFixture.AluvianId);
        state.TrySelectGender(RuntimeCharacterCreationStateFixture.MaleGenderKey);
        state.TrySelectTemplate(RuntimeCharacterCreationStateFixture.CustomTemplateIndex);

        Assert.False(state.TrySpecializeSkill(RuntimeCharacterCreationStateFixture.SkillUncostable));
        Assert.False(state.TryTrainSkill(RuntimeCharacterCreationStateFixture.SkillUncostable));
        Assert.False(state.TryUntrainSkill(RuntimeCharacterCreationStateFixture.SkillUncostable));
        Assert.Equal(
            ChargenSkillAdvancementClass.Inactive,
            state.GetSkillLevel(RuntimeCharacterCreationStateFixture.SkillUncostable));
    }

    [Fact]
    public void TrainThenSpecializeSkill_ChargesExactlyPrimaryCostNotBoth()
    {
        RuntimeCharacterCreationState state = CreateActive();
        state.TrySelectHeritage(RuntimeCharacterCreationStateFixture.AluvianId);
        state.TrySelectGender(RuntimeCharacterCreationStateFixture.MaleGenderKey);
        state.TrySelectTemplate(RuntimeCharacterCreationStateFixture.CustomTemplateIndex);
        int before = state.Snapshot.RemainingSkillCredits;

        Assert.True(state.TryTrainSkill(RuntimeCharacterCreationStateFixture.SkillTrainSpecialize));
        Assert.Equal(before - 4, state.Snapshot.RemainingSkillCredits);

        Assert.True(state.TrySpecializeSkill(RuntimeCharacterCreationStateFixture.SkillTrainSpecialize));
        // PrimaryCost (12) is the TOTAL, not an increment on NormalCost.
        Assert.Equal(before - 12, state.Snapshot.RemainingSkillCredits);

        Assert.True(state.TryUntrainSkill(RuntimeCharacterCreationStateFixture.SkillTrainSpecialize));
        Assert.Equal(before, state.Snapshot.RemainingSkillCredits);
    }

    [Fact]
    public void TrySpecializeSkill_InsufficientCredits_IsRejectedAndLeavesStateUnchanged()
    {
        RuntimeCharacterCreationState state = CreateActive();
        state.TrySelectHeritage(RuntimeCharacterCreationStateFixture.ImpoverishedId);
        state.TrySelectGender(RuntimeCharacterCreationStateFixture.MaleGenderKey);
        state.TrySelectTemplate(RuntimeCharacterCreationStateFixture.CustomTemplateIndex);
        Assert.Equal(5, state.Snapshot.RemainingSkillCredits);

        Assert.False(state.TrySpecializeSkill(RuntimeCharacterCreationStateFixture.SkillTrainSpecialize));
        Assert.Equal(5, state.Snapshot.RemainingSkillCredits);
        Assert.Equal(
            ChargenSkillAdvancementClass.Untrained,
            state.GetSkillLevel(RuntimeCharacterCreationStateFixture.SkillTrainSpecialize));

        // NormalCost (4) fits; the SAME skill Specialized still does not.
        Assert.True(state.TryTrainSkill(RuntimeCharacterCreationStateFixture.SkillTrainSpecialize));
        Assert.Equal(1, state.Snapshot.RemainingSkillCredits);
        Assert.False(state.TrySpecializeSkill(RuntimeCharacterCreationStateFixture.SkillTrainSpecialize));
        Assert.Equal(1, state.Snapshot.RemainingSkillCredits);
        Assert.Equal(
            ChargenSkillAdvancementClass.Trained,
            state.GetSkillLevel(RuntimeCharacterCreationStateFixture.SkillTrainSpecialize));
    }

    // ── Finish gates ────────────────────────────────────────────────────

    private static RuntimeCharacterCreationState ReadyToFinishState()
    {
        RuntimeCharacterCreationState state = CreateActive();
        state.TrySelectHeritage(RuntimeCharacterCreationStateFixture.AluvianId);
        state.TrySelectGender(RuntimeCharacterCreationStateFixture.MaleGenderKey);
        state.TrySelectTemplate(RuntimeCharacterCreationStateFixture.PresetTemplateIndex); // fully spent attrs
        state.TrySetName("Adventurer");
        return state;
    }

    [Fact]
    public void TryBeginFinish_EmptyName_IsRefused()
    {
        RuntimeCharacterCreationState state = ReadyToFinishState();
        state.TrySetName("   ");

        bool accepted = state.TryBeginFinish(
            rosterCount: 0,
            slotCount: 11,
            out _,
            out _,
            out RuntimeCharacterCreationLocalRefusal refusal);

        Assert.False(accepted);
        Assert.True(refusal.NoName);
        Assert.False(state.Snapshot.VerificationPending);
    }

    [Fact]
    public void TryBeginFinish_UnspentAttributeCredits_IsRefused()
    {
        RuntimeCharacterCreationState state = CreateActive();
        state.TrySelectHeritage(RuntimeCharacterCreationStateFixture.AluvianId);
        state.TrySelectGender(RuntimeCharacterCreationStateFixture.MaleGenderKey);
        state.TrySelectTemplate(RuntimeCharacterCreationStateFixture.CustomTemplateIndex); // 6 unspent
        state.TrySetName("Adventurer");

        bool accepted = state.TryBeginFinish(
            0, 11, out _, out _, out RuntimeCharacterCreationLocalRefusal refusal);

        Assert.False(accepted);
        Assert.True(refusal.AttributeCreditsUnspent);
    }

    [Fact]
    public void TryBeginFinish_UnspentAttributeCreditsConfirmed_IsAccepted()
    {
        RuntimeCharacterCreationState state = CreateActive();
        state.TrySelectHeritage(RuntimeCharacterCreationStateFixture.AluvianId);
        state.TrySelectGender(RuntimeCharacterCreationStateFixture.MaleGenderKey);
        state.TrySelectTemplate(RuntimeCharacterCreationStateFixture.CustomTemplateIndex); // 6 unspent
        state.TrySetName("Adventurer");
        Assert.Equal(6, state.Snapshot.RemainingAttributeCredits);

        bool accepted = state.TryBeginFinish(
            0, 11, out CharacterCreate.Request request, out _,
            out RuntimeCharacterCreationLocalRefusal refusal,
            confirmedUnspentCredits: true);

        Assert.True(accepted);
        Assert.False(refusal.Any);
        Assert.True(state.Snapshot.VerificationPending);
        Assert.Equal(10u, request.Attributes.Strength);
    }

    [Fact]
    public void TryBeginFinish_SecondCallWhilePending_IsRefused()
    {
        RuntimeCharacterCreationState state = ReadyToFinishState();
        Assert.True(state.TryBeginFinish(
            0, 11, out _, out _, out RuntimeCharacterCreationLocalRefusal first));
        Assert.False(first.Any);

        bool second = state.TryBeginFinish(
            0, 11, out _, out _, out RuntimeCharacterCreationLocalRefusal refusal);

        Assert.False(second);
        Assert.True(refusal.AlreadyPending);
    }

    [Fact]
    public void TryBeginFinish_HeritageUnset_IsRefused()
    {
        RuntimeCharacterCreationState state = CreateActive();
        state.TrySetName("Adventurer");

        bool accepted = state.TryBeginFinish(
            0, 11, out _, out _, out RuntimeCharacterCreationLocalRefusal refusal);

        Assert.False(accepted);
        Assert.True(refusal.HeritageOrGenderUnset);
        Assert.False(refusal.NoName);
    }

    [Fact]
    public void TryBeginFinish_GenderUnset_IsRefused()
    {
        RuntimeCharacterCreationState state = CreateActive();
        state.TrySelectHeritage(RuntimeCharacterCreationStateFixture.AluvianId);
        state.TrySetName("Adventurer");

        bool accepted = state.TryBeginFinish(
            0, 11, out _, out _, out RuntimeCharacterCreationLocalRefusal refusal);

        Assert.False(accepted);
        Assert.True(refusal.HeritageOrGenderUnset);
    }

    [Fact]
    public void TryBeginFinish_RosterAtSlotCap_IsRefused()
    {
        RuntimeCharacterCreationState state = ReadyToFinishState();

        bool accepted = state.TryBeginFinish(
            rosterCount: 11,
            slotCount: 11,
            out _,
            out _,
            out RuntimeCharacterCreationLocalRefusal refusal);

        Assert.False(accepted);
        Assert.True(refusal.RosterFull);
    }

    [Fact]
    public void TryBeginFinish_Accepted_TrimsNameAndProducesExactly55SkillSlots()
    {
        RuntimeCharacterCreationState state = ReadyToFinishState();
        state.TrySetName("  Adventurer  ");

        bool accepted = state.TryBeginFinish(
            rosterCount: 2,
            slotCount: 11,
            out CharacterCreate.Request request,
            out uint[] skillAdvancementClasses,
            out RuntimeCharacterCreationLocalRefusal refusal);

        Assert.True(accepted);
        Assert.False(refusal.Any);
        Assert.True(state.Snapshot.VerificationPending);
        Assert.Equal("Adventurer", request.Name);
        Assert.Equal(RuntimeCharacterCreationStateFixture.AluvianId, request.Heritage);
        Assert.Equal(RuntimeCharacterCreationStateFixture.MaleGenderKey, request.Gender);
        Assert.Equal(RuntimeCharacterCreationStateFixture.PresetTemplateIndex, request.Template);
        Assert.Equal(16u, request.Attributes.Strength);
        Assert.Equal(CharacterCreate.SkillAdvancementClassCount, skillAdvancementClasses.Length);
        Assert.Equal(
            (uint)ChargenSkillAdvancementClass.Trained,
            skillAdvancementClasses[RuntimeCharacterCreationStateFixture.SkillTrainSpecialize]);
        Assert.Equal(
            (uint)ChargenSkillAdvancementClass.Specialized,
            skillAdvancementClasses[RuntimeCharacterCreationStateFixture.SkillPresetPrimary]);
    }

    // ── Response handling ───────────────────────────────────────────────

    private static RuntimeCharacterCreationState PendingState(out uint[] skills)
    {
        RuntimeCharacterCreationState state = ReadyToFinishState();
        Assert.True(state.TryBeginFinish(0, 11, out _, out skills, out _));
        return state;
    }

    [Fact]
    public void ApplyCreationResponse_Ok_RecordsCreatedIdentityAndClearsPending()
    {
        RuntimeCharacterCreationState state = PendingState(out _);

        state.ApplyCreationResponse(new CharGenVerificationResponse.Parsed(
            (uint)CharGenVerificationResponse.Code.Ok, 0x5000_1234u, "Adventurer", 0u));

        RuntimeCharacterCreationSnapshot snapshot = state.Snapshot;
        Assert.False(snapshot.VerificationPending);
        Assert.Equal(
            new RuntimeCharacterCreationIdentity(0x5000_1234u, "Adventurer"),
            snapshot.LastCreated);
        Assert.Null(snapshot.LastRejection);
    }

    [Theory]
    [InlineData(CharGenVerificationResponse.Code.NameInUse)]
    [InlineData(CharGenVerificationResponse.Code.NameBanned)]
    [InlineData(CharGenVerificationResponse.Code.Corrupt)]
    [InlineData(CharGenVerificationResponse.Code.DatabaseDown)]
    [InlineData(CharGenVerificationResponse.Code.AdminPrivilegeDenied)]
    [InlineData(CharGenVerificationResponse.Code.Pending)]
    [InlineData(CharGenVerificationResponse.Code.Undef)]
    public void ApplyCreationResponse_EachRejectionCode_RecordsTheMappingAndAttemptedName(
        CharGenVerificationResponse.Code code)
    {
        RuntimeCharacterCreationState state = PendingState(out _);

        state.ApplyCreationResponse(new CharGenVerificationResponse.Parsed(
            (uint)code, null, null, null));

        Assert.NotNull(state.Snapshot.LastRejection);
        RuntimeCharacterCreationRejection rejection = state.Snapshot.LastRejection!.Value;
        Assert.Equal(code, rejection.Code);
        Assert.Equal(code.ToString(), rejection.Reason);
        Assert.Equal("Adventurer", rejection.AttemptedName);
        Assert.False(state.Snapshot.VerificationPending);
        Assert.Null(state.Snapshot.LastCreated);
    }

    [Fact]
    public void ApplyCreationResponse_DuplicateReplyWhileNotPending_IsIgnored()
    {
        RuntimeCharacterCreationState state = PendingState(out _);
        state.ApplyCreationResponse(new CharGenVerificationResponse.Parsed(
            (uint)CharGenVerificationResponse.Code.NameInUse, null, null, null));
        Assert.NotNull(state.Snapshot.LastRejection);

        Assert.True(state.TryAcknowledgeRejection());
        state.ApplyCreationResponse(new CharGenVerificationResponse.Parsed(
            (uint)CharGenVerificationResponse.Code.NameInUse, null, null, null));

        Assert.Null(state.Snapshot.LastRejection);
    }

    [Fact]
    public void TryAcknowledgeRejection_ClearsTheSurfacedRejection()
    {
        RuntimeCharacterCreationState state = PendingState(out _);
        state.ApplyCreationResponse(new CharGenVerificationResponse.Parsed(
            (uint)CharGenVerificationResponse.Code.NameBanned, null, null, null));
        Assert.NotNull(state.Snapshot.LastRejection);

        Assert.True(state.TryAcknowledgeRejection());

        Assert.Null(state.Snapshot.LastRejection);
    }


    [Fact]
    public void TryRandomizeCharacter_RollsOnlyTheFourHumanHeritagesAndAGender()
    {
        for (int seed = 0; seed < 200; seed++)
        {
            var state = new RuntimeCharacterCreationState(
                RuntimeCharacterCreationStateFixture.Build(),
                new Random(seed));
            state.Begin(new RuntimeGenerationToken(1));

            Assert.True(state.TryRandomizeCharacter());

            RuntimeCharacterCreationSnapshot snapshot = state.Snapshot;
            Assert.InRange(snapshot.HeritageId, 1u, 4u);
            Assert.True(snapshot.GenderKey is 1u or 2u);
        }
    }

    [Fact]
    public void TryRandomizeCharacter_RollsAppearanceClothingTemplateAndStartArea()
    {
        var state = new RuntimeCharacterCreationState(
            RuntimeCharacterCreationStateFixture.Build(),
            new Random(7));
        state.Begin(new RuntimeGenerationToken(1));

        Assert.True(state.TryRandomizeCharacter());

        RuntimeCharacterCreationSnapshot snapshot = state.Snapshot;
        // Every list in the fixture's shared gender record is non-empty, so
        // a full randomize must leave nothing Unset.
        Assert.NotEqual(RuntimeCharacterCreationAppearance.Unset, snapshot.Appearance.HairStyle);
        Assert.NotEqual(RuntimeCharacterCreationAppearance.Unset, snapshot.Appearance.EyesStrip);
        Assert.NotEqual(RuntimeCharacterCreationAppearance.Unset, snapshot.Appearance.HairColor);
        Assert.NotEqual(RuntimeCharacterCreationAppearance.Unset, snapshot.Appearance.ShirtStyle);
        Assert.NotEqual(RuntimeCharacterCreationAppearance.Unset, snapshot.Appearance.TrousersStyle);
        Assert.NotEqual(RuntimeCharacterCreationAppearance.Unset, snapshot.Appearance.FootwearStyle);
        Assert.NotEqual(RuntimeCharacterCreationSnapshot.TemplateUnset, snapshot.Template);
        Assert.NotEqual(0u, snapshot.Template);
        Assert.True(snapshot.StartArea is 0 or 1);

        Assert.InRange(snapshot.Appearance.SkinShade, 0.0, 1.0);
        Assert.InRange(snapshot.Appearance.HairShade, 0.0, 1.0);
        Assert.InRange(snapshot.Appearance.HeadgearShade, 0.0, 1.0);
        Assert.InRange(snapshot.Appearance.ShirtShade, 0.0, 1.0);
        Assert.InRange(snapshot.Appearance.TrousersShade, 0.0, 1.0);
        Assert.InRange(snapshot.Appearance.FootwearShade, 0.0, 1.0);
    }

    [Fact]
    public void TryRandomizeCharacter_ShadeLattice_ReachesExactlyOneAtRandomMax()
    {
        var state = new RuntimeCharacterCreationState(
            RuntimeCharacterCreationStateFixture.Build(),
            new MaxValueRandom());
        state.Begin(new RuntimeGenerationToken(1));

        Assert.True(state.TryRandomizeCharacter());

        RuntimeCharacterCreationAppearance a = state.Snapshot.Appearance;
        Assert.Equal(1.0, a.SkinShade);
        Assert.Equal(1.0, a.HairShade);
        Assert.Equal(1.0, a.HeadgearShade);
        Assert.Equal(1.0, a.ShirtShade);
        Assert.Equal(1.0, a.TrousersShade);
        Assert.Equal(1.0, a.FootwearShade);
    }

    private sealed class MaxValueRandom : Random
    {
        public override int Next(int maxValue) => maxValue - 1;
    }

    [Fact]
    public void TryRandomizeCharacter_NeverRollsANonHumanHeritage()
    {
        for (int seed = 0; seed < 200; seed++)
        {
            var state = new RuntimeCharacterCreationState(
                RuntimeCharacterCreationStateFixture.Build(),
                new Random(seed));
            state.Begin(new RuntimeGenerationToken(1));

            Assert.True(state.TryRandomizeCharacter());

            Assert.NotEqual(RuntimeCharacterCreationStateFixture.OlthoiId, state.Snapshot.HeritageId);
            Assert.NotEqual(RuntimeCharacterCreationStateFixture.ImpoverishedId, state.Snapshot.HeritageId);
        }
    }

    [Fact]
    public void TryRandomizeCharacter_Inactive_IsRejected()
    {
        var state = new RuntimeCharacterCreationState(
            RuntimeCharacterCreationStateFixture.Build());
        Assert.False(state.TryRandomizeCharacter());
    }

    [Fact]
    public void TryRandomizeAppearance_RequiresHeritageAndGender()
    {
        RuntimeCharacterCreationState state = CreateActive();
        Assert.False(state.TryRandomizeAppearance());

        state.TrySelectHeritage(RuntimeCharacterCreationStateFixture.AluvianId);
        Assert.False(state.TryRandomizeAppearance());

        state.TrySelectGender(RuntimeCharacterCreationStateFixture.MaleGenderKey);
        Assert.True(state.TryRandomizeAppearance());
        Assert.NotEqual(
            RuntimeCharacterCreationAppearance.Unset,
            state.Snapshot.Appearance.HairStyle);
    }

    [Fact]
    public void TryRandomizeClothing_RollsAllFourGearSlots()
    {
        RuntimeCharacterCreationState state = CreateActive();
        state.TrySelectHeritage(RuntimeCharacterCreationStateFixture.AluvianId);
        state.TrySelectGender(RuntimeCharacterCreationStateFixture.MaleGenderKey);

        Assert.True(state.TryRandomizeClothing());

        RuntimeCharacterCreationAppearance a = state.Snapshot.Appearance;
        Assert.NotEqual(RuntimeCharacterCreationAppearance.Unset, a.ShirtStyle);
        Assert.NotEqual(RuntimeCharacterCreationAppearance.Unset, a.TrousersStyle);
        Assert.NotEqual(RuntimeCharacterCreationAppearance.Unset, a.FootwearStyle);
    }

    [Fact]
    public void TryRandomizeClothing_SingleOptionList_NeverHangs()
    {
        RuntimeCharacterCreationState state = CreateActive();
        state.TrySelectHeritage(RuntimeCharacterCreationStateFixture.AluvianId);
        state.TrySelectGender(RuntimeCharacterCreationStateFixture.MaleGenderKey);

        for (int i = 0; i < 50; i++)
            Assert.True(state.TryRandomizeClothing());

        Assert.Equal(0u, state.Snapshot.Appearance.ShirtStyle);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(999)]
    public void TryRandomizeAppearance_ExcludeCurrent_OnCountTwoLists_AlwaysFlips(int seed)
    {
        var state = new RuntimeCharacterCreationState(
            RuntimeCharacterCreationStateFixture.Build(),
            new Random(seed));
        state.Begin(new RuntimeGenerationToken(1));
        state.TrySelectHeritage(RuntimeCharacterCreationStateFixture.AluvianId);
        state.TrySelectGender(RuntimeCharacterCreationStateFixture.MaleGenderKey);

        Assert.True(state.TryRandomizeAppearance());
        RuntimeCharacterCreationAppearance first = state.Snapshot.Appearance;

        Assert.True(state.TryRandomizeAppearance());
        RuntimeCharacterCreationAppearance second = state.Snapshot.Appearance;

        Assert.True(first.HairStyle is 0u or 1u);
        Assert.True(first.HairColor is 0u or 1u);
        Assert.True(first.EyeColor is 0u or 1u);
        Assert.NotEqual(first.HairStyle, second.HairStyle);
        Assert.NotEqual(first.HairColor, second.HairColor);
        Assert.NotEqual(first.EyeColor, second.EyeColor);
    }

    [Fact]
    public void TryRandomizeCharacter_ClearsAPreviouslyCommittedName()
    {
        RuntimeCharacterCreationState state = CreateActive();
        state.TrySelectHeritage(RuntimeCharacterCreationStateFixture.AluvianId);
        state.TrySelectGender(RuntimeCharacterCreationStateFixture.MaleGenderKey);
        Assert.True(state.TrySetName("Bob"));
        Assert.Equal("Bob", state.Snapshot.Name);

        Assert.True(state.TryRandomizeCharacter());

        Assert.Equal(string.Empty, state.Snapshot.Name);
    }
}
