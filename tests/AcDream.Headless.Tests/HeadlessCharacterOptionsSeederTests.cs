using AcDream.Core.Net.Messages;
using AcDream.Headless.Hosting;
using AcDream.Runtime;
using AcDream.Runtime.Gameplay;

namespace AcDream.Headless.Tests;

public sealed class HeadlessCharacterOptionsSeederTests
{
    private const uint AutoSaveMask = 0x00000002u;

    [Fact]
    public void DeclaredEqualsActualSendsNothing()
    {
        using GameRuntime runtime = NewRuntime();
        runtime.CharacterOwner.Options.Replace(0u, 0u);
        var commands = new FakeCharacterCommands(runtime.CharacterOwner.Options);
        var seeder = new HeadlessCharacterOptionsSeeder(
            new Dictionary<CharacterOptionId, bool>
            {
                [CharacterOptionId.AutoRepeatAttack] = false,
            },
            runtime,
            commands);

        seeder.NoteLoginCompleteSent();
        seeder.NoteOptionsSeeded();

        Assert.Empty(commands.CallOrder);
    }

    [Fact]
    public void AutoSaveDifferenceSendsExactlyOneSetSingleOption()
    {
        using GameRuntime runtime = NewRuntime();
        runtime.CharacterOwner.Options.Replace(0u, 0u);
        var commands = new FakeCharacterCommands(runtime.CharacterOwner.Options);
        var seeder = new HeadlessCharacterOptionsSeeder(
            new Dictionary<CharacterOptionId, bool>
            {
                [CharacterOptionId.AutoRepeatAttack] = true,
            },
            runtime,
            commands);

        seeder.NoteLoginCompleteSent();
        seeder.NoteOptionsSeeded();

        var call = Assert.Single(commands.SingleOptionCalls);
        Assert.Equal((uint)CharacterOptionId.AutoRepeatAttack, call.OptionId);
        Assert.True(call.Value);
        Assert.Equal(0, commands.SaveOptionsCallCount);
        Assert.Equal(["set:0"], commands.CallOrder);
    }

    [Fact]
    public void BatchedDifferenceSendsSetSingleOptionThenExactlyOneSaveOptionsAtTheEnd()
    {
        using GameRuntime runtime = NewRuntime();
        runtime.CharacterOwner.Options.Replace(0u, 0u);
        var commands = new FakeCharacterCommands(runtime.CharacterOwner.Options);
        var seeder = new HeadlessCharacterOptionsSeeder(
            new Dictionary<CharacterOptionId, bool>
            {
                [CharacterOptionId.IgnoreTradeRequests] = true,
            },
            runtime,
            commands);

        seeder.NoteLoginCompleteSent();
        seeder.NoteOptionsSeeded();

        var call = Assert.Single(commands.SingleOptionCalls);
        Assert.Equal((uint)CharacterOptionId.IgnoreTradeRequests, call.OptionId);
        Assert.True(call.Value);
        Assert.Equal(1, commands.SaveOptionsCallCount);
        Assert.Equal(["set:3", "save"], commands.CallOrder);
    }

    [Fact]
    public void MixedDeclarationOrdersAllSetsBeforeTheSingleFlush()
    {
        using GameRuntime runtime = NewRuntime();
        runtime.CharacterOwner.Options.Replace(0u, 0u);
        var commands = new FakeCharacterCommands(runtime.CharacterOwner.Options);
        var seeder = new HeadlessCharacterOptionsSeeder(
            new Dictionary<CharacterOptionId, bool>
            {
                // Declared out of id order on purpose — the seeder sorts
                // id-ascending internally, so the batched id (0x03) is
                // expected AFTER the auto-save id (0x00) regardless of
                // declaration order, with the flush always last.
                [CharacterOptionId.IgnoreTradeRequests] = true,
                [CharacterOptionId.AutoRepeatAttack] = true,
            },
            runtime,
            commands);

        seeder.NoteLoginCompleteSent();
        seeder.NoteOptionsSeeded();

        Assert.Equal(2, commands.SingleOptionCalls.Count);
        Assert.Equal(1, commands.SaveOptionsCallCount);
        Assert.Equal(["set:0", "set:3", "save"], commands.CallOrder);
    }

    [Fact]
    public void NeitherPreconditionAloneSendsAnything()
    {
        using GameRuntime runtime = NewRuntime();
        var commands = new FakeCharacterCommands(runtime.CharacterOwner.Options);
        var seeder = new HeadlessCharacterOptionsSeeder(
            new Dictionary<CharacterOptionId, bool>
            {
                [CharacterOptionId.AutoRepeatAttack] = true,
            },
            runtime,
            commands);

        seeder.NoteLoginCompleteSent();
        Assert.Empty(commands.CallOrder);

        runtime.CharacterOwner.Options.Replace(0u, 0u);
        seeder.NoteOptionsSeeded();

        Assert.Single(commands.SingleOptionCalls);
    }

    [Fact]
    public void OptionsSeededBeforeLoginCompleteDefersUntilLoginCompleteArrives()
    {
        using GameRuntime runtime = NewRuntime();
        runtime.CharacterOwner.Options.Replace(0u, 0u);
        var commands = new FakeCharacterCommands(runtime.CharacterOwner.Options);
        var seeder = new HeadlessCharacterOptionsSeeder(
            new Dictionary<CharacterOptionId, bool>
            {
                [CharacterOptionId.AutoRepeatAttack] = true,
            },
            runtime,
            commands);

        seeder.NoteOptionsSeeded();
        Assert.Empty(commands.CallOrder);

        seeder.NoteLoginCompleteSent();
        Assert.Single(commands.SingleOptionCalls);
    }

    [Fact]
    public void EmptyDeclarationNeverSendsRegardlessOfBothSignals()
    {
        using GameRuntime runtime = NewRuntime();
        runtime.CharacterOwner.Options.Replace(0xFFFFFFFFu, 0xFFFFFFFFu);
        var commands = new FakeCharacterCommands(runtime.CharacterOwner.Options);
        var seeder = new HeadlessCharacterOptionsSeeder(
            new Dictionary<CharacterOptionId, bool>(),
            runtime,
            commands);

        Assert.False(seeder.HasDeclaredOptions);
        seeder.NoteLoginCompleteSent();
        seeder.NoteOptionsSeeded();

        Assert.Empty(commands.CallOrder);
    }

    [Fact]
    public void ReconnectWithSameDeclarationsAgainstServerThatNowAgreesSendsNothing()
    {
        using GameRuntime runtime = NewRuntime();
        var declared = new Dictionary<CharacterOptionId, bool>
        {
            [CharacterOptionId.AutoRepeatAttack] = true,
        };

        runtime.CharacterOwner.Options.Replace(0u, 0u);
        var firstCommands = new FakeCharacterCommands(runtime.CharacterOwner.Options);
        var firstSeeder = new HeadlessCharacterOptionsSeeder(
            declared, runtime, firstCommands);
        firstSeeder.NoteLoginCompleteSent();
        firstSeeder.NoteOptionsSeeded();
        Assert.Single(firstCommands.SingleOptionCalls);

        runtime.CharacterOwner.Options.ResetSession();
        runtime.CharacterOwner.Options.Replace(AutoSaveMask, 0u);
        var secondCommands = new FakeCharacterCommands(runtime.CharacterOwner.Options);
        var secondSeeder = new HeadlessCharacterOptionsSeeder(
            declared, runtime, secondCommands);
        secondSeeder.NoteLoginCompleteSent();
        secondSeeder.NoteOptionsSeeded();

        Assert.Empty(secondCommands.CallOrder);
    }

    private static GameRuntime NewRuntime()
    {
        var gameplay = new HeadlessGameplayOperations();
        var runtime = new GameRuntime(new GameRuntimeDependencies(
            gameplay,
            gameplay,
            gameplay,
            gameplay));
        gameplay.Bind(runtime, catalog: null, () => "account");
        return runtime;
    }

    private sealed class FakeCharacterCommands(
        RuntimeCharacterOptionsState options) : IRuntimeCharacterCommands
    {
        internal List<(uint OptionId, bool Value)> SingleOptionCalls { get; } = [];
        internal int SaveOptionsCallCount { get; private set; }
        internal List<string> CallOrder { get; } = [];

        public RuntimeCommandResult Advance(
            RuntimeGenerationToken expectedGeneration,
            in RuntimeAdvancementCommand command) =>
            throw new NotSupportedException(
                "HeadlessCharacterOptionsSeeder never calls Advance.");

        public RuntimeCommandResult SetSingleOption(
            RuntimeGenerationToken expectedGeneration,
            uint optionId,
            bool value)
        {
            SingleOptionCalls.Add((optionId, value));
            CallOrder.Add($"set:{optionId}");
            options.SetOptionBit(optionId, value);
            return new RuntimeCommandResult(
                RuntimeCommandStatus.Accepted,
                expectedGeneration);
        }

        public RuntimeCommandResult SaveOptions(
            RuntimeGenerationToken expectedGeneration)
        {
            SaveOptionsCallCount++;
            CallOrder.Add("save");
            return new RuntimeCommandResult(
                RuntimeCommandStatus.Accepted,
                expectedGeneration);
        }

        public RuntimeCommandResult SetTitle(
            RuntimeGenerationToken expectedGeneration,
            uint titleId) =>
            throw new NotSupportedException(
                "HeadlessCharacterOptionsSeeder never calls SetTitle.");
    }
}
