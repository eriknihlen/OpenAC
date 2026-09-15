using System.Buffers;
using System.Text.Json;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Remote;

/// <summary>
/// The character's whole pack for the phone's inventory view: what is worn,
/// what is in the main pack and each side pack, with the appraisal of
/// anything the client has already assessed. Nothing here asks the server
/// for an appraisal; the phone's tap does that through the assess command,
/// and the next scan picks the answer up. Built on the plugin tick, a few
/// seconds apart; <see cref="Version"/> moves only when something changed,
/// so a poll can tell a real change from a re-read.
/// </summary>
internal sealed class RemoteInventoryBuilder
{
    public const string Schema = "acdream.drakbot.inventory/1";

    /// <summary>The pseudo-container the worn items are listed under.</summary>
    public const uint EquippedContainerId = 1u;

    private readonly IAutomationSurface _surface;
    private readonly ArrayBufferWriter<byte> _buffer = new(32 * 1024);
    private int _lastFingerprint;

    public RemoteInventoryBuilder(IAutomationSurface surface)
    {
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
    }

    public int Version { get; private set; }

    /// <summary>
    /// Builds the inventory document, or returns null when nothing changed
    /// since the last build (the caller keeps serving the previous one).
    /// </summary>
    public byte[]? BuildIfChanged()
    {
        ICharacterInfo character = _surface.Character;
        bool inWorld = _surface.IsAvailable && character.IsInWorld;
        IReadOnlyList<PluginInventoryItem> owned = inWorld ? _surface.Items.CaptureOwnedItems() : [];
        uint playerId = character.ObjectId;

        var entries = new List<Entry>(owned.Count);
        var fingerprint = new HashCode();
        fingerprint.Add(playerId);
        foreach (PluginInventoryItem item in owned)
        {
            bool appraised = _surface.Objects.TryGet(item.ObjectId, out PluginWorldObject world) && world.HasAppraisalData;
            uint containerId = item.IsEquipped
                ? EquippedContainerId
                : item.ContainerObjectId == 0u ? playerId : item.ContainerObjectId;
            entries.Add(new Entry(item, containerId, appraised));
            fingerprint.Add(item.ObjectId);
            fingerprint.Add(item.StackSize);
            fingerprint.Add(containerId);
            fingerprint.Add(item.ContainerSlot);
            fingerprint.Add(item.EquippedLocation);
            fingerprint.Add(appraised);
            fingerprint.Add(item.ItemCurrentMana);
        }
        int hash = fingerprint.ToHashCode();
        if (hash == _lastFingerprint && Version > 0)
            return null;
        _lastFingerprint = hash;
        Version++;

        _buffer.Clear();
        using var json = new Utf8JsonWriter(_buffer);
        json.WriteStartObject();
        json.WriteString("schema", Schema);
        json.WriteNumber("version", Version);
        json.WriteNumber("itemCount", entries.Count);

        json.WritePropertyName("containers");
        json.WriteStartArray();
        WriteContainer(json, EquippedContainerId, "Equipped", "equipped", 0);
        int mainCapacity = _surface.Objects.TryGet(playerId, out PluginWorldObject player) ? player.ItemsCapacity : 0;
        WriteContainer(json, playerId, "Main Pack", "main", mainCapacity);
        foreach (Entry entry in entries)
        {
            if (entry.Item.ObjectClass == PluginObjectClass.Container && !entry.Item.IsEquipped)
                WriteContainer(json, entry.Item.ObjectId, entry.Item.Name, "side", entry.Item.ItemsCapacity);
        }
        json.WriteEndArray();

        json.WritePropertyName("items");
        json.WriteStartArray();
        foreach (Entry entry in entries)
        {
            PluginInventoryItem item = entry.Item;
            json.WriteStartObject();
            json.WriteNumber("id", item.ObjectId);
            json.WriteString("name", item.Name);
            json.WriteNumber("wcid", item.WeenieClassId);
            json.WriteNumber("objectClass", (int)item.ObjectClass);
            json.WriteNumber("containerId", entry.ContainerId);
            json.WriteNumber("location", item.EquippedLocation);
            json.WriteNumber("slot", item.ContainerSlot);
            json.WriteNumber("stackCount", Math.Max(1, item.StackSize));
            json.WriteNumber("iconDid", PluginIcons.Normalize(item.IconId));
            json.WriteBoolean("equipped", item.IsEquipped);
            json.WriteNumber("wieldedLocation", item.EquippedLocation);
            json.WriteBoolean("appraised", entry.Appraised);
            if (entry.Appraised)
            {
                json.WritePropertyName("appraisal");
                RemoteStatusBuilder.GearItem.From(item, _surface.Objects, _surface.Spells).Write(json);
            }
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();
        json.Flush();
        return _buffer.WrittenSpan.ToArray();
    }

    private static void WriteContainer(Utf8JsonWriter json, uint id, string name, string kind, int capacity)
    {
        json.WriteStartObject();
        json.WriteNumber("id", id);
        json.WriteString("name", name);
        json.WriteString("kind", kind);
        json.WriteNumber("capacity", capacity);
        json.WriteEndObject();
    }

    private readonly record struct Entry(PluginInventoryItem Item, uint ContainerId, bool Appraised);
}
