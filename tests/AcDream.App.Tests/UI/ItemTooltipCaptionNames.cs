using System;
using System.Collections.Generic;
using AcDream.App.UI.Layout;
using AcDream.Core.Items;
using AcDream.Automation;

namespace AcDream.App.Tests.UI;

/// <summary>
/// The composed-name resolver every item surface is handed in production,
/// backed by a one-entry material table so a hover caption can be checked
/// against the same name the selection caption shows.
/// </summary>
internal static class ItemTooltipCaptionNames
{
    public const uint Pyreal = 1u;

    /// <summary>A salvageable material, for the surfaces that only take one.</summary>
    public const uint Silver = 60u;

    /// <summary>A material with no name of its own: the caption stays plain.</summary>
    public const uint Unnamed = 61u;

    private static readonly RetailAppraisalNameResolver Resolver = new(
        new Dictionary<uint, string> { [Pyreal] = "Pyreal", [Silver] = "Silver" },
        new CreatureDisplayNameResolver(new Dictionary<uint, string>()));

    public static Func<ClientObject, string> Resolve => Resolver.ResolveAppropriateName;

    /// <summary>An item whose name only reads correctly with its material.</summary>
    public static ClientObject Material(uint guid, uint material = Pyreal) => new()
    {
        ObjectId = guid,
        Name = "Scarab",
        PluralName = "Scarabs",
        MaterialType = material,
    };

    /// <summary>An item with no material: the caption is the plain name.</summary>
    public static ClientObject Plain(uint guid) => new()
    {
        ObjectId = guid,
        Name = "Bread Loaf",
    };
}
