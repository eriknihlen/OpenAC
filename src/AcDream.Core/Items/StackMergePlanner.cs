using System;

namespace AcDream.Core.Items;

public readonly record struct StackMergeItem(
    uint ObjectId,
    uint WeenieClassId,
    int StackSize,
    int MaxStackSize,
    int TradeState);

public readonly record struct StackMergePlan(uint SourceObjectId, uint TargetObjectId, uint Amount);

public static class StackMergePlanner
{
    public static StackMergePlan? Plan(
        in StackMergeItem source,
        in StackMergeItem target,
        bool readyForInventoryRequest,
        int requestedAmount)
    {
        if (!readyForInventoryRequest
            || source.ObjectId == 0
            || target.ObjectId == 0
            || source.ObjectId == target.ObjectId
            || source.MaxStackSize <= 1
            || target.MaxStackSize <= 1
            || source.TradeState == 1
            || target.TradeState == 1
            || source.WeenieClassId != target.WeenieClassId)
            return null;

        int targetSize = Math.Max(1, target.StackSize);
        int available = target.MaxStackSize - targetSize;
        if (available <= 0)
            return null;

        int sourceSize = Math.Max(1, source.StackSize);
        int desired = requestedAmount > 0 ? Math.Min(requestedAmount, sourceSize) : sourceSize;
        uint transfer = (uint)Math.Min(desired, available);
        return transfer == 0
            ? null
            : new StackMergePlan(source.ObjectId, target.ObjectId, transfer);
    }
}
