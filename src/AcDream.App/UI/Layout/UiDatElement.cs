using System;
using System.Numerics;

namespace AcDream.App.UI.Layout;

public class UiDatElement : UiElement, IUiDatStateful
{
#pragma warning disable IDE0051 // private constants kept for documentation / Plan 2
    private const int DrawUndefined   = 0;
    private const int DrawNormal      = 1;
    private const int DrawOverlay     = 2;
    private const int DrawAlphablend  = 3;
#pragma warning restore IDE0051

    protected readonly ElementInfo Info;
    private readonly Func<uint, (uint tex, int w, int h)> _resolve;

    public uint ElementId => Info.Id;

    /// <summary>Which state name to render. <c>""</c> = the unnamed DirectState.
    /// Falls back to DirectState if the named state is absent.</summary>
    public string ActiveState { get; set; } = "";

    public uint ActiveRetailStateId
    {
        get
        {
            if (string.IsNullOrEmpty(ActiveState))
                return UiStateInfo.DirectStateId;
            foreach (var (id, state) in Info.States)
                if (string.Equals(state.Name, ActiveState, StringComparison.Ordinal))
                    return id;
            return UiButtonStateMachine.TryStateId(ActiveState, out uint standard)
                ? standard
                : RetailUiStateIds.TryStateId(ActiveState, out uint custom) ? custom : 0u;
        }
    }

    public override string ActiveCursorStateName => ActiveState;

    public bool TrySetRetailState(uint stateId)
    {
        uint appliedStateId = stateId;
        UiStateInfo? selectedState = null;
        if (stateId == UiStateInfo.DirectStateId)
        {
            if (!Info.States.TryGetValue(stateId, out selectedState)
                && !Info.StateMedia.ContainsKey(""))
                return false;
            ActiveState = "";
        }
        else if (Info.States.TryGetValue(stateId, out selectedState))
        {
            ActiveState = selectedState.Name;
        }
        else
        {
            string stateName = UiButtonStateMachine.StateName(stateId);
            if (string.IsNullOrEmpty(stateName))
                stateName = RetailUiStateIds.StateName(stateId);
            if (string.IsNullOrEmpty(stateName) || !Info.StateMedia.ContainsKey(stateName))
            {
                ActiveState = "";
                appliedStateId = UiStateInfo.DirectStateId;
                Info.States.TryGetValue(appliedStateId, out selectedState);
            }
            else
            {
                ActiveState = stateName;
            }
        }

        if (selectedState is not null
            && selectedState.Properties.TryGetValue(0x3Bu, out var invisibleProp)
            && invisibleProp.Kind == UiPropertyKind.Bool)
            Visible = !invisibleProp.BoolValue;

        if (selectedState?.PassToChildren == true)
        {
            foreach (UiElement child in Children)
                if (child is IUiDatStateful stateful)
                    stateful.TrySetRetailState(appliedStateId);
        }
        return true;
    }

    public UiDatElement(ElementInfo info, Func<uint, (uint tex, int w, int h)> resolve)
    {
        Info = info;
        _resolve = resolve;
        ClickThrough = true; // generic decoration; behavioral widgets opt back in

        if (!string.IsNullOrEmpty(info.DefaultStateName))
            ActiveState = info.DefaultStateName;
        else if (info.StateMedia.ContainsKey("Normal"))
            ActiveState = "Normal";
        // else ActiveState stays "" (DirectState)

        // Outline 0x21 / OutlineColor 0x22 from the effective-default state, mirroring
        // DatWidgetFactory.BuildText's seed of UiText (round-5 review S2).
        Outline = info.Outline;
        if (info.OutlineColor.HasValue)
            OutlineColor = info.OutlineColor.Value;
    }

    // exposed for unit testing
    public (uint File, int DrawMode) ActiveMedia()
        => Info.StateMedia.TryGetValue(ActiveState, out var m) ? m
         : Info.StateMedia.TryGetValue("", out var d) ? d
         : (0u, 0);

    public Action? OnClick { get; set; }
    public Action<int, int>? OnClickAt { get; set; }

    public override bool HandlesClick => OnClick is not null || OnClickAt is not null;

    public override bool OnEvent(in UiEvent e)
    {
        if (e.Type == UiEventType.Click && (OnClick is not null || OnClickAt is not null))
        {
            OnClick?.Invoke();
            OnClickAt?.Invoke(e.Data1, e.Data2);
            return true;
        }
        return false;
    }

    public string? Label { get; set; }
    public UiDatFont? LabelFont { get; set; }
    public Vector4 LabelColor { get; set; } = Vector4.One;

    public Vector4 Tint { get; set; } = Vector4.One;

    public bool Outline { get; set; }

    public Vector4 OutlineColor { get; set; } = UiRenderContext.DefaultOutlineColor;

    public bool MediaVisible { get; set; } = true;

    public uint? RuntimeImageTexture { get; set; }

    protected override void OnDraw(UiRenderContext ctx)
    {
        if (MediaVisible && RuntimeImageTexture is uint runtimeTexture)
        {
            if (runtimeTexture != 0u)
            {
                ctx.DrawSprite(
                    runtimeTexture,
                    0f,
                    0f,
                    Width,
                    Height,
                    0f,
                    0f,
                    1f,
                    1f,
                    Tint);
            }
            DrawLabel(ctx);
            return;
        }

        var (file, _) = ActiveMedia();
        if (MediaVisible && file != 0)
        {
            var (tex, tw, th) = _resolve(file);
            if (tex != 0 && tw != 0 && th != 0)
            {
                ctx.DrawSprite(tex, 0, 0, Width, Height, 0, 0, Width / tw, Height / th, Tint);
            }
        }

        DrawLabel(ctx);
    }

    private void DrawLabel(UiRenderContext ctx)
    {
        if (Label is { Length: > 0 } label && LabelFont is { } lf)
        {
            float tx = (Width - lf.MeasureWidth(label)) * 0.5f;
            float ty = (Height - lf.LineHeight) * 0.5f;
            ctx.DrawStringDat(lf, label, tx, ty, LabelColor, Outline, OutlineColor);
        }
    }
}
