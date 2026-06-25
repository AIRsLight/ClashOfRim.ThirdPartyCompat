using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Reflection;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Raids;
using AIRsLight.ClashOfRim.RemoteMaps;
using AIRsLight.ClashOfRim.MainMenu;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using AIRsLight.ClashOfRim.WorldObjects;
using HarmonyLib;
using RimWorld.Planet;
using Verse;
using RimWorld;
using Verse.AI;
using Verse.AI.Group;
using UnityEngine;

namespace AIRsLight.ClashOfRim.ThirdPartyCompat.VehicleFramework;

internal static class VehicleFrameworkCompatibility
{
    private const string DefensePointEnterVehicleScribeKey = "clashOfRimVehicleFrameworkEnterVehicle";

    private static Type? vehicleCaravanType;
    private static Type? vehiclePawnType;
    private static Type? aerialVehicleInFlightType;
    private static Type? compVehicleLauncherType;
    private static Type? arrivalOptionType;
    private static Type? arrivalActionLandToCaravanType;
    private static Type? vehicleWorldObjectsHolderType;
    private static Type? enterMapUtilityType;
    private static Type? spawnParamsType;
    private static MethodInfo? enterMapMethod;
    private static MethodInfo? vehicleWorldObjectsHolderVehicleCaravanObjectMethod;
    private static MethodInfo? vehicleTryAddPawnMethod;
    private static MethodInfo? vehicleDestroyVehicleAndPawnsMethod;
    private static MethodInfo? vehicleCompVehicleLauncherGetter;
    private static FieldInfo? vehicleHandlersField;
    private static FieldInfo? handlerThingOwnerField;
    private static readonly ConditionalWeakTable<Building_ClashDefensePoint, VehicleDefensePointState> DefensePointStates = new();
    private static readonly ConditionalWeakTable<object, DisabledArrivalOptionInfo> DisabledArrivalOptions = new();

    public static void Apply(Harmony harmony)
    {
        ClashOfRimCompatibilityApi.RegisterCompatibilityPackage(
            ThirdPartyCompatibilityPackageKeys.VehicleFrameworkPackageId,
            IsVehicleFrameworkLoaded,
            new[]
            {
                ThirdPartyCompatibilityPackageKeys.VehicleFrameworkRaidVehicleEntry,
                ThirdPartyCompatibilityPackageKeys.VehicleFrameworkDefenderPassengerProtection,
                ThirdPartyCompatibilityPackageKeys.VehicleFrameworkHitPointBaseline,
                ThirdPartyCompatibilityPackageKeys.VehicleFrameworkRaidSettlementDamage
            });

        if (!IsVehicleFrameworkLoaded())
        {
            return;
        }

        PatchSmashToolsRemoteMapCache(harmony);

        vehicleCaravanType = AccessTools.TypeByName("Vehicles.World.VehicleCaravan");
        vehiclePawnType = vehicleCaravanType?.Assembly.GetType("Vehicles.VehiclePawn") ?? AccessTools.TypeByName("Vehicles.VehiclePawn");
        aerialVehicleInFlightType = vehicleCaravanType?.Assembly.GetType("Vehicles.World.AerialVehicleInFlight") ?? AccessTools.TypeByName("Vehicles.World.AerialVehicleInFlight");
        compVehicleLauncherType = vehicleCaravanType?.Assembly.GetType("Vehicles.CompVehicleLauncher") ?? AccessTools.TypeByName("Vehicles.CompVehicleLauncher");
        arrivalOptionType = vehicleCaravanType?.Assembly.GetType("Vehicles.World.ArrivalOption") ?? AccessTools.TypeByName("Vehicles.World.ArrivalOption");
        arrivalActionLandToCaravanType = vehicleCaravanType?.Assembly.GetType("Vehicles.World.ArrivalAction_LandToCaravan") ?? AccessTools.TypeByName("Vehicles.World.ArrivalAction_LandToCaravan");
        vehicleWorldObjectsHolderType = vehicleCaravanType?.Assembly.GetType("Vehicles.World.VehicleWorldObjectsHolder") ?? AccessTools.TypeByName("Vehicles.World.VehicleWorldObjectsHolder");
        enterMapUtilityType = AccessTools.TypeByName("Vehicles.World.EnterMapUtilityVehicles");
        spawnParamsType = enterMapUtilityType?.GetNestedType("SpawnParams", BindingFlags.Public | BindingFlags.NonPublic);
        enterMapMethod = enterMapUtilityType
            ?.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(method =>
                method.Name == "EnterMap"
                && method.GetParameters().Length == 3);
        vehicleWorldObjectsHolderVehicleCaravanObjectMethod = AccessTools.Method(vehicleWorldObjectsHolderType, "VehicleCaravanObject");
        vehicleTryAddPawnMethod = AccessTools.Method(vehiclePawnType, "TryAddPawn", new[] { typeof(Pawn) });
        vehicleDestroyVehicleAndPawnsMethod = AccessTools.Method(vehiclePawnType, "DestroyVehicleAndPawns", new[] { typeof(DestroyMode) });
        vehicleCompVehicleLauncherGetter = AccessTools.PropertyGetter(vehiclePawnType, "CompVehicleLauncher");
        vehicleHandlersField = AccessTools.Field(vehiclePawnType, "handlers");
        handlerThingOwnerField = AccessTools.Field(
            vehicleCaravanType?.Assembly.GetType("Vehicles.VehicleRoleHandler"),
            "thingOwner");

        PatchDefensePointVehicleBoarding(harmony);
        RegisterAerialRemoteMapLanding();
        PatchAerialRemoteColonyLanding(harmony);
        PatchAerialRemoteColonyLandingSubmitGuard(harmony, includeAerialVehicleInFlight: false);
        ClashOfRimMainMenuPatches.EnqueueMainThreadAction(() =>
            PatchAerialRemoteColonyLandingSubmitGuard(harmony, includeAerialVehicleInFlight: true));

        if (vehicleCaravanType is null || enterMapMethod is null || spawnParamsType is null)
        {
            Log.Warning("[ClashOfRim][ThirdPartyCompat][VehicleFramework] Vehicle caravan entry API was not found.");
            return;
        }

        ClashOfRimCompatibilityApi.RegisterRaidCaravanMapEntryHandler(TryEnterRaidCaravanMap);
        ClashOfRimCompatibilityApi.RegisterRemoteDefenderMapPreparedHandler(ApplyDefenderPassengerFactions);
        ClashOfRimCompatibilityApi.RegisterRemoteDefenderMapPreparedHandler(ApplyDefensePointVehicleBoarding);
        ClashOfRimCompatibilityApi.RegisterRemoteSessionPawnCleanupHandler(TryCleanupRemoteSessionVehiclePawn);
        ClashOfRimCompatibilityApi.RegisterAdminBaselineExtensionProvider(
            ThirdPartyCompatibilityPackageKeys.VehicleFrameworkPackageId,
            "hitPointBaseline",
            ReadVehicleHitPointBaselineExtension);
    }

    private static void PatchDefensePointVehicleBoarding(Harmony harmony)
    {
        MethodInfo? getGizmos = AccessTools.Method(typeof(Building_ClashDefensePoint), nameof(Building_ClashDefensePoint.GetGizmos));
        MethodInfo? exposeData = AccessTools.Method(typeof(Building_ClashDefensePoint), nameof(Building_ClashDefensePoint.ExposeData));
        MethodInfo? getInspectString = AccessTools.Method(typeof(Building_ClashDefensePoint), nameof(Building_ClashDefensePoint.GetInspectString));
        MethodInfo? gizmosPostfix = AccessTools.Method(typeof(VehicleFrameworkCompatibility), nameof(DefensePointGetGizmosPostfix));
        MethodInfo? exposePostfix = AccessTools.Method(typeof(VehicleFrameworkCompatibility), nameof(DefensePointExposeDataPostfix));
        MethodInfo? inspectPostfix = AccessTools.Method(typeof(VehicleFrameworkCompatibility), nameof(DefensePointGetInspectStringPostfix));

        if (getGizmos is not null && gizmosPostfix is not null)
        {
            harmony.Patch(getGizmos, postfix: new HarmonyMethod(gizmosPostfix));
        }

        if (exposeData is not null && exposePostfix is not null)
        {
            harmony.Patch(exposeData, postfix: new HarmonyMethod(exposePostfix));
        }

        if (getInspectString is not null && inspectPostfix is not null)
        {
            harmony.Patch(getInspectString, postfix: new HarmonyMethod(inspectPostfix));
        }
    }

    private static void DefensePointGetGizmosPostfix(Building_ClashDefensePoint __instance, ref IEnumerable<Gizmo> __result)
    {
        IEnumerable<Gizmo> original = __result;
        __result = AppendDefensePointVehicleGizmos(__instance, original);
    }

    private static IEnumerable<Gizmo> AppendDefensePointVehicleGizmos(
        Building_ClashDefensePoint point,
        IEnumerable<Gizmo> original)
    {
        foreach (Gizmo gizmo in original)
        {
            yield return gizmo;
        }

        if (point.Faction != Faction.OfPlayer)
        {
            yield break;
        }

        yield return new Command_Toggle
        {
            defaultLabel = "ClashOfRim.VehicleFramework.DefensePoint.EnterVehicle".Translate(),
            defaultDesc = "ClashOfRim.VehicleFramework.DefensePoint.EnterVehicleDesc".Translate(),
            icon = ContentFinder<Texture2D>.Get("UI/Gizmos/StartLoadVehicle", reportFailure: false) ?? BaseContent.BadTex,
            isActive = () => GetDefensePointState(point).EnterVehicle,
            toggleAction = () =>
            {
                VehicleDefensePointState state = GetDefensePointState(point);
                state.EnterVehicle = !state.EnterVehicle;
            }
        };
    }

    private static void DefensePointExposeDataPostfix(Building_ClashDefensePoint __instance)
    {
        VehicleDefensePointState state = GetDefensePointState(__instance);
        bool enterVehicle = state.EnterVehicle;
        Scribe_Values.Look(ref enterVehicle, DefensePointEnterVehicleScribeKey, defaultValue: false);
        state.EnterVehicle = enterVehicle;
    }

    private static void DefensePointGetInspectStringPostfix(Building_ClashDefensePoint __instance, ref string __result)
    {
        if (!GetDefensePointState(__instance).EnterVehicle)
        {
            return;
        }

        string line = "ClashOfRim.VehicleFramework.DefensePoint.EnterVehicleInspect".Translate();
        __result = __result.NullOrEmpty() ? line : __result + "\n" + line;
    }

    private static VehicleDefensePointState GetDefensePointState(Building_ClashDefensePoint point)
    {
        return DefensePointStates.GetValue(point, _ => new VehicleDefensePointState());
    }

    private static ModAdminBaselineExtensionDto ReadVehicleHitPointBaselineExtension()
    {
        List<ModAdminBaselineExtensionRecordDto> records = DefDatabase<ThingDef>.AllDefsListForReading
            .Where(def => def is not null
                && !string.IsNullOrWhiteSpace(def.defName)
                && IsVehicleDef(def))
            .Select(def =>
            {
                int estimatedMaxHitPoints = ReadEstimatedMaxHitPoints(def);
                return new ModAdminBaselineExtensionRecordDto
                {
                    Key = def.defName,
                    Values = new Dictionary<string, string>
                    {
                        ["defName"] = def.defName,
                        ["label"] = def.label ?? string.Empty,
                        ["modPackageId"] = def.modContentPack?.PackageId ?? string.Empty,
                        ["modName"] = def.modContentPack?.Name ?? string.Empty,
                        ["useHitPoints"] = estimatedMaxHitPoints > 0 ? "true" : "false",
                        ["hitPointSource"] = def.useHitPoints ? "thingDef" : "vehicleComponents",
                        ["estimatedMaxHitPoints"] = estimatedMaxHitPoints.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    }
                };
            })
            .OrderBy(entry => entry.Values.TryGetValue("modPackageId", out string modPackageId) ? modPackageId : string.Empty, StringComparer.Ordinal)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .ToList();

        return new ModAdminBaselineExtensionDto
        {
            ProviderId = ThirdPartyCompatibilityPackageKeys.VehicleFrameworkPackageId,
            Kind = "hitPointBaseline",
            Records = records
        };
    }

    private static bool IsVehicleDef(ThingDef def)
    {
        Type? current = def.GetType();
        while (current is not null)
        {
            if (string.Equals(current.FullName, "Vehicles.VehicleDef", StringComparison.Ordinal))
            {
                return true;
            }

            current = current.BaseType;
        }

        return false;
    }

    private static int ReadEstimatedMaxHitPoints(ThingDef def)
    {
        if (TryReadVehicleComponentMaxHitPoints(def, out int componentMaxHitPoints))
        {
            return componentMaxHitPoints;
        }

        if (!def.useHitPoints)
        {
            return 0;
        }

        try
        {
            return Math.Max(1, Mathf.RoundToInt(def.GetStatValueAbstract(StatDefOf.MaxHitPoints)));
        }
        catch
        {
            return Math.Max(1, def.BaseMaxHitPoints);
        }
    }

    private static bool TryReadVehicleComponentMaxHitPoints(ThingDef def, out int hitPoints)
    {
        hitPoints = 0;
        object? components = ReadMember(def, "components");
        if (components is not IEnumerable enumerable)
        {
            return false;
        }

        foreach (object? component in enumerable)
        {
            int componentHealth = ReadIntMember(component, "health");
            if (componentHealth > 0)
            {
                hitPoints += componentHealth;
            }
        }

        return hitPoints > 0;
    }

    private static int ReadIntMember(object? instance, string memberName)
    {
        object? value = ReadMember(instance, memberName);
        if (value is int integer)
        {
            return integer;
        }

        if (value is float single && single > 0)
        {
            return Mathf.RoundToInt(single);
        }

        if (value is double number && number > 0)
        {
            return (int)Math.Round(number, MidpointRounding.AwayFromZero);
        }

        return 0;
    }

    private static object? ReadMember(object? instance, string memberName)
    {
        if (instance is null || string.IsNullOrWhiteSpace(memberName))
        {
            return null;
        }

        Type type = instance.GetType();
        return AccessTools.Field(type, memberName)?.GetValue(instance)
            ?? AccessTools.Property(type, memberName)?.GetValue(instance);
    }

    private static void ApplyDefenderPassengerFactions(Map map, Faction defenderFaction)
    {
        if (map?.listerThings?.AllThings is null || defenderFaction is null)
        {
            return;
        }

        foreach (Thing thing in map.listerThings.AllThings.ToList())
        {
            if (thing is null
                || vehicleHandlersField is null
                || !IsVehiclePawn(thing.GetType()))
            {
                continue;
            }

            foreach (Pawn passenger in VehiclePassengers(thing))
            {
                if (passenger is null || passenger.Destroyed || passenger.Dead)
                {
                    continue;
                }

                ClashOfRimCompatibilityApi.ApplyRaidDefenderProxyPawnProtection(passenger, defenderFaction);
            }
        }
    }

    private static void ApplyDefensePointVehicleBoarding(Map map, Faction defenderFaction)
    {
        if (map?.listerThings?.AllThings is null
            || defenderFaction is null
            || vehiclePawnType is null)
        {
            return;
        }

        int enabledPoints = 0;
        int boardedPawns = 0;
        int staticVehicles = 0;
        int assaultVehicles = 0;
        var orderedVehicles = new HashSet<Pawn>();

        foreach (Building_ClashDefensePoint point in DefensePointUtility.AllDefensePoints(map).ToList())
        {
            if (!GetDefensePointState(point).EnterVehicle)
            {
                continue;
            }

            enabledPoints++;
            Thing? vehicle = FindNearestDefenderVehicle(point, defenderFaction);
            if (vehicle is not Pawn vehiclePawn)
            {
                continue;
            }

            Pawn? assignedPawn = point.AssignedPawn;
            if (!IsUsableVehicleCrew(assignedPawn, defenderFaction))
            {
                continue;
            }

            if (TryBoardVehicle(vehicle, assignedPawn!))
            {
                boardedPawns++;
            }

            if (orderedVehicles.Add(vehiclePawn))
            {
                if (point.AiMode == DefensePointAiMode.Assault
                    && TryStartVehicleAssault(map, defenderFaction, vehiclePawn, point))
                {
                    assaultVehicles++;
                }
                else
                {
                    MakeVehicleStaticDefense(vehiclePawn);
                    staticVehicles++;
                }
            }
        }

        if (enabledPoints > 0 && Prefs.DevMode)
        {
            Log.Message(
                "[ClashOfRim][ThirdPartyCompat][VehicleFramework] Applied defense point vehicle boarding: points="
                + enabledPoints
                + ", boarded="
                + boardedPawns
                + ", staticVehicles="
                + staticVehicles
                + ", assaultVehicles="
                + assaultVehicles);
        }
    }

    private static Thing? FindNearestDefenderVehicle(Building_ClashDefensePoint point, Faction defenderFaction)
    {
        return point.Map.listerThings.AllThings
            .Where(thing => thing is Pawn pawn
                && IsVehiclePawn(thing.GetType())
                && pawn.Faction == defenderFaction
                && pawn.Spawned
                && !pawn.Destroyed
                && !pawn.Dead
                && !IsAerialVehicle(thing)
                && thing.Position.InHorDistOf(point.Position, point.ActionRadius))
            .OrderBy(thing => thing.Position.DistanceToSquared(point.Position))
            .FirstOrDefault();
    }

    private static bool IsAerialVehicle(Thing vehicle)
    {
        if (vehicleCompVehicleLauncherGetter is null)
        {
            return false;
        }

        try
        {
            return vehicleCompVehicleLauncherGetter.Invoke(vehicle, Array.Empty<object>()) is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsUsableVehicleCrew(Pawn? pawn, Faction defenderFaction)
    {
        return pawn is { Spawned: true, Dead: false, Downed: false }
            && pawn.Faction == defenderFaction
            && pawn.Map is not null
            && pawn.mindState is not null;
    }

    private static bool TryBoardVehicle(Thing vehicle, Pawn pawn)
    {
        if (vehicleTryAddPawnMethod is null)
        {
            return false;
        }

        try
        {
            pawn.GetLord()?.RemovePawn(pawn);
            if (pawn.CurJob is not null)
            {
                pawn.jobs?.EndCurrentJob(JobCondition.InterruptForced, startNewJob: true, canReturnToPool: true);
            }

            RestUtility.WakeUp(pawn, true);
            object? result = vehicleTryAddPawnMethod.Invoke(vehicle, new object[] { pawn });
            return result is true;
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim][ThirdPartyCompat][VehicleFramework] Failed to board defense point pawn into vehicle: pawn="
                + pawn.LabelShort
                + ", vehicle="
                + vehicle.LabelShort
                + ", error="
                + ex);
            return false;
        }
    }

    private static void MakeVehicleStaticDefense(Pawn vehiclePawn)
    {
        try
        {
            vehiclePawn.GetLord()?.RemovePawn(vehiclePawn);
            if (vehiclePawn.CurJob is not null)
            {
                vehiclePawn.jobs?.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false, canReturnToPool: true);
            }

            vehiclePawn.jobs?.ClearQueuedJobs(canReturnToPool: true);
            vehiclePawn.pather?.StopDead();
            vehiclePawn.mindState.enemyTarget = null;
            vehiclePawn.mindState.meleeThreat = null;
            vehiclePawn.mindState.duty = null;

            object? ignition = ReadMember(vehiclePawn, "ignition");
            PropertyInfo? draftedProperty = ignition?.GetType().GetProperty("Drafted");
            if (draftedProperty is not null && draftedProperty.CanWrite)
            {
                draftedProperty.SetValue(ignition, true);
            }
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim][ThirdPartyCompat][VehicleFramework] Failed to set defense point vehicle static: vehicle="
                + vehiclePawn.LabelShort
                + ", error="
                + ex);
        }
    }

    private static bool TryStartVehicleAssault(
        Map map,
        Faction defenderFaction,
        Pawn vehiclePawn,
        Building_ClashDefensePoint point)
    {
        try
        {
            if (!VehicleFrameworkAssaultUtility.CanVehicleAssault(vehiclePawn))
            {
                return false;
            }

            vehiclePawn.GetLord()?.RemovePawn(vehiclePawn);
            if (vehiclePawn.CurJob is not null)
            {
                vehiclePawn.jobs?.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false, canReturnToPool: true);
            }

            vehiclePawn.jobs?.ClearQueuedJobs(canReturnToPool: true);
            vehiclePawn.pather?.StopDead();
            vehiclePawn.mindState.enemyTarget = null;
            vehiclePawn.mindState.meleeThreat = null;
            vehiclePawn.mindState.duty = null;
            VehicleFrameworkAssaultUtility.SetVehicleDrafted(vehiclePawn, drafted: true);

            Lord lord = LordMaker.MakeNewLord(
                defenderFaction,
                new LordJob_ClashVehicleAssault(point.Position, point.ActionRadius),
                map,
                new List<Pawn> { vehiclePawn });
            lord.CurLordToil?.UpdateAllDuties();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim][ThirdPartyCompat][VehicleFramework] Failed to start defense point vehicle assault: vehicle="
                + vehiclePawn.LabelShort
                + ", error="
                + ex);
            return false;
        }
    }

    private static bool TryCleanupRemoteSessionVehiclePawn(Pawn pawn, string reason)
    {
        if (pawn is null || vehiclePawnType is null || !IsVehiclePawn(pawn.GetType()))
        {
            return false;
        }

        try
        {
            pawn.GetLord()?.RemovePawn(pawn);
            if (pawn.CurJob is not null)
            {
                pawn.jobs?.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false, canReturnToPool: true);
            }

            pawn.jobs?.ClearQueuedJobs(canReturnToPool: true);
            pawn.pather?.StopDead();
            if (!pawn.Destroyed && vehicleDestroyVehicleAndPawnsMethod is not null)
            {
                vehicleDestroyVehicleAndPawnsMethod.Invoke(pawn, new object[] { DestroyMode.Vanish });
            }
            else if (!pawn.Destroyed)
            {
                pawn.Destroy(DestroyMode.Vanish);
            }

            if (Find.WorldPawns?.Contains(pawn) == true)
            {
                Find.WorldPawns.RemoveAndDiscardPawnViaGC(pawn);
            }

            if (Prefs.DevMode)
            {
                Log.Message("[ClashOfRim][ThirdPartyCompat][VehicleFramework] Cleaned remote session vehicle pawn: vehicle="
                    + pawn.ToStringSafe()
                    + ", reason="
                    + reason);
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim][ThirdPartyCompat][VehicleFramework] Failed to cleanup remote session vehicle pawn: vehicle="
                + pawn.ToStringSafe()
                + ", error="
                + ex);
            return false;
        }
    }

    private static void PatchSmashToolsRemoteMapCache(Harmony harmony)
    {
        Type? componentCacheType = AccessTools.TypeByName("SmashTools.ComponentCache");
        Type? detachedCacheType = AccessTools.TypeByName("SmashTools.DetachedMapComponentCache`1");
        if (componentCacheType is null)
        {
            Log.Warning("[ClashOfRim][ThirdPartyCompat][VehicleFramework] SmashTools ComponentCache API was not found.");
            return;
        }

        if (detachedCacheType is null)
        {
            Log.Warning("[ClashOfRim][ThirdPartyCompat][VehicleFramework] SmashTools DetachedMapComponentCache API was not found.");
            return;
        }

        FieldInfo? detachedTypesField = AccessTools.Field(componentCacheType, "DetachedComponentTypes");
        if (detachedTypesField?.GetValue(null) is not IEnumerable detachedTypes)
        {
            Log.Warning("[ClashOfRim][ThirdPartyCompat][VehicleFramework] SmashTools detached component type list was not found.");
            return;
        }

        MethodInfo? beforeAdd = AccessTools.Method(typeof(VehicleFrameworkCompatibility), nameof(BeforeSmashToolsDetachedAddComponent));
        if (beforeAdd is null)
        {
            Log.Warning("[ClashOfRim][ThirdPartyCompat][VehicleFramework] SmashTools detached component cache patch method was not found.");
            return;
        }

        try
        {
            int patched = 0;
            foreach (object? detachedTypeObject in detachedTypes)
            {
                if (detachedTypeObject is not Type detachedType)
                {
                    continue;
                }

                Type closedCacheType = detachedCacheType.MakeGenericType(detachedType);
                MethodInfo? addComponent = AccessTools.Method(closedCacheType, "AddComponent", new[] { typeof(Map) });
                if (addComponent is null)
                {
                    continue;
                }

                harmony.Patch(addComponent, prefix: new HarmonyMethod(beforeAdd));
                patched++;
            }

            if (patched == 0)
            {
                Log.Warning("[ClashOfRim][ThirdPartyCompat][VehicleFramework] No SmashTools detached map component cache methods were patched.");
            }
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim][ThirdPartyCompat][VehicleFramework] Failed to patch SmashTools remote map cache handling: "
                + ex);
        }
    }

    private static bool BeforeSmashToolsDetachedAddComponent(Map map, MethodBase __originalMethod)
    {
        if (map is null || !RemoteMapProjectionLoadScope.Active)
        {
            return true;
        }

        try
        {
            FieldInfo? mapCompsField = AccessTools.Field(__originalMethod.DeclaringType, "MapComps");
            if (mapCompsField?.GetValue(null) is IDictionary mapComps && mapComps.Contains(map.uniqueID))
            {
                return false;
            }
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim][ThirdPartyCompat][VehicleFramework] Failed to inspect SmashTools detached map component cache before add: map=Map_"
                + map.uniqueID
                + ", error="
                + ex);
        }

        return true;
    }

    private static bool IsVehiclePawn(Type type)
    {
        Type? current = type;
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

    private static IEnumerable<Pawn> VehiclePassengers(Thing vehicle)
    {
        if (vehicleHandlersField?.GetValue(vehicle) is not IEnumerable handlers)
        {
            yield break;
        }

        foreach (object? handler in handlers)
        {
            if (handler is null || handlerThingOwnerField?.GetValue(handler) is not IEnumerable thingOwner)
            {
                continue;
            }

            foreach (object? held in thingOwner)
            {
                if (held is Pawn pawn)
                {
                    yield return pawn;
                }
            }
        }
    }

    private static bool IsVehicleFrameworkLoaded()
    {
        return LoadedModManager.RunningModsListForReading.Any(mod =>
            string.Equals(mod.PackageIdPlayerFacing, ThirdPartyCompatibilityPackageKeys.VehicleFrameworkPackageId, StringComparison.OrdinalIgnoreCase))
            || AccessTools.TypeByName("Vehicles.World.VehicleCaravan") is not null;
    }

    private static void RegisterAerialRemoteMapLanding()
    {
        Type? compatibilityType = AccessTools.TypeByName("Vehicles.Compatibility.AerialVehicleCompatibility");
        Type? settingsType = AccessTools.TypeByName("Vehicles.Compatibility.AerialVehicleCompatibility+Settings");
        MethodInfo? registerMethod = AccessTools.Method(compatibilityType, "RegisterWorldObjectType");
        if (compatibilityType is null || settingsType is null || registerMethod is null)
        {
            Log.Warning("[ClashOfRim][ThirdPartyCompat][VehicleFramework] Aerial vehicle compatibility API was not found.");
            return;
        }

        try
        {
            object? settings = Activator.CreateInstance(settingsType, true, false);
            if (settings is null)
            {
                Log.Warning("[ClashOfRim][ThirdPartyCompat][VehicleFramework] Failed to create aerial landing settings.");
                return;
            }

            FieldInfo? canLandValidatorField = AccessTools.Field(settingsType, "canLandInValidator");
            MethodInfo? validatorMethod = AccessTools.Method(
                typeof(VehicleFrameworkCompatibility),
                nameof(CanLandInRemoteSession));
            if (canLandValidatorField is not null && validatorMethod is not null)
            {
                Delegate validator = Delegate.CreateDelegate(canLandValidatorField.FieldType, validatorMethod);
                canLandValidatorField.SetValue(settings, validator);
            }

            registerMethod.Invoke(null, new[] { typeof(RemoteSessionMapParent), settings });
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim][ThirdPartyCompat][VehicleFramework] Failed to register aerial landing compatibility: "
                + ex);
        }
    }

    private static bool CanLandInRemoteSession(MapParent mapParent)
    {
        return mapParent is RemoteSessionMapParent remoteParent && remoteParent.Policy.CanInjectUnits;
    }

    private static void PatchAerialRemoteColonyLanding(Harmony harmony)
    {
        if (compVehicleLauncherType is null
            || arrivalOptionType is null
            || arrivalActionLandToCaravanType is null)
        {
            Log.Warning("[ClashOfRim][ThirdPartyCompat][VehicleFramework] Aerial remote colony landing API was not found.");
            return;
        }

        MethodInfo? optionsAt = AccessTools.Method(compVehicleLauncherType, "OptionsAt", new[] { typeof(GlobalTargetInfo) });
        MethodInfo? optionsPostfixDefinition = AccessTools.Method(
            typeof(VehicleFrameworkCompatibility),
            nameof(CompVehicleLauncherOptionsAtPostfix));
        MethodInfo? arrived = AccessTools.Method(arrivalActionLandToCaravanType, "Arrived", new[] { typeof(GlobalTargetInfo) });
        MethodInfo? arrivedPostfix = AccessTools.Method(
            typeof(VehicleFrameworkCompatibility),
            nameof(LandToCaravanArrivedPostfix));

        if (optionsAt is null || optionsPostfixDefinition is null || arrived is null || arrivedPostfix is null)
        {
            Log.Warning("[ClashOfRim][ThirdPartyCompat][VehicleFramework] Aerial remote colony landing patch methods were not found.");
            return;
        }

        try
        {
            MethodInfo optionsPostfix = optionsPostfixDefinition.MakeGenericMethod(arrivalOptionType);
            harmony.Patch(optionsAt, postfix: new HarmonyMethod(optionsPostfix));
            harmony.Patch(arrived, postfix: new HarmonyMethod(arrivedPostfix));
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim][ThirdPartyCompat][VehicleFramework] Failed to patch aerial remote colony landing: "
                + ex);
        }
    }

    private static void PatchAerialRemoteColonyLandingSubmitGuard(Harmony harmony, bool includeAerialVehicleInFlight)
    {
        if (arrivalOptionType is null)
        {
            return;
        }

        MethodInfo? prefix = AccessTools.Method(
            typeof(VehicleFrameworkCompatibility),
            nameof(CompVehicleLauncherOnTargetingFinishedPrefix));
        if (prefix is null)
        {
            Log.Warning("[ClashOfRim][ThirdPartyCompat][VehicleFramework] Aerial remote colony landing submit guard was not found.");
            return;
        }

        int patched = 0;
        IEnumerable<Type?> targetTypes = includeAerialVehicleInFlight
            ? new[] { aerialVehicleInFlightType }
            : new[] { compVehicleLauncherType, vehicleCaravanType };
        foreach (Type? targetType in targetTypes)
        {
            MethodInfo? onTargetingFinished = targetType
                ?.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name.IndexOf("OnTargetingFinished", StringComparison.Ordinal) >= 0
                    && method.GetParameters().Length == 2
                    && method.GetParameters()[1].ParameterType == arrivalOptionType);
            if (onTargetingFinished is null)
            {
                continue;
            }

            try
            {
                harmony.Patch(onTargetingFinished, prefix: new HarmonyMethod(prefix));
                patched++;
            }
            catch (Exception ex)
            {
                Log.Warning("[ClashOfRim][ThirdPartyCompat][VehicleFramework] Failed to patch aerial remote colony landing submit guard for "
                    + targetType?.FullName
                    + ": "
                    + ex);
            }
        }

        if (patched == 0)
        {
            Log.Warning("[ClashOfRim][ThirdPartyCompat][VehicleFramework] Aerial remote colony landing submit guard target methods were not found.");
        }
    }

    private static bool CompVehicleLauncherOnTargetingFinishedPrefix(object __1)
    {
        if (__1 is null || !DisabledArrivalOptions.TryGetValue(__1, out DisabledArrivalOptionInfo disabled))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(disabled.Message))
        {
            Messages.Message(disabled.Message, MessageTypeDefOf.RejectInput, historical: false);
        }

        return false;
    }

    private static void CompVehicleLauncherOptionsAtPostfix<TArrivalOption>(
        object __instance,
        GlobalTargetInfo target,
        ref IEnumerable<TArrivalOption> __result)
        where TArrivalOption : class
    {
        if (!TryCreateRemoteColonyLandingOption(__instance, target, out object? option, out bool replaceExisting)
            || option is not TArrivalOption typedOption)
        {
            return;
        }

        __result = replaceExisting ? SingleOption(typedOption) : AppendOption(__result, typedOption);
    }

    private static IEnumerable<TArrivalOption> SingleOption<TArrivalOption>(TArrivalOption option)
    {
        yield return option;
    }

    private static IEnumerable<TArrivalOption> AppendOption<TArrivalOption>(
        IEnumerable<TArrivalOption> source,
        TArrivalOption option)
    {
        if (source is not null)
        {
            foreach (TArrivalOption existing in source)
            {
                yield return existing;
            }
        }

        yield return option;
    }

    private static bool TryCreateRemoteColonyLandingOption(
        object launcher,
        GlobalTargetInfo target,
        out object? option,
        out bool replaceExisting)
    {
        option = null;
        replaceExisting = false;
        if (arrivalOptionType is null
            || arrivalActionLandToCaravanType is null
            || !TryFindRemoteColony(target, out RemoteColonyMapParent? remoteColony)
            || remoteColony is null
            || !IsVehicleRemoteLandingTarget(remoteColony))
        {
            return false;
        }

        bool hostile = IsHostileRemoteColony(remoteColony);
        bool orbital = IsOrbitalTile(remoteColony.Tile);
        replaceExisting = orbital;
        bool disabled = false;
        TaggedString label;

        if (orbital && !hostile)
        {
            label = "ClashOfRim.VehicleFramework.RemoteLanding.OrbitalNonHostileDisabled".Translate(remoteColony.Label.Named("TARGET"));
            disabled = true;
        }
        else if (hostile && !remoteColony.CanRaid)
        {
            if (!orbital)
            {
                return false;
            }

            label = "ClashOfRim.VehicleFramework.RemoteLanding.RaidUnavailable".Translate();
            disabled = true;
        }
        else
        {
            label = (hostile
                ? "ClashOfRim.VehicleFramework.RemoteLanding.StartRaid"
                : "ClashOfRim.VehicleFramework.RemoteLanding.CreateCaravan").Translate();
        }

        object? vehicle = ReadMember(launcher, "Vehicle");
        if (vehicle is null)
        {
            return false;
        }

        object? arrivalAction;
        try
        {
            arrivalAction = Activator.CreateInstance(arrivalActionLandToCaravanType, vehicle);
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim][ThirdPartyCompat][VehicleFramework] Failed to create remote landing action: "
                + ex);
            return false;
        }

        if (arrivalAction is null)
        {
            return false;
        }

        try
        {
            option = Activator.CreateInstance(arrivalOptionType, label, arrivalAction);
            if (disabled && option is not null)
            {
                MarkArrivalOptionDisabled(option, label.ToString());
            }

            return option is not null;
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim][ThirdPartyCompat][VehicleFramework] Failed to create remote landing option: "
                + ex);
            return false;
        }
    }

    private static void MarkArrivalOptionDisabled(object option, string message)
    {
        DisabledArrivalOptions.Remove(option);
        DisabledArrivalOptions.Add(option, new DisabledArrivalOptionInfo(message));

        try
        {
            PropertyInfo? acceptanceReportProperty = option.GetType().GetProperty(
                "AcceptanceReport",
                BindingFlags.Instance | BindingFlags.Public);
            Type? reportType = acceptanceReportProperty?.PropertyType;
            MethodInfo? failReasonFactory = reportType
                ?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(method =>
                    method.Name == "WithFailReason"
                    && method.GetParameters().Length == 1);
            if (acceptanceReportProperty is null || failReasonFactory is null)
            {
                return;
            }

            ParameterInfo parameter = failReasonFactory.GetParameters()[0];
            object argument = parameter.ParameterType == typeof(TaggedString)
                ? (TaggedString)message
                : message;
            object? report = failReasonFactory.Invoke(null, new[] { argument });
            if (report is not null)
            {
                acceptanceReportProperty.SetValue(option, report);
            }
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim][ThirdPartyCompat][VehicleFramework] Failed to mark remote landing option disabled: "
                + ex);
        }
    }

    private static void LandToCaravanArrivedPostfix(object __instance, GlobalTargetInfo __0)
    {
        GlobalTargetInfo destinationTile = __0;
        if (!TryFindRemoteColony(destinationTile, out RemoteColonyMapParent? remoteColony)
            || remoteColony is null
            || !IsVehicleRemoteLandingTarget(remoteColony)
            || !IsHostileRemoteColony(remoteColony))
        {
            return;
        }

        if (!remoteColony.CanRaid)
        {
            string unavailable = "ClashOfRim.VehicleFramework.RemoteLanding.RaidUnavailable".Translate();
            Messages.Message(unavailable, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        object? vehicle = ReadMember(__instance, "vehicle");
        Caravan? caravan = FindVehicleCaravan(vehicle);
        if (caravan is null)
        {
            string message = "ClashOfRim.VehicleFramework.RemoteLanding.RaidStartFailedNoCaravan".Translate();
            Messages.Message(message, MessageTypeDefOf.RejectInput, historical: false);
            Log.Warning("[ClashOfRim][ThirdPartyCompat][VehicleFramework] Remote vehicle landing could not find the landed vehicle caravan.");
            return;
        }

        try
        {
            LoadedModManager.GetMod<ClashOfRimMod>().StartRaidFromVehicleLanding(caravan, BuildMarker(remoteColony));
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim][ThirdPartyCompat][VehicleFramework] Failed to start raid after remote vehicle landing: "
                + ex);
        }
    }

    private static bool TryFindRemoteColony(GlobalTargetInfo target, out RemoteColonyMapParent? remoteColony)
    {
        remoteColony = target.WorldObject as RemoteColonyMapParent;
        if (remoteColony is not null)
        {
            return true;
        }

        if (target.WorldObject is not null)
        {
            return false;
        }

        if (!target.Tile.Valid || Find.WorldObjects?.AllWorldObjects is null)
        {
            return false;
        }

        remoteColony = Find.WorldObjects.AllWorldObjects
            .OfType<RemoteColonyMapParent>()
            .FirstOrDefault(candidate => candidate.Tile == target.Tile);
        return remoteColony is not null;
    }

    private static bool IsVehicleRemoteLandingTarget(RemoteColonyMapParent remoteColony)
    {
        return string.Equals(remoteColony.RuntimeKind, "TradeableColony", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(remoteColony.OwnerUserId)
            && !string.IsNullOrWhiteSpace(remoteColony.OwnerColonyId)
            && !string.IsNullOrWhiteSpace(remoteColony.SourceWorldObjectId)
            && !string.IsNullOrWhiteSpace(remoteColony.SourceMapId);
    }

    private static bool IsHostileRemoteColony(RemoteColonyMapParent remoteColony)
    {
        return string.Equals(remoteColony.RelationKind, "Hostile", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOrbitalTile(PlanetTile tile)
    {
        try
        {
            return tile.Valid && (tile.LayerDef.isSpace || Math.Max(0, tile.Layer.LayerID) > 0);
        }
        catch
        {
            return false;
        }
    }

    private static ModWorldMapMarkerDto BuildMarker(RemoteColonyMapParent remoteColony)
    {
        return new ModWorldMapMarkerDto
        {
            MarkerId = remoteColony.MarkerId,
            Kind = remoteColony.RuntimeKind,
            OwnerUserId = remoteColony.OwnerUserId,
            OwnerColonyId = remoteColony.OwnerColonyId,
            WorldObjectId = remoteColony.SourceWorldObjectId,
            MapId = remoteColony.SourceMapId,
            SnapshotId = remoteColony.SourceSnapshotId,
            Tile = remoteColony.Tile,
            Label = remoteColony.SourceLabel,
            RelatedEventId = remoteColony.RelatedEventId,
            CanRaid = remoteColony.CanRaid,
            CanTrade = remoteColony.CanTrade,
            CanReinforce = remoteColony.CanReinforce,
            RaidUnavailableReason = remoteColony.RaidUnavailableReason,
            RaidUnavailableUntilUtc = remoteColony.RaidUnavailableUntilUtc,
            RelationKind = remoteColony.RelationKind,
            OwnerOnline = remoteColony.OwnerOnline,
            OwnerLastSeenAtUtc = remoteColony.OwnerLastSeenAtUtc,
            OwnerFactionName = remoteColony.OwnerFactionName,
            Appearance = new ModColonyAppearanceDto
            {
                Mode = remoteColony.AppearanceMode,
                IconDefName = remoteColony.AppearanceIconDefName,
                ColorDefName = remoteColony.AppearanceColorDefName,
                ColorHex = remoteColony.AppearanceColorHex
            }
        };
    }

    private static Caravan? FindVehicleCaravan(object? vehicle)
    {
        if (vehicle is not Pawn vehiclePawn)
        {
            return null;
        }

        Caravan? caravan = Find.WorldObjects?.Caravans?
            .FirstOrDefault(candidate => candidate?.PawnsListForReading?.Contains(vehiclePawn) == true);
        if (caravan is not null)
        {
            return caravan;
        }

        if (vehicleWorldObjectsHolderType is null || vehicleWorldObjectsHolderVehicleCaravanObjectMethod is null)
        {
            return null;
        }

        try
        {
            MethodInfo? getComponent = typeof(World).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(method => method.Name == nameof(World.GetComponent) && method.IsGenericMethodDefinition)
                .FirstOrDefault(method => method.GetParameters().Length == 0);
            object? holder = getComponent?.MakeGenericMethod(vehicleWorldObjectsHolderType).Invoke(Find.World, Array.Empty<object>());
            return vehicleWorldObjectsHolderVehicleCaravanObjectMethod.Invoke(holder, new[] { vehicle }) as Caravan;
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim][ThirdPartyCompat][VehicleFramework] Failed to locate landed vehicle caravan: "
                + ex);
            return null;
        }
    }

    private static RaidCaravanMapEntryResult TryEnterRaidCaravanMap(
        Caravan caravan,
        Map map,
        IReadOnlyList<Pawn> attackPawns)
    {
        if (vehicleCaravanType is null
            || enterMapMethod is null
            || spawnParamsType is null
            || caravan is null
            || !vehicleCaravanType.IsAssignableFrom(caravan.GetType()))
        {
            return RaidCaravanMapEntryResult.NotHandled;
        }

        List<Pawn> pawns = attackPawns
            .Where(pawn => pawn is not null && !pawn.Destroyed && !pawn.Dead)
            .ToList();
        if (pawns.Count == 0)
        {
            return RaidCaravanMapEntryResult.Failed("Vehicle caravan has no available attackers.");
        }

        try
        {
            object? spawnParams = Activator.CreateInstance(spawnParamsType, CaravanEnterMode.Edge);
            if (spawnParams is null)
            {
                return RaidCaravanMapEntryResult.Failed("Vehicle caravan spawn parameters could not be created.");
            }

            enterMapMethod.Invoke(null, new[] { caravan, map, spawnParams });
            bool anyEntered = pawns.Any(pawn => pawn.Spawned && pawn.Map == map)
                || caravan.Destroyed
                || Find.WorldObjects?.Contains(caravan) == false;
            if (!anyEntered)
            {
                return RaidCaravanMapEntryResult.Failed("Vehicle caravan did not enter the raid map.");
            }

            List<string> attackThingIds = pawns
                .Select(pawn => pawn.ThingID)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return RaidCaravanMapEntryResult.Success(attackThingIds);
        }
        catch (Exception ex)
        {
            return RaidCaravanMapEntryResult.Failed(ex.ToString());
        }
    }

    private sealed class VehicleDefensePointState
    {
        public bool EnterVehicle;
    }

    private sealed class DisabledArrivalOptionInfo
    {
        public DisabledArrivalOptionInfo(string message)
        {
            Message = message;
        }

        public string Message { get; }
    }

}
