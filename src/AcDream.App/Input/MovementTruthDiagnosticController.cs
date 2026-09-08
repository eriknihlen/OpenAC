using System.Numerics;
using AcDream.Core.Net;

namespace AcDream.App.Input;

internal sealed class MovementTruthDiagnosticController
    : IMovementTruthDiagnosticSink
{
    private readonly bool _enabled;
    private readonly IRuntimeLocalPlayerControllerSource _player;
    private readonly ILocalPlayerIdentitySource _identity;
    private MovementTruthOutbound? _lastOutbound;

    private readonly record struct MovementTruthOutbound(
        string Kind,
        uint Sequence,
        DateTime TimeUtc,
        Vector3 LocalWorldPosition,
        Vector3 WirePosition,
        uint WireCellId,
        bool IsOnGround,
        byte ContactByte,
        Vector3 Velocity);

    public MovementTruthDiagnosticController(
        bool enabled,
        IRuntimeLocalPlayerControllerSource player,
        ILocalPlayerIdentitySource identity)
    {
        _enabled = enabled;
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
    }

    public void OnOutbound(
        string kind,
        uint sequence,
        MovementResult result,
        Vector3 wirePosition,
        uint wireCellId,
        byte contactByte)
    {
        if (!_enabled)
            return;

        Vector3 velocity = _player.Controller?.BodyVelocity ?? Vector3.Zero;
        _lastOutbound = new MovementTruthOutbound(
            kind,
            sequence,
            DateTime.UtcNow,
            result.Position,
            wirePosition,
            wireCellId,
            result.IsOnGround,
            contactByte,
            velocity);

        Console.WriteLine(FormattableString.Invariant(
            $"move-truth OUT kind={kind} seq={sequence} local={Fmt(result.Position)} localCell=0x{result.CellId:X8} wire={Fmt(wirePosition)} wireCell=0x{wireCellId:X8} grounded={result.IsOnGround} contact={contactByte} vel={Fmt(velocity)} f={FmtCmd(result.ForwardCommand)} s={FmtCmd(result.SidestepCommand)} t={FmtCmd(result.TurnCommand)}"));
    }

    public void OnServerEcho(
        WorldSession.EntityPositionUpdate update,
        Vector3 serverWorldPosition)
    {
        if (!_enabled || update.Guid != _identity.ServerGuid)
            return;

        DateTime now = DateTime.UtcNow;
        PlayerMovementController? controller = _player.Controller;
        Vector3? localPosition = controller?.Position;
        uint? localCellId = controller?.CellId;
        Vector3? deltaLocal = localPosition.HasValue
            ? serverWorldPosition - localPosition.Value
            : null;

        string localText = localPosition.HasValue ? Fmt(localPosition.Value) : "-";
        string localCellText = localCellId.HasValue
            ? FormattableString.Invariant($"0x{localCellId.Value:X8}")
            : "-";
        string deltaLocalText = deltaLocal.HasValue ? Fmt(deltaLocal.Value) : "-";
        string deltaLocalLength = deltaLocal.HasValue
            ? FormattableString.Invariant($"{deltaLocal.Value.Length():F3}")
            : "-";

        string lastText = "-";
        if (_lastOutbound is { } last)
        {
            Vector3 deltaOut = serverWorldPosition - last.LocalWorldPosition;
            double ageMs = (now - last.TimeUtc).TotalMilliseconds;
            lastText = FormattableString.Invariant(
                $"{last.Kind}:{last.Sequence} ageMs={ageMs:F0} outGrounded={last.IsOnGround} outContact={last.ContactByte} outCell=0x{last.WireCellId:X8} deltaOut={Fmt(deltaOut)} distOut={deltaOut.Length():F3}");
        }

        string state = controller?.State.ToString() ?? "-";
        string velocityText = update.Velocity.HasValue
            ? Fmt(update.Velocity.Value)
            : "-";

        Console.WriteLine(FormattableString.Invariant(
            $"move-truth ECHO guid=0x{update.Guid:X8} server={Fmt(serverWorldPosition)} serverCell=0x{update.Position.LandblockId:X8} local={localText} localCell={localCellText} deltaLocal={deltaLocalText} distLocal={deltaLocalLength} serverVel={velocityText} state={state} lastOut={lastText}"));
    }

    public void ResetSession() => _lastOutbound = null;

    private static string Fmt(Vector3 value) =>
        FormattableString.Invariant(
            $"({value.X:F3},{value.Y:F3},{value.Z:F3})");

    private static string FmtCmd(uint? command) =>
        command.HasValue
            ? FormattableString.Invariant($"0x{command.Value:X8}")
            : "-";
}
