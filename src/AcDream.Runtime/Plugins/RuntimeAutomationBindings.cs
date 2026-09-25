using System.Reflection;
using AcDream.Content;
using AcDream.Core.Physics;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Navigation;

namespace AcDream.Runtime.Plugins;

/// <summary>Leaving the world, and whether that is allowed right now.</summary>
internal sealed record RuntimeAutomationLogoutCommands(
    Func<bool> Request,
    Func<bool> CanRequest);

/// <summary>
/// The installed data files a host lends the plugin surface, together with
/// the lock every read of them on that host is made under. The two travel
/// as one so a host cannot hand over the files without the lock: the files
/// are not safe to read from two threads at once, and the host's other
/// readers already take this lock.
/// </summary>
/// <param name="Dats">The installed data files.</param>
/// <param name="Lock">The host's lock on <paramref name="Dats"/>, the same object its other readers hold.</param>
internal sealed record RuntimeAutomationContent(IDatReaderWriter Dats, object Lock)
{
    public IDatReaderWriter Dats { get; } = Dats ?? throw new ArgumentNullException(nameof(Dats));
    public object Lock { get; } = Lock ?? throw new ArgumentNullException(nameof(Lock));
}

/// <summary>
/// What a particular host lends the plugin surface. Everything a runtime
/// owner can answer on its own is bound inside
/// <see cref="RuntimeAutomationBindings.Apply"/> and does not appear here;
/// what is left is either genuinely host-shaped (a keyboard going into a
/// chat entry, a window's chat draft) or an operation that has not been
/// moved into the runtime yet, in which case the host that lacks it carries
/// a seam-census allow-list row saying so.
/// </summary>
/// <remarks>
/// <see cref="Declared"/> is the host's standing claim about which of these
/// it can ever supply; the census compares those claims between hosts
/// without needing either host to be running. <see cref="Apply"/> refuses a
/// record that supplies something its host never declared, so the claim
/// cannot drift away from the code that builds the record.
/// </remarks>
internal sealed record RuntimeAutomationHostCapabilities
{
    /// <summary>The host this record was built by, for failure messages.</summary>
    public required string HostName { get; init; }

    /// <summary>Every capability name this host can ever supply.</summary>
    public required IReadOnlySet<string> Declared { get; init; }

    /// <summary>
    /// Capabilities this host declares but can only supply when a condition
    /// holds, keyed by capability name with the condition in plain terms
    /// (for example "only with installed content"). Every key must also be
    /// in <see cref="Declared"/>. A capability outside this map is one the
    /// host claims it always supplies, so a missing one is a defect rather
    /// than a configuration.
    /// </summary>
    public IReadOnlyDictionary<string, string> Conditional { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Where a binding problem is reported; host-shaped, not a capability.</summary>
    public Action<string>? Warn { get; init; }

    /// <summary>
    /// Whether the keyboard is going into text rather than into the character.
    /// Host-shaped, not a capability: the chat entry is a runtime owner, and
    /// this is the one thing about it only a host with a keyboard can know. A
    /// host without one leaves it out and the entry decides for itself.
    /// </summary>
    public Func<bool>? KeyboardGoesToText { get; init; }

    /// <summary>
    /// The tick the plugin surface follows. Host-shaped, not a capability:
    /// every host has one, and which object it is is the host's own business.
    /// </summary>
    public IEvents? PluginEvents { get; init; }

    /// <summary>
    /// Shows or hides the navigation grid. Host-shaped, not a capability:
    /// there is nothing to draw a grid on without a window, so a windowless
    /// client leaves it out and the verb says so in plain words.
    /// </summary>
    public Func<bool>? NavigationGrid { get; init; }

    /// <summary>
    /// Draws a planned route without walking it, for the same reason as
    /// <see cref="NavigationGrid"/>.
    /// </summary>
    public Func<uint, bool>? NavigationRoutePreview { get; init; }

    public RuntimeAutomationContent? Content { get; init; }
    public MagicCatalog? MagicCatalog { get; init; }
    public Func<string, bool>? SubmitChatText { get; init; }
    public IGameRuntimeCommands? SessionCommands { get; init; }
    public NavigationWalkController? NavigationWalk { get; init; }
    public RuntimeAutomationLogoutCommands? Logout { get; init; }
    public Func<uint, bool, bool>? AnswerConfirmation { get; init; }


    /// <summary>The names of the properties that are not the host's own bookkeeping.</summary>
    internal static IReadOnlyList<PropertyInfo> CapabilityProperties { get; } =
        typeof(RuntimeAutomationHostCapabilities)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(static property => property.Name is not (
                nameof(HostName) or nameof(Declared) or nameof(Warn)
                or nameof(Conditional) or nameof(KeyboardGoesToText)
                or nameof(PluginEvents) or nameof(NavigationGrid)
                or nameof(NavigationRoutePreview)))
            .OrderBy(static property => property.Name, StringComparer.Ordinal)
            .ToArray();

    /// <summary>Every capability name the surface knows how to be given.</summary>
    internal static IReadOnlySet<string> AllCapabilityNames { get; } =
        CapabilityProperties
            .Select(static property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>What this particular record actually carries.</summary>
    internal IReadOnlySet<string> Supplied()
    {
        var supplied = new HashSet<string>(StringComparer.Ordinal);
        foreach (PropertyInfo property in CapabilityProperties)
        {
            object? value = property.GetValue(this);
            if (value is bool flag ? flag : value is not null)
                supplied.Add(property.Name);
        }
        return supplied;
    }
}

/// <summary>
/// The one place the plugin surface's seams are filled. Both hosts call it,
/// so a seam that exists for one and not the other is a difference in the
/// capability record rather than in two separately written wiring blocks
/// nothing compares.
/// </summary>
internal static class RuntimeAutomationBindings
{
    /// <summary>The installed skill table every host reads skill names and icons from.</summary>
    private const uint SkillTableId = 0x0E000004u;

    /// <summary>
    /// Builds the plugin surface. Both hosts come through here, so the
    /// construction-time inputs a surface needs -- the tick it announces
    /// itself on, where the clients on this machine leave their notes, and
    /// the words this client answers to -- cannot be present on one client
    /// and quietly absent on the other.
    /// </summary>
    /// <param name="inputs">What this host hands the surface at birth.</param>
    /// <exception cref="ArgumentException">
    /// The host named no peer folder, so the clients on this machine would
    /// have nowhere to find one another.
    /// </exception>
    internal static RuntimeAutomationSurface CreateSurface(
        RuntimeAutomationSurfaceInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentException.ThrowIfNullOrWhiteSpace(
            inputs.PeerDirectory,
            $"{inputs.HostName}.{nameof(inputs.PeerDirectory)}");
        return new RuntimeAutomationSurface(
            inputs.PluginEvents,
            new LocalPluginPeerRegistry(inputs.PeerDirectory),
            inputs.PeerTags);
    }

    /// <summary>
    /// Which capability each seam needs, or <see langword="null"/> when the
    /// seam is filled from the runtime itself and therefore exists on every
    /// host. A key of the form <c>Method.parameter</c> is an optional
    /// argument of that seam, which a host can leave out on its own.
    /// </summary>
    internal static IReadOnlyDictionary<string, string?> SeamCapabilities { get; } =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Bind"] = null,
            ["BindSkillNames"] = nameof(RuntimeAutomationHostCapabilities.Content),
            ["BindSkillIcons"] = nameof(RuntimeAutomationHostCapabilities.Content),
            ["BindPaletteColorResolver"] =
                nameof(RuntimeAutomationHostCapabilities.Content),
            ["BindMagicCatalog"] =
                nameof(RuntimeAutomationHostCapabilities.MagicCatalog),
            ["BindSubmit"] =
                nameof(RuntimeAutomationHostCapabilities.SubmitChatText),
            ["BindSessionCommands"] =
                nameof(RuntimeAutomationHostCapabilities.SessionCommands),
            ["BindNavigationWalk"] =
                nameof(RuntimeAutomationHostCapabilities.NavigationWalk),
            ["BindNavigationCommands"] = null,
            ["BindStatusCommand"] = null,
            ["BindEquipment"] = null,
            ["BindEquipment.equipSecondary"] = null,
            ["BindItems"] = null,
            ["BindItems.salvageItems"] = null,
            ["BindItems.sellItem"] = null,
            ["BindItems.moveItemJoiningStack"] = null,
            ["BindLogout"] = nameof(RuntimeAutomationHostCapabilities.Logout),
            ["BindDialogs"] =
                nameof(RuntimeAutomationHostCapabilities.AnswerConfirmation),
            ["BindWorldObjectUse"] = null,
            ["BindGhostDeletion"] = null,
            ["BindSelectionActions"] = null,
            ["BindChatInputActive"] = null,
            ["BindChatComposer"] = null,
            ["BindSpeciesNameResolver"] =
                nameof(RuntimeAutomationHostCapabilities.Content),
            ["BindTitleNameResolver"] =
                nameof(RuntimeAutomationHostCapabilities.Content),
            ["BindDungeonMap"] =
                nameof(RuntimeAutomationHostCapabilities.Content),
        };

    /// <summary>
    /// Fills every seam the runtime can answer and every seam the host has
    /// lent a capability for, and reports which seams that came to. The
    /// report is what a test compares against
    /// <see cref="SeamCapabilities"/>, so the map cannot claim a seam the
    /// code does not actually bind.
    /// </summary>
    internal static IReadOnlySet<string> Apply(
        RuntimeAutomationSurface surface,
        GameRuntime runtime,
        RuntimeAutomationHostCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(capabilities);

        IReadOnlySet<string> supplied = capabilities.Supplied();
        string[] undeclared = supplied
            .Where(name => !capabilities.Declared.Contains(name))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        if (undeclared.Length != 0)
        {
            throw new InvalidOperationException(
                $"The {capabilities.HostName} host supplied plugin "
                + $"capabilities it does not declare: "
                + $"{string.Join(", ", undeclared)}. Add them to the host's "
                + "declared set so the host-parity census can see them.");
        }

        string[] conditionalButUndeclared = capabilities.Conditional.Keys
            .Where(name => !capabilities.Declared.Contains(name))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        if (conditionalButUndeclared.Length != 0)
        {
            throw new InvalidOperationException(
                $"The {capabilities.HostName} host names conditions for "
                + "plugin capabilities it does not declare at all: "
                + $"{string.Join(", ", conditionalButUndeclared)}.");
        }

        var bound = new HashSet<string>(StringComparer.Ordinal);

        if (!surface.EnsureBound(
            runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast))
        {
            capabilities.Warn?.Invoke(
                "plugin automation: the surface is already shut down, so "
                + "nothing was bound for this session");
            return bound;
        }
        bound.Add("Bind");

        if (capabilities.Content is { } content)
            BindContent(surface, runtime, content, capabilities.Warn, bound);
        if (capabilities.MagicCatalog is { } magicCatalog)
        {
            surface.BindMagicCatalog(magicCatalog);
            bound.Add(nameof(surface.BindMagicCatalog));
            // Which weenie classes are spell-component packs is named by the
            // spell catalogue, which is read from the installed data files
            // rather than from the wire. A host that draws an inventory also
            // tells the owner during its own composition, before any plugin
            // exists; telling it here as well costs nothing and is the only
            // time a host without one ever hears it.
            runtime.ItemInteractionOwner.BindComponentPackResolver(
                magicCatalog.IsComponentPack);
        }
        if (capabilities.SubmitChatText is { } submitChatText)
        {
            surface.BindSubmit(submitChatText);
            bound.Add("BindSubmit");
        }
        if (capabilities.SessionCommands is { } sessionCommands)
        {
            surface.BindSessionCommands(sessionCommands);
            bound.Add(nameof(surface.BindSessionCommands));
        }
        if (capabilities.NavigationWalk is { } navigationWalk)
        {
            surface.BindNavigationWalk(navigationWalk);
            bound.Add(nameof(surface.BindNavigationWalk));
        }
        // The client's own navigation verbs are built once, here, on the one
        // command registry both hosts hand plugins. A host that can draw lends
        // the two verbs that need drawing; the rest answer the same on either.
        surface.BindNavigationCommands(
            runtime,
            capabilities.PluginEvents,
            capabilities.NavigationGrid,
            capabilities.NavigationRoutePreview,
            capabilities.NavigationWalk is { } narratedWalk
                ? listener => narratedWalk.Narration = listener
                : null);
        bound.Add(nameof(surface.BindNavigationCommands));
        // /status is the same kind of thing: one verb, one answer, on
        // whichever front end asked.
        surface.BindStatusCommand(runtime);
        bound.Add(nameof(surface.BindStatusCommand));
        // Every item command a plugin can issue is answered by the runtime's
        // own item-interaction owner. Who owns an item, which container is
        // open, which vendor is trading, whether a request is already in
        // flight and how recently the last use went out are all runtime
        // state, so nothing here depends on a host drawing anything, and a
        // client without a window gives the same answer as a client with one.
        RuntimeItemInteraction itemOwner = runtime.ItemInteractionOwner;
        surface.BindEquipment(
            (itemId, requestedLocation) => itemOwner.TryWieldItem(
                itemId, (AcDream.Core.Items.EquipMask)requestedLocation),
            () => itemOwner.IsAutoWieldBusy,
            itemOwner.TryWieldItemSecondary);
        bound.Add(nameof(surface.BindEquipment));
        bound.Add("BindEquipment.equipSecondary");
        surface.BindItems(
            itemOwner.TryUseItemForAutomation,
            itemOwner.TryApplyItem,
            itemOwner.TryMoveItemForAutomation,
            itemOwner.TryMergeItemsForAutomation,
            itemOwner.TryDropItemForAutomation,
            itemOwner.TryGiveItemForAutomation,
            itemOwner.TryPlaceWorldItemInBackpack,
            itemOwner.TryAppraiseForAutomation,
            itemOwner.TrySalvageItemsForAutomation,
            (vendorId, itemId, amount) =>
                itemOwner.TrySell(vendorId, [(amount, itemId)]),
            itemOwner.TryMoveItemForAutomation);
        bound.Add(nameof(surface.BindItems));
        bound.Add("BindItems.salvageItems");
        bound.Add("BindItems.sellItem");
        bound.Add("BindItems.moveItemJoiningStack");
        if (capabilities.Logout is { } logout)
        {
            surface.BindLogout(logout.Request, logout.CanRequest);
            bound.Add(nameof(surface.BindLogout));
        }
        if (capabilities.AnswerConfirmation is { } answerConfirmation)
        {
            surface.BindDialogs(answerConfirmation);
            bound.Add(nameof(surface.BindDialogs));
        }
        // Using an object the character does not own walks to it first, and
        // that walk-then-use route is a runtime owner, so both clients give a
        // plugin the same walk, the same use and the same refusals.
        surface.BindWorldObjectUse(
            objectId => RuntimeAutomationSurface.MapWorldObjectUseOutcome(
                runtime.WorldObjectUseOwner.TryUse(objectId)));
        bound.Add(nameof(surface.BindWorldObjectUse));
        // Letting go of an object the client still believes in is a decision
        // about the entity directory, so both clients answer it from the same
        // owner; the one that draws has already lent it the route that takes
        // down what it drew.
        surface.BindGhostDeletion(runtime.GhostDismissalOwner.Dismiss);
        bound.Add(nameof(surface.BindGhostDeletion));
        // Stepping the selection is an ordering over the entity directory, so
        // both clients step through the same characters in the same order; a
        // key press reaches the same owner by its own route.
        RuntimeSelectionCycle selectionCycle = runtime.SelectionCycleOwner;
        surface.BindSelectionActions(action => action switch
        {
            PluginSelectionAction.PreviousSelection =>
                selectionCycle.SelectPreviousSelection(),
            PluginSelectionAction.PreviousPlayer => selectionCycle.SelectPlayer(
                RuntimeSelectionCycleDirection.Previous),
            PluginSelectionAction.NextPlayer => selectionCycle.SelectPlayer(
                RuntimeSelectionCycleDirection.Next),
            _ => false,
        });
        bound.Add(nameof(surface.BindSelectionActions));
        // The chat entry is a runtime owner, so both hosts answer these from
        // the same place: a console front end and a chat box are two ways of
        // driving one entry.
        AcDream.Runtime.Chat.RuntimeChatEntryOwner chatEntry =
            runtime.CommunicationOwner.ChatEntryOwner;
        chatEntry.BindInputActiveSource(capabilities.KeyboardGoesToText);
        surface.BindChatInputActive(() => chatEntry.IsInputActive);
        bound.Add(nameof(surface.BindChatInputActive));
        surface.BindChatComposer(chatEntry.Compose);
        bound.Add(nameof(surface.BindChatComposer));
        ReportDeclaredButUnfilled(capabilities, bound);
        return bound;
    }

    /// <summary>
    /// Says which seams the host's declaration promised and this run did not
    /// fill. A host guards every capability with a nullable part, so a part
    /// that came back null quietly leaves a seam empty while the parity
    /// census -- which reads declarations, not runs -- still swears it is
    /// filled. A capability whose condition the host named is reported as a
    /// configuration; anything else is reported as a defect.
    /// </summary>
    private static void ReportDeclaredButUnfilled(
        RuntimeAutomationHostCapabilities capabilities,
        IReadOnlySet<string> bound)
    {
        if (capabilities.Warn is not { } warn)
            return;

        foreach (string seam in SeamCapabilities
            .Where(entry => entry.Value is { } capability
                && capabilities.Declared.Contains(capability)
                && !bound.Contains(entry.Key))
            .Select(static entry => entry.Key)
            .OrderBy(static seam => seam, StringComparer.Ordinal))
        {
            string capability = SeamCapabilities[seam]!;
            warn(capabilities.Conditional.TryGetValue(
                capability, out string? condition)
                ? $"plugin automation: {seam} is unfilled on the "
                    + $"{capabilities.HostName} host because {condition}; "
                    + "plugins asking for it get nothing this session"
                : $"plugin automation: {seam} is unfilled on the "
                    + $"{capabilities.HostName} host although the host "
                    + "declares it unconditionally; a plugin asking for it "
                    + "gets nothing");
        }
    }

    /// <summary>
    /// What the installed data files lend the surface: palette colours for
    /// appearance, and the skill table, without which a plugin sees the
    /// character's skills unnamed and cannot judge what it can cast. One
    /// reader, one load, the same result on either host. Every read is made
    /// under the lock the host handed over with the files, the same lock its
    /// own readers hold, so a plugin asking for a whole dungeon's cells
    /// cannot read beside the host's streaming.
    /// </summary>
    private static void BindContent(
        RuntimeAutomationSurface surface,
        GameRuntime runtime,
        RuntimeAutomationContent content,
        Action<string>? warn,
        HashSet<string> bound)
    {
        IDatReaderWriter dats = content.Dats;
        object datLock = content.Lock;

        // The poses a line of speech can carry come out of the same files, and
        // both hosts read them here so "hello *wave*" does the same thing on
        // either. Nothing on the plugin surface needs them; the chat command
        // route does.
        runtime.CommunicationOwner.ChatPoses =
            AcDream.Runtime.Chat.ChatPoseCatalog.Load(dats, datLock);

        // What a contract is called and what it asks for is authored in
        // the same files. Read the first time a plugin opens the
        // character's contracts and not again, so a session that never
        // asks never pays for it.
        runtime.ContractsOwner.BindCatalog(() =>
        {
            lock (datLock)
                return AcDream.Content.ContractTableReader.Load(dats);
        });

        // Palette colours are read the moment a plugin asks for an object's
        // palettes, from whatever thread it asks on, so the catalogue takes
        // the host's lock on every read.
        surface.BindPaletteColorResolver(
            new AcDream.Content.CharGen.ChargenAppearanceCatalog(dats, datLock));
        bound.Add(nameof(surface.BindPaletteColorResolver));

        // What kind of creature a plugin is looking at. The table is read the
        // first time something asks rather than while the surface is being
        // wired, so a session that never asks never pays for it.
        var creatureNames = new Lazy<CreatureDisplayNameResolver>(
            () =>
            {
                lock (datLock)
                    return CreatureDisplayNameResolver.Load(dats);
            });
        surface.BindSpeciesNameResolver(
            species => creatureNames.Value.Resolve(species));
        bound.Add(nameof(surface.BindSpeciesNameResolver));

        // What a character title says is authored in the same files, and a
        // plugin reporting the character's titles reads it from here on
        // either host. Read the first time a title is asked about.
        var titleNames = new Lazy<AcDream.Content.CharacterTitleResolver>(
            () => new AcDream.Content.CharacterTitleResolver(dats));
        surface.BindTitleNameResolver(titleId =>
        {
            lock (datLock)
                return titleNames.Value.Resolve(titleId);
        });
        bound.Add(nameof(surface.BindTitleNameResolver));

        // The shape of a dungeon is authored in the same files: a plugin
        // drawing a map reads it from here, on either host, and the files
        // are only opened when a plan is first asked for.
        surface.BindDungeonMap(dats, datLock);
        bound.Add(nameof(surface.BindDungeonMap));

        DatReaderWriter.DBObjs.SkillTable? skillTable;
        lock (datLock)
            skillTable = dats.Get<DatReaderWriter.DBObjs.SkillTable>(SkillTableId);
        if (skillTable is null)
        {
            warn?.Invoke(
                "plugin automation: the installed skill table is missing, so "
                + "plugins will see unnamed skills");
            return;
        }

        var names = new Dictionary<uint, string>(skillTable.Skills.Count);
        var icons = new Dictionary<uint, uint>(skillTable.Skills.Count);
        foreach (var entry in skillTable.Skills)
        {
            names[(uint)entry.Key] = entry.Value.Name;
            icons[(uint)entry.Key] = entry.Value.IconId;
        }
        surface.BindSkillNames(names);
        surface.BindSkillIcons(icons);
        bound.Add(nameof(surface.BindSkillNames));
        bound.Add(nameof(surface.BindSkillIcons));
    }
}
