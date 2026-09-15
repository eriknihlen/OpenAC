using System.Text.RegularExpressions;

namespace AcDream.Launcher.Core.Plugins;

/// <summary>Builds and parses the <c>github.com/.../releases/.../download/...</c> URLs the plugin
/// pipeline uses. No <c>api.github.com</c> call is ever made (L-308).</summary>
public static class GitHubReleaseLocator
{
    private const string Host = "github.com";

    /// <summary>GitHub username rule: alphanumeric with single, non-leading, non-trailing hyphens,
    /// 1-39 characters.</summary>
    private static readonly Regex OwnerPattern = new(
        @"\A(?=.{1,39}\z)[A-Za-z0-9]+(?:-[A-Za-z0-9]+)*\z",
        RegexOptions.Compiled);

    private static readonly Regex NamePattern = new(
        @"\A[A-Za-z0-9._-]{1,100}\z",
        RegexOptions.Compiled);

    /// <summary>The owner/name shape both <see cref="TryParseRepoUrl"/> and
    /// <c>PluginCatalog</c>'s list validation enforce, so a repo string is never trusted with only
    /// one of the two checking it.</summary>
    public static bool IsValidRepo(string repo)
    {
        if (string.IsNullOrEmpty(repo))
            return false;

        string[] parts = repo.Split('/');
        return parts.Length == 2 && IsValidOwner(parts[0]) && IsValidName(parts[1]);
    }

    private static bool IsValidOwner(string owner) => OwnerPattern.IsMatch(owner);

    private static bool IsValidName(string name) =>
        name is not "." and not ".." && NamePattern.IsMatch(name);

    public static Uri LatestAsset(string repo, string assetName) =>
        BuildUri(repo, $"releases/latest/download/{Uri.EscapeDataString(assetName)}");

    public static Uri TaggedAsset(string repo, string tag, string assetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        return BuildUri(
            repo,
            $"releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(assetName)}");
    }

    /// <summary>Parses an <c>https://github.com/{owner}/{repo}</c> URL a user typed in. Nothing else
    /// is accepted: no query, fragment, credentials, or extra path segments.</summary>
    public static bool TryParseRepoUrl(string? value, out string repo)
    {
        repo = string.Empty;
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase)
            || uri.Port != 443
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        string[] segments = uri.AbsolutePath
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2 || !IsValidOwner(segments[0]) || !IsValidName(segments[1]))
            return false;

        repo = $"{segments[0]}/{segments[1]}";
        return true;
    }

    private static Uri BuildUri(string repo, string path)
    {
        (string owner, string name) = SplitRepo(repo);
        return new Uri(
            $"https://{Host}/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/{path}");
    }

    private static (string Owner, string Name) SplitRepo(string repo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        if (!IsValidRepo(repo))
        {
            throw new ArgumentException(
                $"'{repo}' is not an 'owner/name' repository.",
                nameof(repo));
        }

        string[] parts = repo.Split('/');
        return (parts[0], parts[1]);
    }
}
