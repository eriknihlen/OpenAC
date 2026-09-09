using System.Text;

namespace AcDream.Tools.ShaderCompiler;

internal static class GlslIncludeExpander
{
    internal static string Expand(string source, string sourceDirectory) =>
        Expand(source, Path.GetFullPath(sourceDirectory), []);

    private static string Expand(
        string source,
        string sourceDirectory,
        HashSet<string> active)
    {
        var output = new StringBuilder();
        foreach (string line in source.Replace("\r\n", "\n").Split('\n'))
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith("#include \"", StringComparison.Ordinal)
                || !trimmed.EndsWith('"'))
            {
                output.AppendLine(line);
                continue;
            }

            string key = trimmed[10..^1];
            if (key.Length == 0
                || Path.IsPathRooted(key)
                || key.Contains("..", StringComparison.Ordinal)
                || key.Contains('\\'))
                throw new InvalidDataException($"Unsafe GLSL include '{key}'.");
            string path = Path.GetFullPath(Path.Combine(sourceDirectory, key));
            if (!path.StartsWith(sourceDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || !File.Exists(path))
                throw new FileNotFoundException($"GLSL include '{key}' was not found.", path);
            if (!active.Add(path))
                throw new InvalidDataException($"Cyclic GLSL include '{key}'.");
            output.AppendLine($"// ---- begin include: {key} ----");
            output.Append(Expand(File.ReadAllText(path), sourceDirectory, active));
            output.AppendLine($"// ---- end include: {key} ----");
            active.Remove(path);
        }
        return output.ToString();
    }
}
