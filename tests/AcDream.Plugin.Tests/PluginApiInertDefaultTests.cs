// Copyright (c) OpenAC contributors.
// Distributed under the terms of the MIT license.

using AcDream.Plugin.Abstractions;

namespace AcDream.Plugin.Tests;

/// <summary>
/// A member added to the contract answers something harmless on a host that
/// has not implemented it, so a plugin built against the newer contract
/// still runs on an older host.
/// </summary>
public sealed class PluginApiInertDefaultTests
{
    [Fact]
    public void ARenderPackRegistryNamesNoFolderByDefault()
    {
        AcDream.Plugin.Abstractions.Rendering.IRenderPackRegistry registry = new BareRenderPackRegistry();
        Assert.Null(registry.PluginDirectory);
    }

    private sealed class BareRenderPackRegistry : AcDream.Plugin.Abstractions.Rendering.IRenderPackRegistry
    {
        public IDisposable Register(
            AcDream.Plugin.Abstractions.Rendering.RenderPackDescriptor descriptor,
            AcDream.Plugin.Abstractions.Rendering.IRenderPackAssets assets) =>
            NoOpPluginRegistration.Instance;
    }

    [Fact]
    public void VitaeReadsAsNoPenaltyByDefault()
    {
        ICharacterInfo character = new MinimalCharacter();
        Assert.Equal(1f, character.VitaeMultiplier);
        Assert.Equal(0, character.VitaePenaltyPercent);
    }

    [Fact]
    public void TitlesReadAsNoneByDefault()
    {
        ICharacterInfo character = new MinimalCharacter();
        Assert.Equal(0u, character.CurrentTitleId);
        Assert.Empty(character.Titles);
    }

    [Fact]
    public void AllegianceIdentitiesReadAsNoneByDefault()
    {
        IAllegianceAutomation allegiance = new EmptyAllegiance();
        PluginAllegianceSnapshot snapshot = allegiance.Snapshot;
        Assert.Null(snapshot.Monarch);
        Assert.Null(snapshot.Patron);
        Assert.Empty(snapshot.Vassals);
    }

    private sealed class EmptyAllegiance : IAllegianceAutomation;

    [Fact]
    public void AFellowshipsTermsAndLevelsReadAsNoneByDefault()
    {
        IFellowshipAutomation fellowship = new EmptyFellowship();
        Assert.False(fellowship.SharesExperience);
        Assert.False(fellowship.SplitsExperienceEvenly);
        Assert.Equal(
            0u,
            new PluginFellowMember(1u, "x", 0u, 0u, 0u, 0u, 0u, 0u, 0f).Level);
    }

    private sealed class EmptyFellowship : IFellowshipAutomation;

    [Fact]
    public void IndoorCellsAreEmptyByDefault()
    {
        IDungeonMapAutomation map = new EmptyDungeonMap();
        Assert.Empty(map.CaptureIndoorCells(0x01230000u));
    }

    private sealed class EmptyDungeonMap : IDungeonMapAutomation;

    [Fact]
    public void TheStatusBoardKeepsNothingByDefault()
    {
        IPluginStatusBoard board = NoOpPluginStatusBoard.Instance;
        Assert.False(board.IsAvailable);
        Assert.False(board.Publish("state", "x"));
        Assert.False(board.TryRead("any.plugin", "state", out string value));
        Assert.Equal(string.Empty, value);
        Assert.Empty(board.Capture("any.plugin"));
        // The empty answer is shared between callers, so it must not be
        // something one of them can fill in for everybody else.
        Assert.False(board.Capture("any.plugin")
            is IDictionary<string, string> { IsReadOnly: false });
    }

    [Theory]
    [InlineData(0.95f, 5)]
    [InlineData(0.9f, 10)]
    [InlineData(0.67f, 33)]
    [InlineData(1f, 0)]
    public void VitaePenaltyIsTheMultiplierInWholePercent(float multiplier, int percent)
    {
        ICharacterInfo character = new MinimalCharacter { Vitae = multiplier };
        Assert.Equal(percent, character.VitaePenaltyPercent);
    }

    private sealed class MinimalCharacter : ICharacterInfo
    {
        public float? Vitae { get; init; }
        public bool IsInWorld => false;
        public uint ObjectId => 0u;
        public uint CurrentHealth => 0u;
        public uint MaxHealth => 0u;
        public uint CurrentStamina => 0u;
        public uint MaxStamina => 0u;
        public uint CurrentMana => 0u;
        public uint MaxMana => 0u;
        public IReadOnlyList<PluginSkillInfo> Skills => [];
        public IReadOnlyList<PluginAttributeInfo> Attributes => [];
        public IReadOnlyList<PluginActiveEnchantment> ActiveEnchantments => [];
        float ICharacterInfo.VitaeMultiplier => Vitae ?? 1f;

        public bool TryGetSkill(uint skillId, out PluginSkillInfo skill)
        {
            skill = default;
            return false;
        }
    }
}
