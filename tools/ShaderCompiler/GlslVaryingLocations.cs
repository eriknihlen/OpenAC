using System.Text;
using System.Text.RegularExpressions;

namespace AcDream.Tools.ShaderCompiler;

internal static partial class GlslVaryingLocations
{
    // GLSL allows the interpolation qualifier on either side of the direction —
    // both `out flat uvec2 v;` and `flat out uvec2 v;` appear in acdream's
    // shaders — so the pattern accepts either and neither.
    [GeneratedRegex(
        @"^(?<indent>\s*)(?<leading>(flat|noperspective|smooth|centroid)\s+)*(?<direction>in|out)\s+(?<interp>(flat|noperspective|smooth|centroid)\s+)*(?<type>[A-Za-z_][A-Za-z0-9_]*)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*(?<array>\[[^\]]*\])?\s*;\s*(?<trailing>//.*)?$",
        RegexOptions.ExplicitCapture)]
    private static partial Regex VaryingDeclaration();

    internal static string Apply(string source, string stage, Dictionary<string, int> locationsByName)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(locationsByName);

        string[] lines = source.Replace("\r\n", "\n").Split('\n');
        var output = new StringBuilder();
        int nextFragmentOutput = 0;

        foreach (string line in lines)
        {
            if (line.Contains("layout(", StringComparison.Ordinal) || line.Contains('{'))
            {
                output.AppendLine(line);
                continue;
            }

            Match match = VaryingDeclaration().Match(line);
            if (!match.Success)
            {
                output.AppendLine(line);
                continue;
            }

            string direction = match.Groups["direction"].Value;
            string name = match.Groups["name"].Value;

            if (stage == "vert" && direction == "in")
            {
                output.AppendLine(line);
                continue;
            }

            int location;
            if (stage == "frag" && direction == "out")
            {
                location = nextFragmentOutput++;
            }
            else if (locationsByName.TryGetValue(name, out int existing))
            {
                location = existing;
            }
            else
            {
                location = locationsByName.Count == 0 ? 0 : locationsByName.Values.Max() + 1;
                locationsByName[name] = location;
            }

            string indent = match.Groups["indent"].Value;
            string rest = line[indent.Length..];
            output.AppendLine($"{indent}layout(location = {location}) {rest}");
        }

        return output.ToString();
    }
}
