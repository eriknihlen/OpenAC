namespace AcDream.App.UI.Layout;

internal static class RetailCombatLayout
{
    internal const int VisibleFavoriteSlots = 18;
    internal const float FavoriteCellWidth = 32f;

    internal static float FitFavoriteSlots(
        ImportedLayout layout,
        int visibleSlots = VisibleFavoriteSlots,
        float cellWidth = FavoriteCellWidth)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (visibleSlots < 1)
            throw new ArgumentOutOfRangeException(nameof(visibleSlots));
        if (!(cellWidth > 0f) || !float.IsFinite(cellWidth))
            throw new ArgumentOutOfRangeException(nameof(cellWidth));

        UiElement root = layout.Root;
        ApplyDescendantLayout(root);
        if (layout.FindElement(SpellcastingUiController.FavoriteListId) is not UiItemList list)
            throw new InvalidOperationException("Retail combat layout has no favorite spell list.");

        float targetViewport = visibleSlots * cellWidth;
        root.Width = MathF.Max(0f, root.Width + targetViewport - list.Width);
        ApplyDescendantLayout(root);
        return root.Width;
    }

    private static void ApplyDescendantLayout(UiElement parent)
    {
        foreach (UiElement child in parent.Children)
        {
            child.ApplyAnchor(parent.Width, parent.Height);
            ApplyDescendantLayout(child);
        }
    }
}
