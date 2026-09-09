using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using AcDream.App.UI.Layout;
using AcDream.Content;
using AcDream.Core.Content;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Lib;
using DatReaderWriter.Lib.IO;
using DatReaderWriter.Types;

namespace AcDream.App.Tests.UI.Layout;

public sealed class DatStringResolverTemplateTests
{
    private const uint TableId = 0x23000001u;

    [Fact]
    public void PlayerVariableIsTheRetailHash()
        => Assert.Equal(0x05506DA2u, DatStringResolver.PlayerVariable);

    [Fact]
    public void ComposesTrailingFragmentTemplate()
    {
        // ID_Allegiance_SwearConfirmation's shape:
        // ["Do you wish to swear to ", "?"] + [PLAYER]
        var resolver = MakeResolver(
            "ID_Allegiance_SwearConfirmation",
            fragments: ["Do you wish to swear to ", "?"],
            variables: [DatStringResolver.PlayerVariable]);

        Assert.Equal(
            "Do you wish to swear to +Horan?",
            resolver.ResolveTemplate(
                TableId,
                "ID_Allegiance_SwearConfirmation",
                new Dictionary<uint, string>
                {
                    [DatStringResolver.PlayerVariable] = "+Horan",
                }));
    }

    [Fact]
    public void ComposesLeadingVariableTemplate()
    {
        // ID_Fellowship_FellowshipRequest's shape: an EMPTY first fragment,
        // so the player name leads the sentence.
        var resolver = MakeResolver(
            "ID_Fellowship_FellowshipRequest",
            fragments: [
                "",
                " has invited you to join their fellowship. Do you accept?",
            ],
            variables: [DatStringResolver.PlayerVariable]);

        Assert.Equal(
            "+Acdream has invited you to join their fellowship. Do you accept?",
            resolver.ResolveTemplate(
                TableId,
                "ID_Fellowship_FellowshipRequest",
                new Dictionary<uint, string>
                {
                    [DatStringResolver.PlayerVariable] = "+Acdream",
                }));
    }

    [Fact]
    public void MissingVariableSubstitutesEmpty()
    {
        var resolver = MakeResolver(
            "ID_Allegiance_SwearConfirmation",
            fragments: ["Do you wish to swear to ", "?"],
            variables: [DatStringResolver.PlayerVariable]);

        Assert.Equal(
            "Do you wish to swear to ?",
            resolver.ResolveTemplate(
                TableId,
                "ID_Allegiance_SwearConfirmation",
                new Dictionary<uint, string>()));
    }

    [Fact]
    public void ResolveDecodesEscapesAtTheSource()
    {
        var resolver = MakeResolver(
            "ID_Confirm_Exit",
            fragments: [
                "This will exit your character from the game world.\\n\\nAre you sure?",
            ],
            variables: []);

        Assert.Equal(
            "This will exit your character from the game world.\n\nAre you sure?",
            resolver.Resolve(
                TableId, DatStringResolver.ComputeHash("ID_Confirm_Exit")));
    }

    [Fact]
    public void ResolveAllDecodesEveryVariant()
    {
        var resolver = MakeResolver(
            "ID_Variants",
            fragments: ["one\\nline", "two\\tcol"],
            variables: []);

        string[] variants = Assert.IsType<string[]>(resolver.ResolveAll(
            TableId, DatStringResolver.ComputeHash("ID_Variants")));
        Assert.Equal(["one\nline", "two\tcol"], variants);
    }

    [Fact]
    public void ResolveTemplateDecodesFragmentsAndKeepsVariablesVerbatim()
    {
        var resolver = MakeResolver(
            "ID_Delete_Confirmation",
            fragments: ["Delete ", "?\\nType 'DELETE' to confirm."],
            variables: [DatStringResolver.PlayerVariable]);

        Assert.Equal(
            "Delete Odd\\nName?\nType 'DELETE' to confirm.",
            resolver.ResolveTemplate(
                TableId,
                "ID_Delete_Confirmation",
                new Dictionary<uint, string>
                {
                    [DatStringResolver.PlayerVariable] = "Odd\\nName",
                }));
    }

    [Fact]
    public void UnknownKeyResolvesNull()
    {
        var resolver = MakeResolver(
            "ID_Allegiance_SwearConfirmation",
            fragments: ["Do you wish to swear to ", "?"],
            variables: [DatStringResolver.PlayerVariable]);

        Assert.Null(resolver.ResolveTemplate(
            TableId, "ID_Not_A_Key", new Dictionary<uint, string>()));
    }

    private static DatStringResolver MakeResolver(
        string key,
        string[] fragments,
        uint[] variables)
    {
        var entry = new StringTableString();
        foreach (string fragment in fragments)
            entry.Strings.Add(fragment);
        foreach (uint variable in variables)
            entry.Variables.Add(variable);

        var table = new StringTable { Id = TableId };
        table.Strings[DatStringResolver.ComputeHash(key)] = entry;
        return new DatStringResolver(new SingleTableSource(table));
    }

    private sealed class SingleTableSource : IDatReaderWriter
    {
        private readonly StringTable _table;

        public SingleTableSource(StringTable table) => _table = table;

        public string SourceDirectory => string.Empty;
        public IDatDatabase Portal => throw new NotSupportedException();
        public IDatDatabase Cell => throw new NotSupportedException();
        public ReadOnlyDictionary<uint, IDatDatabase> CellRegions { get; } =
            new(new Dictionary<uint, IDatDatabase>());
        public IDatDatabase HighRes => throw new NotSupportedException();
        public IDatDatabase Language => throw new NotSupportedException();
        public IDatDatabase Local => throw new NotSupportedException();
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

        public IEnumerable<uint> GetAllIdsOfType<T>() where T : IDBObj =>
            Array.Empty<uint>();

        public IEnumerable<IDatReaderWriter.IdResolution> ResolveId(uint id) =>
            Array.Empty<IDatReaderWriter.IdResolution>();

        public bool TrySave<T>(T obj, int iteration = 0) where T : IDBObj =>
            throw new NotSupportedException();

        public bool TrySave<T>(
            uint regionId,
            T obj,
            int iteration = 0) where T : IDBObj =>
            throw new NotSupportedException();

        [return: MaybeNull]
        public T Get<T>(uint fileId) where T : IDBObj =>
            fileId == _table.Id && _table is T match ? match : default;

        public bool TryGet<T>(
            uint fileId,
            [MaybeNullWhen(false)] out T value) where T : IDBObj
        {
            if (fileId == _table.Id && _table is T match)
            {
                value = match;
                return true;
            }
            value = default;
            return false;
        }

        public void Dispose() { }
    }
}
