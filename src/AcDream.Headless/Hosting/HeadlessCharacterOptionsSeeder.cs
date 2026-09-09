using AcDream.Core.Net.Messages;
using AcDream.Runtime;
using AcDream.Runtime.Gameplay;

namespace AcDream.Headless.Hosting;

internal sealed class HeadlessCharacterOptionsSeeder
{
    private readonly IReadOnlyList<KeyValuePair<CharacterOptionId, bool>> _declared;
    private readonly GameRuntime _runtime;
    private readonly IRuntimeCharacterCommands _commands;
    private bool _loginCompleteSent;

    internal HeadlessCharacterOptionsSeeder(
        IReadOnlyDictionary<CharacterOptionId, bool> declared,
        GameRuntime runtime,
        IRuntimeCharacterCommands commands)
    {
        ArgumentNullException.ThrowIfNull(declared);
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _declared = [.. declared.OrderBy(static pair => (uint)pair.Key)];
    }

    internal bool HasDeclaredOptions => _declared.Count > 0;

    internal void NoteLoginCompleteSent()
    {
        _loginCompleteSent = true;
        TryDiffAndSend();
    }

    internal void NoteOptionsSeeded() => TryDiffAndSend();

    private void TryDiffAndSend()
    {
        if (!_loginCompleteSent || _declared.Count == 0)
            return;

        RuntimeCharacterOptionsState options = _runtime.CharacterOwner.Options;
        if (!options.HasServerSeed)
            return;

        RuntimeGenerationToken generation = _runtime.Generation;
        bool needsFlush = false;
        foreach ((CharacterOptionId id, bool desired) in _declared)
        {
            if (!CharacterOptionTable.TryGet(id, out CharacterOptionTableEntry entry))
                continue;

            uint word = entry.IsOptions1 ? options.Options1 : options.Options2;
            bool current = (word & entry.Mask) != 0u;
            if (current == desired)
                continue;

            _commands.SetSingleOption(generation, (uint)id, desired);
            if (!entry.IsAutoSave)
                needsFlush = true;
        }

        if (needsFlush)
            _commands.SaveOptions(generation);
    }
}
