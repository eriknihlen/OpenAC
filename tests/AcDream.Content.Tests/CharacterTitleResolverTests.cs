using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using AcDream.Content;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Lib.IO;
using DatReaderWriter.Types;

namespace AcDream.Content.Tests;

/// <summary>
/// A title's text is two reads: the title number names an entry in the title
/// mapping, and that entry's name is the key of the text in the title string
/// table. Both clients read it through this one resolver.
/// </summary>
public sealed class CharacterTitleResolverTests
{
    [Fact]
    public void ATitleReadsItsTextThroughTheMappingAndTheStringTable()
    {
        var resolver = new CharacterTitleResolver(new TitleContent());

        Assert.Equal("Master of \"Things\"", resolver.Resolve(7u));
        Assert.Null(resolver.Resolve(8u));
        Assert.Null(resolver.Resolve(0u));
    }

    private sealed class TitleContent : IDatReaderWriter
    {
        private readonly EnumMapper _mapper = BuildMapper();
        private readonly StringTable _strings = BuildStrings();

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

        public bool TryGetFileBytes(uint regionId, uint fileId, ref byte[] bytes, out int bytesRead)
        {
            bytesRead = 0;
            return false;
        }

        public IEnumerable<uint> GetAllIdsOfType<T>() where T : IDBObj => Array.Empty<uint>();

        public IEnumerable<IDatReaderWriter.IdResolution> ResolveId(uint id) =>
            Array.Empty<IDatReaderWriter.IdResolution>();

        public bool TrySave<T>(T obj, int iteration = 0) where T : IDBObj =>
            throw new NotSupportedException();

        public bool TrySave<T>(uint regionId, T obj, int iteration = 0) where T : IDBObj =>
            throw new NotSupportedException();

        [return: MaybeNull]
        public T Get<T>(uint fileId) where T : IDBObj
        {
            if (fileId == CharacterTitleResolver.TitleEnumMapperId && _mapper is T mapper)
                return mapper;
            if (fileId == CharacterTitleResolver.TitleStringTableId && _strings is T strings)
                return strings;
            return default;
        }

        public bool TryGet<T>(uint fileId, [MaybeNullWhen(false)] out T value) where T : IDBObj
        {
            value = Get<T>(fileId);
            return value is not null;
        }

        public void Dispose()
        {
        }

        private static EnumMapper BuildMapper()
        {
            var mapper = new EnumMapper { BaseEnumMap = 0u };
            mapper.IdToStringMap.Add(7u, "ID_Title_Master");
            mapper.IdToStringMap.Add(8u, "ID_Title_Unwritten");
            return mapper;
        }

        private static StringTable BuildStrings()
        {
            var entry = new StringTableString();
            entry.Strings.Add(@"Master of \qThings\q");
            var table = new StringTable { Id = CharacterTitleResolver.TitleStringTableId };
            table.Strings[RetailStringHash.Compute("ID_Title_Master")] = entry;
            return table;
        }
    }
}
