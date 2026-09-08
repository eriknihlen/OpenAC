using DatReaderWriter.Lib.IO;
using System.Diagnostics.CodeAnalysis;

namespace AcDream.Core.Content;

public interface IDatObjectSource
{
    [return: MaybeNull]
    T Get<T>(uint fileId) where T : IDBObj;

    bool TryGet<T>(uint fileId, [MaybeNullWhen(false)] out T value)
        where T : IDBObj;
}
