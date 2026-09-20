#pragma warning disable CS1591
using System.Globalization;
using System.Text.RegularExpressions;

namespace AcDream.Plugin.Abstractions;

/// <summary>Parses compass coordinate pairs from ordinary chat text.</summary>
public static partial class PluginChatCoordinateParser
{
    [GeneratedRegex(@"(?<a>\d+(?:\.\d+)?)\s*(?<ad>[NSEW])\s*(?:[,;/|]|\s+)\s*(?<b>\d+(?:\.\d+)?)\s*(?<bd>[NSEW])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PairPattern();

    /// <summary>Finds every unambiguous coordinate pair in a message.</summary>
    public static IReadOnlyList<PluginChatCoordinate> ParseAll(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<PluginChatCoordinate>();
        var result = new List<PluginChatCoordinate>();
        foreach (Match match in PairPattern().Matches(text))
        {
            if (!double.TryParse(match.Groups["a"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double a)
                || !double.TryParse(match.Groups["b"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double b)) continue;
            char ad = char.ToUpperInvariant(match.Groups["ad"].Value[0]);
            char bd = char.ToUpperInvariant(match.Groups["bd"].Value[0]);
            if (ad is not ('N' or 'S') || bd is not ('E' or 'W'))
            {
                if (ad is not ('E' or 'W') || bd is not ('N' or 'S')) continue;
                (a, b) = (b, a); (ad, bd) = (bd, ad);
            }
            result.Add(new PluginChatCoordinate(bd == 'W' ? -b : b, ad == 'S' ? -a : a));
        }
        return result;
    }

    /// <summary>Parses exactly one coordinate pair and rejects ambiguous text.</summary>
    public static bool TryParse(string? text, out PluginChatCoordinate coordinate)
    {
        IReadOnlyList<PluginChatCoordinate> values = ParseAll(text);
        coordinate = values.Count == 1 ? values[0] : default;
        return values.Count == 1;
    }
}

/// <summary>Routes coordinate chat clicks to a plugin-owned destination callback.</summary>
public sealed class PluginChatCoordinateLinkRouter : IDisposable
{
    private readonly IPluginChat _chat;
    private readonly Action<PluginChatCoordinate> _destination;
    private bool _disposed;
    public PluginChatCoordinateLinkRouter(IPluginChat chat, Action<PluginChatCoordinate> destination)
    {
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _destination = destination ?? throw new ArgumentNullException(nameof(destination));
        _chat.LinkClicked += OnLinkClicked;
    }
    private void OnLinkClicked(PluginChatLinkClicked link)
    {
        if (!_disposed && link.Kind == PluginChatLinkKind.Coordinate && link.Coordinate is { } coordinate)
            _destination(coordinate);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _chat.LinkClicked -= OnLinkClicked;
    }
}
