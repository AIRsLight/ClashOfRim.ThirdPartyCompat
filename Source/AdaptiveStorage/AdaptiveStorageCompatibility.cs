using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.RemoteMaps;
using AIRsLight.ClashOfRim.ThirdPartyCompat;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.ThirdPartyCompat.AdaptiveStorage;

public static class AdaptiveStorageCompatibility
{
    private const string ThingClassName = "AdaptiveStorage.ThingClass";
    private static readonly Dictionary<Type, Func<Thing, object?>?> StoredThingsAccessors = new();

    public static void Apply(Harmony harmony)
    {
        ClashOfRimCompatibilityApi.RegisterCompatibilityPackage(
            ThirdPartyCompatibilityPackageKeys.AdaptiveStoragePackageId,
            () => IsModLoaded(ThirdPartyCompatibilityPackageKeys.AdaptiveStoragePackageId),
            new[] { ThirdPartyCompatibilityPackageKeys.AdaptiveStorageRemoteMapPackedContents });

        if (!IsModLoaded(ThirdPartyCompatibilityPackageKeys.AdaptiveStoragePackageId))
        {
            return;
        }

        // Core 0.4.5+ restores saved stacks before buildings without applying live
        // overflow rules, for both vanilla and modded storage. Keep only the
        // Adaptive Storage-specific packed-content access policy here.
        ClashOfRimCompatibilityApi.RegisterRemoteMapLoadedHandler(HandleRemoteMapLoaded);
    }

    private static void HandleRemoteMapLoaded(
        Map map,
        RemoteSessionMapParent carrier,
        string scope,
        ModSnapshotPackageMetadataDto package)
    {
        int forbidden = ForbidRemoteStoredThings(map);

        if (forbidden > 0)
        {
            Log.Message("[ClashOfRim.Compat] Adaptive Storage forbade remote stored things: map="
                + map?.GetUniqueLoadID()
                + ", count="
                + forbidden
                + ".");
        }
    }

    private static int ForbidRemoteStoredThings(Map? map)
    {
        if (map?.listerThings?.AllThings is null)
        {
            return 0;
        }

        int forbidden = 0;
        var processedThingIds = new HashSet<int>();
        foreach (Thing container in map.listerThings.AllThings.ToList())
        {
            if (!IsAdaptiveStorageThing(container))
            {
                continue;
            }

            foreach (Thing storedThing in ReadStoredThings(container))
            {
                if (!ShouldForbidStoredThing(storedThing) || !processedThingIds.Add(storedThing.thingIDNumber))
                {
                    continue;
                }

                try
                {
                    storedThing.SetForbidden(true, warnOnFail: false);
                    forbidden++;
                }
                catch (Exception ex)
                {
                    Log.Warning("[ClashOfRim.Compat] Failed to forbid Adaptive Storage stored thing "
                        + Describe(storedThing)
                        + ": "
                        + ex.GetType().Name
                        + " "
                        + ex.Message);
                }
            }
        }

        return forbidden;
    }

    private static IReadOnlyList<Thing> ReadStoredThings(Thing container)
    {
        var result = new List<Thing>();
        Func<Thing, object?>? accessor = StoredThingsAccessor(container.GetType());
        if (accessor is null)
        {
            return result;
        }

        object? value;
        try
        {
            value = accessor(container);
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim.Compat] Failed to read Adaptive Storage contents for "
                + Describe(container)
                + ": "
                + ex.GetType().Name
                + " "
                + ex.Message);
            return result;
        }

        if (value is not IEnumerable enumerable)
        {
            return result;
        }

        foreach (object? entry in enumerable)
        {
            if (entry is Thing thing)
            {
                result.Add(thing);
            }
        }

        return result;
    }

    private static Func<Thing, object?>? StoredThingsAccessor(Type type)
    {
        if (StoredThingsAccessors.TryGetValue(type, out Func<Thing, object?>? cached))
        {
            return cached;
        }

        MethodInfo? getter = AccessTools.PropertyGetter(type, "StoredThings");
        if (getter is not null)
        {
            Func<Thing, object?> accessor = thing => getter.Invoke(thing, null);
            StoredThingsAccessors[type] = accessor;
            return accessor;
        }

        FieldInfo? field = AccessTools.Field(type, "_storedThings");
        if (field is not null)
        {
            Func<Thing, object?> accessor = thing => field.GetValue(thing);
            StoredThingsAccessors[type] = accessor;
            return accessor;
        }

        StoredThingsAccessors[type] = null;
        return null;
    }

    private static bool ShouldForbidStoredThing(Thing thing)
    {
        if (thing is null || thing.Destroyed || !thing.Spawned)
        {
            return false;
        }

        if (thing.def is null || !thing.def.EverHaulable)
        {
            return false;
        }

        return thing is ThingWithComps thingWithComps
            && thingWithComps.GetComp<CompForbiddable>() is not null;
    }

    private static bool IsAdaptiveStorageThing(Thing thing)
    {
        if (thing?.GetType() is not { } type)
        {
            return false;
        }

        return string.Equals(type.FullName, ThingClassName, StringComparison.Ordinal)
            || string.Equals(type.FullName, "AdaptiveStorageFramework.AdaptiveStorage.ThingClass", StringComparison.Ordinal)
            || string.Equals(type.Name, "ThingClass", StringComparison.Ordinal)
                && (type.Namespace?.IndexOf("AdaptiveStorage", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;
    }

    private static string Describe(Thing thing)
    {
        return (thing.def?.defName ?? thing.GetType().Name) + "/" + thing.ThingID;
    }

    private static bool IsModLoaded(string packageId)
    {
        return LoadedModManager.RunningModsListForReading.Any(
            mod => string.Equals(mod.PackageIdPlayerFacing, packageId, StringComparison.OrdinalIgnoreCase));
    }
}
