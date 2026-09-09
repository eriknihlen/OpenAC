namespace AcDream.Core.Physics;

public static class RetailObjectManagerTail
{
    public static void Run(
        Motion.TargetManager? target,
        Motion.MovementManager? movement,
        Motion.MotionTableManager? partArray,
        Motion.PositionManager? position)
    {
        target?.HandleTargetting();
        movement?.UseTime();
        partArray?.UseTime();
        position?.UseTime();
    }

    public static void Run(
        Action? handleTargeting,
        Motion.MovementManager? movement,
        Action? partArrayHandleMovement,
        Motion.PositionManager? position)
    {
        handleTargeting?.Invoke();
        movement?.UseTime();
        partArrayHandleMovement?.Invoke();
        position?.UseTime();
    }

    public static void Run(
        Action? checkDetection,
        Action? handleTargeting,
        Action? movementUseTime,
        Action? partArrayHandleMovement,
        Action? positionUseTime)
    {
        checkDetection?.Invoke();
        handleTargeting?.Invoke();
        movementUseTime?.Invoke();
        partArrayHandleMovement?.Invoke();
        positionUseTime?.Invoke();
    }
}
