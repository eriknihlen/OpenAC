using System.Runtime.CompilerServices;
using System.Text;

namespace AcDream.Core.Net.Messages;

public static class Encodings
{
#pragma warning disable CA2255 // Library-internal: registers CP1252 once
                              // before any wire-string parser is invoked.
    [ModuleInitializer]
    internal static void Register()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }
#pragma warning restore CA2255

    public static readonly Encoding Windows1252 = Encoding.GetEncoding(1252);
}
