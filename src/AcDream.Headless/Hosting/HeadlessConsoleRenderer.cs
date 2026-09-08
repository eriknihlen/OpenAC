using AcDream.Runtime;

namespace AcDream.Headless.Hosting;

internal sealed class HeadlessConsoleRenderer : IRuntimeEventObserver
{
    private const string Reset = "[0m";
    private const string Dim = "[2m";

    private readonly TextWriter _output;
    private readonly bool _useColor;

    internal HeadlessConsoleRenderer(TextWriter output, bool useColor)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _useColor = useColor;
    }

    public void OnChat(in RuntimeChatDelta delta)
    {
        string? line = HeadlessConsoleChatFormatter.Format(delta.Entry);
        if (!string.IsNullOrEmpty(line))
            WriteLine(line, dim: false);
    }

    internal void WriteInterfaceText(string text) => WriteLine(text, dim: false);

    public void OnLifecycle(in RuntimeLifecycleDelta delta)
    {
        switch (delta.Current)
        {
            case RuntimeLifecycleState.InWorld:
                WriteLine("entered world", dim: true);
                break;
            case RuntimeLifecycleState.Stopping:
                WriteLine("disconnecting", dim: true);
                break;
            case RuntimeLifecycleState.Faulted:
                WriteLine("session faulted", dim: true);
                break;
        }
    }

    public void OnCommand(in RuntimeCommandDelta delta)
    {
        if (delta.Status == RuntimeCommandStatus.Rejected)
        {
            WriteLine(
                $"command rejected: {delta.Domain} {delta.Text}".TrimEnd(),
                dim: true);
        }
    }

    public void OnPortal(in RuntimePortalDelta delta)
    {
        if (delta.Portal.IsMaterialized)
        {
            WriteLine(
                $"portal -> cell 0x{delta.Portal.DestinationCell:X8}",
                dim: true);
        }
    }

    public void OnEntity(in RuntimeEntityDelta delta)
    {
    }

    public void OnInventory(in RuntimeInventoryDelta delta)
    {
    }

    public void OnMovement(in RuntimeMovementDelta delta)
    {
    }

    public void OnCombat(in RuntimeCombatDelta delta)
    {
    }

    private void WriteLine(string text, bool dim)
    {
        _output.WriteLine(_useColor && dim ? Dim + text + Reset : text);
        _output.Flush();
    }
}
