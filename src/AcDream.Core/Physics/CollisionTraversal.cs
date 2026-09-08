using System.Numerics;

namespace AcDream.Core.Physics;

internal enum CollisionTraversalMode
{
    Graph,
    Flat,
}

internal static class CollisionTraversal
{
    internal static bool HasCellContainment(
        PhysicsDataCache cache,
        CellPhysics cell)
    {
        if (UseFlat(cache))
        {
            FlatCellContainmentBsp flat = cell.FlatContainmentBsp ??
                throw MissingFlat("cell containment");
            bool flatAuthorityResult = flat.RootIndex >= 0;
            CollisionShadowVerifier? flatShadow = cache.CollisionShadow;
            if (flatShadow is null ||
                !flatShadow.TrySample(out long flatAuthoritySample))
                return flatAuthorityResult;

            bool graphRefereeResult = false;
            Exception? graphRefereeFault = null;
            flatShadow.BeginGraphPass();
            try
            {
                graphRefereeResult = cell.CellBSP?.Root is not null;
            }
            catch (Exception fault)
            {
                graphRefereeFault = fault;
            }
            finally
            {
                flatShadow.EndGraphPass();
            }

            if (graphRefereeFault is null)
            {
                flatShadow.RecordBoolean(
                    flatAuthoritySample,
                    "HasCellContainment",
                    cell.SourceId,
                    graphRefereeResult,
                    flatAuthorityResult,
                    string.Empty);
            }
            else
            {
                flatShadow.RecordFault(
                    flatAuthoritySample,
                    "HasCellContainment",
                    cell.SourceId,
                    graphRefereeFault,
                    string.Empty,
                    "flat");
            }
            return flatAuthorityResult;
        }

        CollisionShadowVerifier? shadow = cache.CollisionShadow;
        if (shadow is null || !shadow.TrySample(out long sample))
            return cell.CellBSP?.Root is not null;

        bool flatResult = false;
        Exception? flatFault = null;
        shadow.BeginFlatPass();
        try
        {
            flatResult = (cell.FlatContainmentBsp ??
                throw MissingFlat("cell containment")).RootIndex >= 0;
        }
        catch (Exception fault)
        {
            flatFault = fault;
        }
        finally
        {
            shadow.EndFlatPass();
        }

        bool graphResult = cell.CellBSP?.Root is not null;
        if (flatFault is null)
        {
            shadow.RecordBoolean(
                sample,
                "HasCellContainment",
                cell.SourceId,
                graphResult,
                flatResult,
                string.Empty);
        }
        else
        {
            shadow.RecordFault(
                sample,
                "HasCellContainment",
                cell.SourceId,
                flatFault,
                string.Empty);
        }
        return graphResult;
    }

    internal static bool HasPhysics(
        PhysicsDataCache cache,
        CellPhysics cell)
    {
        if (UseFlat(cache))
        {
            FlatPhysicsBsp flat = cell.FlatPhysicsBsp ??
                throw MissingFlat("cell physics");
            bool flatAuthorityResult = flat.RootIndex >= 0;
            CollisionShadowVerifier? flatShadow = cache.CollisionShadow;
            if (flatShadow is null ||
                !flatShadow.TrySample(out long flatAuthoritySample))
                return flatAuthorityResult;

            bool graphRefereeResult = false;
            Exception? graphRefereeFault = null;
            flatShadow.BeginGraphPass();
            try
            {
                graphRefereeResult = cell.BSP?.Root is not null;
            }
            catch (Exception fault)
            {
                graphRefereeFault = fault;
            }
            finally
            {
                flatShadow.EndGraphPass();
            }

            if (graphRefereeFault is null)
            {
                flatShadow.RecordBoolean(
                    flatAuthoritySample,
                    "HasCellPhysics",
                    cell.SourceId,
                    graphRefereeResult,
                    flatAuthorityResult,
                    string.Empty);
            }
            else
            {
                flatShadow.RecordFault(
                    flatAuthoritySample,
                    "HasCellPhysics",
                    cell.SourceId,
                    graphRefereeFault,
                    string.Empty,
                    "flat");
            }
            return flatAuthorityResult;
        }

        CollisionShadowVerifier? shadow = cache.CollisionShadow;
        if (shadow is null || !shadow.TrySample(out long sample))
            return cell.BSP?.Root is not null;

        bool flatResult = false;
        Exception? flatFault = null;
        shadow.BeginFlatPass();
        try
        {
            flatResult = (cell.FlatPhysicsBsp ??
                throw MissingFlat("cell physics")).RootIndex >= 0;
        }
        catch (Exception fault)
        {
            flatFault = fault;
        }
        finally
        {
            shadow.EndFlatPass();
        }

        bool graphResult = cell.BSP?.Root is not null;
        if (flatFault is null)
        {
            shadow.RecordBoolean(
                sample,
                "HasCellPhysics",
                cell.SourceId,
                graphResult,
                flatResult,
                string.Empty);
        }
        else
        {
            shadow.RecordFault(
                sample,
                "HasCellPhysics",
                cell.SourceId,
                flatFault,
                string.Empty);
        }
        return graphResult;
    }

    internal static bool HasPhysics(
        PhysicsDataCache cache,
        GfxObjPhysics gfxObject)
    {
        if (UseFlat(cache))
        {
            FlatPhysicsBsp flat = gfxObject.FlatPhysicsBsp ??
                throw MissingFlat("GfxObj physics");
            bool flatAuthorityResult = flat.RootIndex >= 0;
            CollisionShadowVerifier? flatShadow = cache.CollisionShadow;
            if (flatShadow is null ||
                !flatShadow.TrySample(out long flatAuthoritySample))
                return flatAuthorityResult;

            bool graphRefereeResult = false;
            Exception? graphRefereeFault = null;
            flatShadow.BeginGraphPass();
            try
            {
                graphRefereeResult = gfxObject.BSP?.Root is not null;
            }
            catch (Exception fault)
            {
                graphRefereeFault = fault;
            }
            finally
            {
                flatShadow.EndGraphPass();
            }

            if (graphRefereeFault is null)
            {
                flatShadow.RecordBoolean(
                    flatAuthoritySample,
                    "HasGfxObjPhysics",
                    gfxObject.SourceId,
                    graphRefereeResult,
                    flatAuthorityResult,
                    string.Empty);
            }
            else
            {
                flatShadow.RecordFault(
                    flatAuthoritySample,
                    "HasGfxObjPhysics",
                    gfxObject.SourceId,
                    graphRefereeFault,
                    string.Empty,
                    "flat");
            }
            return flatAuthorityResult;
        }

        CollisionShadowVerifier? shadow = cache.CollisionShadow;
        if (shadow is null || !shadow.TrySample(out long sample))
            return gfxObject.BSP?.Root is not null;

        bool flatResult = false;
        Exception? flatFault = null;
        shadow.BeginFlatPass();
        try
        {
            flatResult = (gfxObject.FlatPhysicsBsp ??
                throw MissingFlat("GfxObj physics")).RootIndex >= 0;
        }
        catch (Exception fault)
        {
            flatFault = fault;
        }
        finally
        {
            shadow.EndFlatPass();
        }

        bool graphResult = gfxObject.BSP?.Root is not null;
        if (flatFault is null)
        {
            shadow.RecordBoolean(
                sample,
                "HasGfxObjPhysics",
                gfxObject.SourceId,
                graphResult,
                flatResult,
                string.Empty);
        }
        else
        {
            shadow.RecordFault(
                sample,
                "HasGfxObjPhysics",
                gfxObject.SourceId,
                flatFault,
                string.Empty);
        }
        return graphResult;
    }

    internal static FlatCollisionSphere RootBoundingSphere(
        PhysicsDataCache cache,
        CellPhysics cell)
    {
        if (UseFlat(cache))
        {
            FlatPhysicsBsp flat = cell.FlatPhysicsBsp ??
                throw MissingFlat("cell physics");
            FlatCollisionSphere flatAuthorityResult =
                flat.Nodes[flat.RootIndex].BoundingSphere;
            CollisionShadowVerifier? flatShadow = cache.CollisionShadow;
            if (flatShadow is null ||
                !flatShadow.TrySample(out long flatAuthoritySample))
                return flatAuthorityResult;

            try
            {
                flatShadow.BeginGraphPass();
                DatReaderWriter.Types.Sphere graphSphere =
                    cell.BSP!.Root!.BoundingSphere;
                var graphRefereeResult =
                    new FlatCollisionSphere(
                        graphSphere.Origin,
                        graphSphere.Radius);
                flatShadow.EndGraphPass();
                flatShadow.RecordSphere(
                    flatAuthoritySample,
                    "RootBoundingSphere",
                    cell.SourceId,
                    graphRefereeResult,
                    flatAuthorityResult);
            }
            catch (Exception fault)
            {
                if (flatShadow.IsGraphPass)
                    flatShadow.EndGraphPass();
                flatShadow.RecordFault(
                    flatAuthoritySample,
                    "RootBoundingSphere",
                    cell.SourceId,
                    fault,
                    string.Empty,
                    "flat");
            }
            return flatAuthorityResult;
        }

        DatReaderWriter.Types.Sphere sphere = cell.BSP!.Root!.BoundingSphere;
        var graphResult =
            new FlatCollisionSphere(sphere.Origin, sphere.Radius);
        CollisionShadowVerifier? shadow = cache.CollisionShadow;
        if (shadow is null || !shadow.TrySample(out long sample))
            return graphResult;

        try
        {
            shadow.BeginFlatPass();
            FlatPhysicsBsp flat = cell.FlatPhysicsBsp ??
                throw MissingFlat("cell physics");
            FlatCollisionSphere flatResult =
                flat.Nodes[flat.RootIndex].BoundingSphere;
            shadow.EndFlatPass();
            shadow.RecordSphere(
                sample,
                "RootBoundingSphere",
                cell.SourceId,
                graphResult,
                flatResult);
        }
        catch (Exception fault)
        {
            if (shadow.IsFlatPass)
                shadow.EndFlatPass();
            shadow.RecordFault(
                sample,
                "RootBoundingSphere",
                cell.SourceId,
                fault,
                string.Empty);
        }
        return graphResult;
    }

    internal static bool PointInsideCell(
        PhysicsDataCache cache,
        CellPhysics cell,
        Vector3 localPoint)
    {
        if (UseFlat(cache))
        {
            FlatCellContainmentBsp flat = cell.FlatContainmentBsp ??
                throw MissingFlat("cell containment");
            bool flatAuthorityResult =
                FlatBspQuery.PointInsideCellBsp(flat, localPoint);
            CollisionShadowVerifier? flatShadow = cache.CollisionShadow;
            if (flatShadow is null ||
                !flatShadow.TrySample(out long flatAuthoritySample))
                return flatAuthorityResult;

            bool graphRefereeResult = false;
            Exception? graphRefereeFault = null;
            flatShadow.BeginGraphPass();
            try
            {
                graphRefereeResult = BSPQuery.PointInsideCellBsp(
                    cell.CellBSP?.Root,
                    localPoint);
            }
            catch (Exception fault)
            {
                graphRefereeFault = fault;
            }
            finally
            {
                flatShadow.EndGraphPass();
            }
            string flatAuthorityInput = CollisionShadowVerifier.FormatInput(
                localPoint,
                0f);
            if (graphRefereeFault is null)
            {
                flatShadow.RecordBoolean(
                    flatAuthoritySample,
                    "PointInsideCell",
                    cell.SourceId,
                    graphRefereeResult,
                    flatAuthorityResult,
                    flatAuthorityInput);
            }
            else
            {
                flatShadow.RecordFault(
                    flatAuthoritySample,
                    "PointInsideCell",
                    cell.SourceId,
                    graphRefereeFault,
                    flatAuthorityInput,
                    "flat");
            }
            return flatAuthorityResult;
        }

        CollisionShadowVerifier? shadow = cache.CollisionShadow;
        if (shadow is null || !shadow.TrySample(out long sample))
            return BSPQuery.PointInsideCellBsp(
                cell.CellBSP?.Root,
                localPoint);

        bool flatResult = false;
        Exception? flatFault = null;
        shadow.BeginFlatPass();
        try
        {
            flatResult = FlatBspQuery.PointInsideCellBsp(
                cell.FlatContainmentBsp ??
                    throw MissingFlat("cell containment"),
                localPoint);
        }
        catch (Exception fault)
        {
            flatFault = fault;
        }
        finally
        {
            shadow.EndFlatPass();
        }
        bool graphResult = BSPQuery.PointInsideCellBsp(
            cell.CellBSP?.Root,
            localPoint);
        string input = CollisionShadowVerifier.FormatInput(
            localPoint,
            0f);
        if (flatFault is null)
        {
            shadow.RecordBoolean(
                sample,
                "PointInsideCell",
                cell.SourceId,
                graphResult,
                flatResult,
                input);
        }
        else
        {
            shadow.RecordFault(
                sample,
                "PointInsideCell",
                cell.SourceId,
                flatFault,
                input);
        }
        return graphResult;
    }

    internal static bool SphereIntersectsCell(
        PhysicsDataCache cache,
        CellPhysics cell,
        Vector3 localCenter,
        float radius)
    {
        if (UseFlat(cache))
        {
            FlatCellContainmentBsp flat = cell.FlatContainmentBsp ??
                throw MissingFlat("cell containment");
            bool flatAuthorityResult = FlatBspQuery.SphereIntersectsCellBsp(
                flat,
                localCenter,
                radius);
            CollisionShadowVerifier? flatShadow = cache.CollisionShadow;
            if (flatShadow is null ||
                !flatShadow.TrySample(out long flatAuthoritySample))
                return flatAuthorityResult;

            bool graphRefereeResult = false;
            Exception? graphRefereeFault = null;
            flatShadow.BeginGraphPass();
            try
            {
                graphRefereeResult = BSPQuery.SphereIntersectsCellBsp(
                    cell.CellBSP?.Root,
                    localCenter,
                    radius);
            }
            catch (Exception fault)
            {
                graphRefereeFault = fault;
            }
            finally
            {
                flatShadow.EndGraphPass();
            }
            string flatAuthorityInput = CollisionShadowVerifier.FormatInput(
                localCenter,
                radius);
            if (graphRefereeFault is null)
            {
                flatShadow.RecordBoolean(
                    flatAuthoritySample,
                    "SphereIntersectsCell",
                    cell.SourceId,
                    graphRefereeResult,
                    flatAuthorityResult,
                    flatAuthorityInput);
            }
            else
            {
                flatShadow.RecordFault(
                    flatAuthoritySample,
                    "SphereIntersectsCell",
                    cell.SourceId,
                    graphRefereeFault,
                    flatAuthorityInput,
                    "flat");
            }
            return flatAuthorityResult;
        }

        CollisionShadowVerifier? shadow = cache.CollisionShadow;
        if (shadow is null || !shadow.TrySample(out long sample))
        {
            return BSPQuery.SphereIntersectsCellBsp(
                cell.CellBSP?.Root,
                localCenter,
                radius);
        }

        bool flatResult = false;
        Exception? flatFault = null;
        shadow.BeginFlatPass();
        try
        {
            flatResult = FlatBspQuery.SphereIntersectsCellBsp(
                cell.FlatContainmentBsp ??
                    throw MissingFlat("cell containment"),
                localCenter,
                radius);
        }
        catch (Exception fault)
        {
            flatFault = fault;
        }
        finally
        {
            shadow.EndFlatPass();
        }
        bool graphResult = BSPQuery.SphereIntersectsCellBsp(
            cell.CellBSP?.Root,
            localCenter,
            radius);
        string input = CollisionShadowVerifier.FormatInput(
            localCenter,
            radius);
        if (flatFault is null)
        {
            shadow.RecordBoolean(
                sample,
                "SphereIntersectsCell",
                cell.SourceId,
                graphResult,
                flatResult,
                input);
        }
        else
        {
            shadow.RecordFault(
                sample,
                "SphereIntersectsCell",
                cell.SourceId,
                flatFault,
                input);
        }
        return graphResult;
    }

    internal static bool BoxIntersectsCell(
        PhysicsDataCache cache,
        CellPhysics cell,
        Vector3 localMin,
        Vector3 localMax)
    {
        if (UseFlat(cache))
        {
            FlatCellContainmentBsp flat = cell.FlatContainmentBsp ??
                throw MissingFlat("cell containment");
            bool flatAuthorityResult = FlatBspQuery.BoxIntersectsCellBsp(
                flat,
                localMin,
                localMax);
            CollisionShadowVerifier? flatShadow = cache.CollisionShadow;
            if (flatShadow is null ||
                !flatShadow.TrySample(out long flatAuthoritySample))
                return flatAuthorityResult;

            bool graphRefereeResult = false;
            Exception? graphRefereeFault = null;
            flatShadow.BeginGraphPass();
            try
            {
                graphRefereeResult = BSPQuery.BoxIntersectsCellBsp(
                    cell.CellBSP?.Root,
                    localMin,
                    localMax);
            }
            catch (Exception fault)
            {
                graphRefereeFault = fault;
            }
            finally
            {
                flatShadow.EndGraphPass();
            }
            string flatAuthorityInput = CollisionShadowVerifier.FormatInput(
                localMin,
                localMax);
            if (graphRefereeFault is null)
            {
                flatShadow.RecordBoolean(
                    flatAuthoritySample,
                    "BoxIntersectsCell",
                    cell.SourceId,
                    graphRefereeResult,
                    flatAuthorityResult,
                    flatAuthorityInput);
            }
            else
            {
                flatShadow.RecordFault(
                    flatAuthoritySample,
                    "BoxIntersectsCell",
                    cell.SourceId,
                    graphRefereeFault,
                    flatAuthorityInput,
                    "flat");
            }
            return flatAuthorityResult;
        }

        CollisionShadowVerifier? shadow = cache.CollisionShadow;
        if (shadow is null || !shadow.TrySample(out long sample))
        {
            return BSPQuery.BoxIntersectsCellBsp(
                cell.CellBSP?.Root,
                localMin,
                localMax);
        }

        bool flatResult = false;
        Exception? flatFault = null;
        shadow.BeginFlatPass();
        try
        {
            flatResult = FlatBspQuery.BoxIntersectsCellBsp(
                cell.FlatContainmentBsp ??
                    throw MissingFlat("cell containment"),
                localMin,
                localMax);
        }
        catch (Exception fault)
        {
            flatFault = fault;
        }
        finally
        {
            shadow.EndFlatPass();
        }
        bool graphResult = BSPQuery.BoxIntersectsCellBsp(
            cell.CellBSP?.Root,
            localMin,
            localMax);
        string input = CollisionShadowVerifier.FormatInput(localMin, localMax);
        if (flatFault is null)
        {
            shadow.RecordBoolean(
                sample,
                "BoxIntersectsCell",
                cell.SourceId,
                graphResult,
                flatResult,
                input);
        }
        else
        {
            shadow.RecordFault(
                sample,
                "BoxIntersectsCell",
                cell.SourceId,
                flatFault,
                input);
        }
        return graphResult;
    }

    internal static TransitionState FindCollisions(
        PhysicsDataCache cache,
        CellPhysics cell,
        Transition transition,
        Vector3 localSphereCenter,
        float localSphereRadius,
        bool hasLocalSphere1,
        Vector3 localSphere1Center,
        float localSphere1Radius,
        Vector3 localCurrentCenter,
        Vector3 localSpaceZ,
        float scale,
        Quaternion localToWorld,
        PhysicsEngine? engine,
        Vector3 worldOrigin)
    {
        if (UseFlat(cache))
        {
            CollisionShadowVerifier? flatShadow = cache.CollisionShadow;
            long flatAuthoritySample = 0;
            bool sampled =
                flatShadow is not null &&
                flatShadow.TrySample(out flatAuthoritySample);
            Transition? graphTransition = sampled
                ? flatShadow!.PrepareShadow(transition)
                : null;
            FlatPhysicsBsp flat = cell.FlatPhysicsBsp ??
                throw MissingFlat("cell physics");
            TransitionState flatAuthorityState = FlatBspQuery.FindCollisions(
                flat,
                transition,
                localSphereCenter,
                localSphereRadius,
                hasLocalSphere1,
                localSphere1Center,
                localSphere1Radius,
                localCurrentCenter,
                localSpaceZ,
                scale,
                localToWorld,
                engine,
                worldOrigin);
            if (!sampled)
                return flatAuthorityState;

            TransitionState graphRefereeState = TransitionState.Invalid;
            Exception? graphRefereeFault = null;
            flatShadow!.BeginGraphPass();
            try
            {
                graphRefereeState = BSPQuery.FindCollisions(
                    cell.BSP?.Root,
                    cell.Resolved,
                    graphTransition!,
                    localSphereCenter,
                    localSphereRadius,
                    hasLocalSphere1,
                    localSphere1Center,
                    localSphere1Radius,
                    localCurrentCenter,
                    localSpaceZ,
                    scale,
                    localToWorld,
                    engine,
                    worldOrigin);
            }
            catch (Exception fault)
            {
                graphRefereeFault = fault;
            }
            finally
            {
                flatShadow.EndGraphPass();
            }

            string flatAuthorityInput = CollisionShadowVerifier.FormatInput(
                localSphereCenter,
                localSphereRadius,
                hasLocalSphere1,
                localSphere1Center,
                localSphere1Radius,
                localCurrentCenter,
                localSpaceZ,
                scale,
                localToWorld,
                worldOrigin);
            if (graphRefereeFault is null)
            {
                flatShadow.RecordTransition(
                    flatAuthoritySample,
                    "FindCellCollisions",
                    cell.SourceId,
                    graphRefereeState,
                    flatAuthorityState,
                    graphTransition!,
                    transition,
                    flatAuthorityInput);
            }
            else
            {
                flatShadow.RecordFault(
                    flatAuthoritySample,
                    "FindCellCollisions",
                    cell.SourceId,
                    graphRefereeFault,
                    flatAuthorityInput,
                    "flat");
            }
            return flatAuthorityState;
        }

        CollisionShadowVerifier? shadow = cache.CollisionShadow;
        if (shadow is null || !shadow.TrySample(out long sample))
        {
            return BSPQuery.FindCollisions(
                cell.BSP?.Root,
                cell.Resolved,
                transition,
                localSphereCenter,
                localSphereRadius,
                hasLocalSphere1,
                localSphere1Center,
                localSphere1Radius,
                localCurrentCenter,
                localSpaceZ,
                scale,
                localToWorld,
                engine,
                worldOrigin);
        }

        Transition flatTransition = shadow.PrepareShadow(transition);
        TransitionState flatState = TransitionState.Invalid;
        Exception? flatFault = null;
        shadow.BeginFlatPass();
        try
        {
            flatState = FlatBspQuery.FindCollisions(
                cell.FlatPhysicsBsp ??
                    throw MissingFlat("cell physics"),
                flatTransition,
                localSphereCenter,
                localSphereRadius,
                hasLocalSphere1,
                localSphere1Center,
                localSphere1Radius,
                localCurrentCenter,
                localSpaceZ,
                scale,
                localToWorld,
                engine,
                worldOrigin);
        }
        catch (Exception fault)
        {
            flatFault = fault;
        }
        finally
        {
            shadow.EndFlatPass();
        }

        TransitionState graphState = BSPQuery.FindCollisions(
            cell.BSP?.Root,
            cell.Resolved,
            transition,
            localSphereCenter,
            localSphereRadius,
            hasLocalSphere1,
            localSphere1Center,
            localSphere1Radius,
            localCurrentCenter,
            localSpaceZ,
            scale,
            localToWorld,
            engine,
            worldOrigin);
        string input = CollisionShadowVerifier.FormatInput(
            localSphereCenter,
            localSphereRadius,
            hasLocalSphere1,
            localSphere1Center,
            localSphere1Radius,
            localCurrentCenter,
            localSpaceZ,
            scale,
            localToWorld,
            worldOrigin);
        if (flatFault is null)
        {
            shadow.RecordTransition(
                sample,
                "FindCellCollisions",
                cell.SourceId,
                graphState,
                flatState,
                transition,
                flatTransition,
                input);
        }
        else
        {
            shadow.RecordFault(
                sample,
                "FindCellCollisions",
                cell.SourceId,
                flatFault,
                input);
        }
        return graphState;
    }

    internal static TransitionState FindCollisions(
        PhysicsDataCache cache,
        GfxObjPhysics gfxObject,
        Transition transition,
        Vector3 localSphereCenter,
        float localSphereRadius,
        bool hasLocalSphere1,
        Vector3 localSphere1Center,
        float localSphere1Radius,
        Vector3 localCurrentCenter,
        Vector3 localSpaceZ,
        float scale,
        Quaternion localToWorld,
        PhysicsEngine? engine,
        Vector3 worldOrigin)
    {
        if (UseFlat(cache))
        {
            CollisionShadowVerifier? flatShadow = cache.CollisionShadow;
            long flatAuthoritySample = 0;
            bool sampled =
                flatShadow is not null &&
                flatShadow.TrySample(out flatAuthoritySample);
            Transition? graphTransition = sampled
                ? flatShadow!.PrepareShadow(transition)
                : null;
            FlatPhysicsBsp flat = gfxObject.FlatPhysicsBsp ??
                throw MissingFlat("GfxObj physics");
            TransitionState flatAuthorityState = FlatBspQuery.FindCollisions(
                flat,
                transition,
                localSphereCenter,
                localSphereRadius,
                hasLocalSphere1,
                localSphere1Center,
                localSphere1Radius,
                localCurrentCenter,
                localSpaceZ,
                scale,
                localToWorld,
                engine,
                worldOrigin);
            if (!sampled)
                return flatAuthorityState;

            TransitionState graphRefereeState = TransitionState.Invalid;
            Exception? graphRefereeFault = null;
            flatShadow!.BeginGraphPass();
            try
            {
                graphRefereeState = BSPQuery.FindCollisions(
                    gfxObject.BSP!.Root!,
                    gfxObject.Resolved,
                    graphTransition!,
                    localSphereCenter,
                    localSphereRadius,
                    hasLocalSphere1,
                    localSphere1Center,
                    localSphere1Radius,
                    localCurrentCenter,
                    localSpaceZ,
                    scale,
                    localToWorld,
                    engine,
                    worldOrigin);
            }
            catch (Exception fault)
            {
                graphRefereeFault = fault;
            }
            finally
            {
                flatShadow.EndGraphPass();
            }

            string flatAuthorityInput = CollisionShadowVerifier.FormatInput(
                localSphereCenter,
                localSphereRadius,
                hasLocalSphere1,
                localSphere1Center,
                localSphere1Radius,
                localCurrentCenter,
                localSpaceZ,
                scale,
                localToWorld,
                worldOrigin);
            if (graphRefereeFault is null)
            {
                flatShadow.RecordTransition(
                    flatAuthoritySample,
                    "FindGfxObjCollisions",
                    gfxObject.SourceId,
                    graphRefereeState,
                    flatAuthorityState,
                    graphTransition!,
                    transition,
                    flatAuthorityInput);
            }
            else
            {
                flatShadow.RecordFault(
                    flatAuthoritySample,
                    "FindGfxObjCollisions",
                    gfxObject.SourceId,
                    graphRefereeFault,
                    flatAuthorityInput,
                    "flat");
            }
            return flatAuthorityState;
        }

        CollisionShadowVerifier? shadow = cache.CollisionShadow;
        if (shadow is null || !shadow.TrySample(out long sample))
        {
            return BSPQuery.FindCollisions(
                gfxObject.BSP!.Root!,
                gfxObject.Resolved,
                transition,
                localSphereCenter,
                localSphereRadius,
                hasLocalSphere1,
                localSphere1Center,
                localSphere1Radius,
                localCurrentCenter,
                localSpaceZ,
                scale,
                localToWorld,
                engine,
                worldOrigin);
        }

        Transition flatTransition = shadow.PrepareShadow(transition);
        TransitionState flatState = TransitionState.Invalid;
        Exception? flatFault = null;
        shadow.BeginFlatPass();
        try
        {
            flatState = FlatBspQuery.FindCollisions(
                gfxObject.FlatPhysicsBsp ??
                    throw MissingFlat("GfxObj physics"),
                flatTransition,
                localSphereCenter,
                localSphereRadius,
                hasLocalSphere1,
                localSphere1Center,
                localSphere1Radius,
                localCurrentCenter,
                localSpaceZ,
                scale,
                localToWorld,
                engine,
                worldOrigin);
        }
        catch (Exception fault)
        {
            flatFault = fault;
        }
        finally
        {
            shadow.EndFlatPass();
        }

        TransitionState graphState = BSPQuery.FindCollisions(
            gfxObject.BSP!.Root!,
            gfxObject.Resolved,
            transition,
            localSphereCenter,
            localSphereRadius,
            hasLocalSphere1,
            localSphere1Center,
            localSphere1Radius,
            localCurrentCenter,
            localSpaceZ,
            scale,
            localToWorld,
            engine,
            worldOrigin);
        string input = CollisionShadowVerifier.FormatInput(
            localSphereCenter,
            localSphereRadius,
            hasLocalSphere1,
            localSphere1Center,
            localSphere1Radius,
            localCurrentCenter,
            localSpaceZ,
            scale,
            localToWorld,
            worldOrigin);
        if (flatFault is null)
        {
            shadow.RecordTransition(
                sample,
                "FindGfxObjCollisions",
                gfxObject.SourceId,
                graphState,
                flatState,
                transition,
                flatTransition,
                input);
        }
        else
        {
            shadow.RecordFault(
                sample,
                "FindGfxObjCollisions",
                gfxObject.SourceId,
                flatFault,
                input);
        }
        return graphState;
    }

    private static bool UseFlat(PhysicsDataCache cache)
    {
        CollisionShadowVerifier? shadow = cache.CollisionShadow;
        if (shadow?.IsGraphPass == true)
            return false;
        if (shadow?.IsFlatPass == true)
            return true;
        return cache.CollisionTraversalMode == CollisionTraversalMode.Flat;
    }

    private static InvalidOperationException MissingFlat(string kind) =>
        new(
            $"Flat collision traversal requires a prepared {kind} asset. " +
            "Gameplay must not fall back silently.");
}
