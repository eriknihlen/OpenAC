using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics.Motion;

public sealed class MovementTypeWideningTests
{
    [Theory]
    [InlineData(MovementType.Invalid, 0)]
    [InlineData(MovementType.RawCommand, 1)]
    [InlineData(MovementType.InterpretedCommand, 2)]
    [InlineData(MovementType.StopRawCommand, 3)]
    [InlineData(MovementType.StopInterpretedCommand, 4)]
    [InlineData(MovementType.StopCompletely, 5)]
    [InlineData(MovementType.MoveToObject, 6)]
    [InlineData(MovementType.MoveToPosition, 7)]
    [InlineData(MovementType.TurnToObject, 8)]
    [InlineData(MovementType.TurnToHeading, 9)]
    public void EnumValues_MatchRetailMovementTypesTypeTable(MovementType value, int expected)
        => Assert.Equal(expected, (int)value);
}
