using System.Text;

namespace AcDream.App.UI.Layout;

public static class RetailStringEscapes
{
    private const string MetaCharacters = "[]!{}#\\|^$";

    internal static char GetUnEscapedChar(char value) => value switch
    {
        'n' => '\n',
        'q' => '"',
        'r' => '\r',
        't' => '\t',
        not '\0' when MetaCharacters.Contains(value) => value,
        _ => '\0',
    };

    internal static char GetEscapedChar(char value) => value switch
    {
        '\t' => 't',
        '\n' => 'n',
        '\r' => 'r',
        '"' => 'q',
        not '\0' when MetaCharacters.Contains(value) => value,
        _ => '\0',
    };

    public static string Unescape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        int first = value.IndexOf('\\');
        if (first < 0)
            return value;

        var result = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char current = value[i];
            char next = i + 1 < value.Length ? value[i + 1] : '\0';
            char unescaped = GetUnEscapedChar(next);
            if (current == '\\' && unescaped != '\0')
            {
                result.Append(unescaped);
                i++;
            }
            else if (current != '\0')
            {
                result.Append(current);
            }
        }
        return result.ToString();
    }

    public static string Escape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        StringBuilder? result = null;
        for (int i = 0; i < value.Length; i++)
        {
            char current = value[i];
            char escaped = GetEscapedChar(current);
            if (escaped != '\0')
            {
                result ??= new StringBuilder(value.Length + 4)
                    .Append(value, 0, i);
                result.Append('\\').Append(escaped);
            }
            else if (current != '\0')
            {
                result?.Append(current);
            }
        }
        return result?.ToString() ?? value;
    }
}
