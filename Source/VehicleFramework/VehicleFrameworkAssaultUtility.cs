using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace AIRsLight.ClashOfRim.ThirdPartyCompat.VehicleFramework;

internal sealed class LordJob_ClashVehicleAssault : LordJob
{
    private IntVec3 origin;
    private float radius;

    public LordJob_ClashVehicleAssault()
    {
    }

    public LordJob_ClashVehicleAssault(IntVec3 origin, float radius)
    {
        this.origin = origin;
        this.radius = radius;
    }

    public override StateGraph CreateGraph()
    {
        StateGraph graph = new();
        graph.AddToil(new LordToil_ClashVehicleAssault(origin, radius));
        return graph;
    }

    public override void ExposeData()
    {
        Scribe_Values.Look(ref origin, "origin");
        Scribe_Values.Look(ref radius, "radius");
    }
}

public sealed class JobGiver_ClashVehicleAssaultGoto : ThinkNode_JobGiver
{
    protected override Job? TryGiveJob(Pawn pawn)
    {
        if (pawn is null)
        {
            return null;
        }

        PawnDuty? duty = pawn.mindState?.duty;
        if (duty is null
            || !VehicleFrameworkAssaultUtility.TryFindAssaultDestination(
                pawn,
                duty.focus.Cell,
                duty.radius,
                out IntVec3 destination))
        {
            return null;
        }

        if (destination == pawn.Position || destination.DistanceToSquared(pawn.Position) <= 4)
        {
            pawn.pather?.StopDead();
            return JobMaker.MakeJob(JobDefOf.Wait_Combat, 180, true);
        }

        if (pawn.CurJobDef == JobDefOf.Goto && pawn.CurJob?.targetA.Cell == destination)
        {
            return null;
        }

        VehicleFrameworkAssaultUtility.SetVehicleDrafted(pawn, drafted: true);
        Job job = JobMaker.MakeJob(JobDefOf.Goto, destination);
        job.locomotionUrgency = LocomotionUrgency.Jog;
        job.checkOverrideOnExpire = true;
        job.expiryInterval = 240;
        job.collideWithPawns = true;
        return job;
    }
}

internal sealed class LordToil_ClashVehicleAssault : LordToil
{
    private const string VehicleAssaultDutyDefName = "ClashOfRim_VehicleDefenseAssault";

    private IntVec3 origin;
    private float radius;

    public LordToil_ClashVehicleAssault()
    {
    }

    public LordToil_ClashVehicleAssault(IntVec3 origin, float radius)
    {
        this.origin = origin;
        this.radius = radius;
    }

    public override IntVec3 FlagLoc => origin;

    public override bool AllowSatisfyLongNeeds => false;

    public override void UpdateAllDuties()
    {
        foreach (Pawn pawn in lord.ownedPawns)
        {
            if (pawn?.mindState is null)
            {
                continue;
            }

            DutyDef dutyDef = DefDatabase<DutyDef>.GetNamed(VehicleAssaultDutyDefName, errorOnFail: false)
                ?? DutyDefOf.Defend;
            float dutyRadius = Math.Max(radius, VehicleFrameworkAssaultUtility.MinimumAssaultRadius);
            pawn.mindState.duty = new PawnDuty(dutyDef, origin, dutyRadius);
            pawn.mindState.duty.radius = dutyRadius;
            pawn.mindState.duty.focusSecond = origin;
            pawn.mindState.duty.locomotion = LocomotionUrgency.Jog;
        }
    }
}

internal static class VehicleFrameworkAssaultUtility
{
    internal const float MinimumAssaultRadius = 16f;
    private const float TargetApproachRadius = 10f;

    private static Type? vehiclePawnType;
    private static Type? pathingHelperType;
    private static FieldInfo? vehiclePatherField;
    private static MethodInfo? tryFindNearestStandableCellMethod;
    private static PropertyInfo? canMoveFinalProperty;
    private static PropertyInfo? draftedProperty;
    private static PropertyInfo? compVehicleLauncherProperty;

    public static bool CanVehicleAssault(Pawn vehicle)
    {
        if (!IsVehiclePawn(vehicle)
            || vehicle is not { Spawned: true, Dead: false, Downed: false }
            || vehicle.Map is null
            || IsAerialVehicle(vehicle))
        {
            return false;
        }

        EnsureVehicleTypes(vehicle);
        return ReadBool(canMoveFinalProperty, vehicle)
            && GetVehiclePather(vehicle) is not null;
    }

    public static void SetVehicleDrafted(Pawn vehicle, bool drafted)
    {
        EnsureVehicleTypes(vehicle);
        try
        {
            draftedProperty?.SetValue(vehicle, drafted);
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim][ThirdPartyCompat][VehicleFramework] Failed to set vehicle drafted state: vehicle="
                + vehicle.ToStringSafe()
                + ", error="
                + ex.Message);
        }
    }

    public static bool TryFindAssaultDestination(Pawn vehicle, IntVec3 origin, float radius, out IntVec3 destination)
    {
        destination = IntVec3.Invalid;
        if (!CanVehicleAssault(vehicle))
        {
            return false;
        }

        Thing? target = FindNearestHostileTarget(vehicle, origin, Math.Max(radius, MinimumAssaultRadius));
        if (target is null || !TryFindVehicleStandCell(vehicle, target, out destination))
        {
            return false;
        }

        return destination.IsValid;
    }

    private static bool IsVehiclePawn(Pawn pawn)
    {
        Type? current = pawn.GetType();
        while (current is not null)
        {
            if (current.FullName == "Vehicles.VehiclePawn")
            {
                return true;
            }

            current = current.BaseType;
        }

        return false;
    }

    private static void EnsureVehicleTypes(Pawn vehicle)
    {
        Type type = vehicle.GetType();
        if (vehiclePawnType == type && vehiclePatherField is not null)
        {
            return;
        }

        vehiclePawnType = type;
        pathingHelperType = AccessTools.TypeByName("Vehicles.PathingHelper");
        vehiclePatherField = AccessTools.Field(vehiclePawnType, "vehiclePather");
        canMoveFinalProperty = AccessTools.Property(vehiclePawnType, "CanMoveFinal");
        draftedProperty = AccessTools.Property(vehiclePawnType, "Drafted");
        compVehicleLauncherProperty = AccessTools.Property(vehiclePawnType, "CompVehicleLauncher");
        tryFindNearestStandableCellMethod = AccessTools.Method(
            pathingHelperType,
            "TryFindNearestStandableCell",
            new[] { vehiclePawnType, typeof(IntVec3), typeof(IntVec3).MakeByRefType(), typeof(float) });
    }

    private static object? GetVehiclePather(Pawn vehicle)
    {
        EnsureVehicleTypes(vehicle);
        return vehiclePatherField?.GetValue(vehicle);
    }

    private static bool IsAerialVehicle(Pawn vehicle)
    {
        EnsureVehicleTypes(vehicle);
        try
        {
            return compVehicleLauncherProperty?.GetValue(vehicle) is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool ReadBool(PropertyInfo? property, object instance)
    {
        try
        {
            return property?.GetValue(instance) is true;
        }
        catch
        {
            return false;
        }
    }

    private static Thing? FindNearestHostileTarget(Pawn vehicle, IntVec3 origin, float radius)
    {
        if (vehicle.Map?.attackTargetsCache is null)
        {
            return null;
        }

        float radiusSquared = radius * radius;
        Thing? best = null;
        float bestDistance = float.MaxValue;
        List<IAttackTarget> targets = vehicle.Map.attackTargetsCache.GetPotentialTargetsFor(vehicle);
        for (int i = 0; i < targets.Count; i++)
        {
            IAttackTarget attackTarget = targets[i];
            Thing thing = attackTarget.Thing;
            if (thing is null
                || thing.Destroyed
                || !thing.Spawned
                || !thing.HostileTo(vehicle)
                || attackTarget.ThreatDisabled(vehicle)
                || !AttackTargetFinder.IsAutoTargetable(attackTarget))
            {
                continue;
            }

            float originDistance = thing.Position.DistanceToSquared(origin);
            if (originDistance > radiusSquared)
            {
                continue;
            }

            float vehicleDistance = thing.Position.DistanceToSquared(vehicle.Position);
            if (vehicleDistance < bestDistance)
            {
                bestDistance = vehicleDistance;
                best = thing;
            }
        }

        return best;
    }

    private static bool TryFindVehicleStandCell(Pawn vehicle, Thing target, out IntVec3 destination)
    {
        destination = IntVec3.Invalid;
        if (tryFindNearestStandableCellMethod is not null)
        {
            object?[] args =
            {
                vehicle,
                target.Position,
                destination,
                target is Pawn ? TargetApproachRadius : -1f
            };
            try
            {
                if (tryFindNearestStandableCellMethod.Invoke(null, args) is true && args[2] is IntVec3 found)
                {
                    destination = found;
                    return destination.IsValid;
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[ClashOfRim][ThirdPartyCompat][VehicleFramework] Failed to find assault vehicle stand cell: vehicle="
                    + vehicle.ToStringSafe()
                    + ", target="
                    + target.ToStringSafe()
                    + ", error="
                    + ex.Message);
            }
        }

        destination = target.Position;
        return destination.IsValid;
    }

}
