using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.RemoteMaps;
using AIRsLight.ClashOfRim.ThirdPartyCompat;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.ThirdPartyCompat.AdaptiveStorage;

public static class AdaptiveStorageCompatibility
{
    private const string ThingClassName = "AdaptiveStorage.ThingClass";
    private const int RemoteProjectionCellItemLimit = 1_000_000;
    private static readonly Dictionary<int, HashSet<IntVec3>> RemoteProjectionStorageCells = new();
    private static readonly Dictionary<Type, Func<Thing, object?>?> StoredThingsAccessors = new();
    private static bool runtimePatchesApplied;

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

        ClashOfRimCompatibilityApi.RegisterRemoteMapProjectionSanitizer(PrepareRemoteMapProjection);
        ClashOfRimCompatibilityApi.RegisterRemoteMapLoadedHandler(HandleRemoteMapLoaded);
        LongEventHandler.ExecuteWhenFinished(() => ApplyRuntimePatches(harmony));
    }

    private static void PrepareRemoteMapProjection(
        ModSnapshotPackageMetadataDto package,
        XElement mapElement,
        XElement referencePawnsElement)
    {
        ApplyRuntimePatches(new Harmony(ClashOfRimThirdPartyCompatMod.HarmonyId));
        RegisterProjectionStorageCells(mapElement);
    }

    private static void ApplyRuntimePatches(Harmony harmony)
    {
        if (runtimePatchesApplied || !IsModLoaded(ThirdPartyCompatibilityPackageKeys.AdaptiveStoragePackageId))
        {
            return;
        }

        MethodInfo? getMaxItemsAllowedInCell = AccessTools.Method(
            typeof(GridsUtility),
            nameof(GridsUtility.GetMaxItemsAllowedInCell),
            new[] { typeof(IntVec3), typeof(Map) });
        if (getMaxItemsAllowedInCell is null)
        {
            Log.Warning("[ClashOfRim.Compat] Adaptive Storage compatibility could not find GridsUtility.GetMaxItemsAllowedInCell.");
            return;
        }

        harmony.Patch(
            getMaxItemsAllowedInCell,
            postfix: new HarmonyMethod(typeof(AdaptiveStorageRemoteCellLimitPatch), nameof(AdaptiveStorageRemoteCellLimitPatch.Postfix)));
        runtimePatchesApplied = true;
    }

    private static void RegisterProjectionStorageCells(XElement mapElement)
    {
        if (!int.TryParse(mapElement.Element("uniqueID")?.Value?.Trim(), out int mapId))
        {
            return;
        }

        CleanupStaleProjectionStorageCells();

        HashSet<IntVec3> cells = new();
        foreach (XElement container in mapElement
            .Descendants("thing")
            .Where(IsAdaptiveStorageThingElement)
            .Concat(mapElement
                .Descendants("li")
                .Where(IsAdaptiveStorageThingElement))
            .Distinct())
        {
            string? defName = container.Element("def")?.Value?.Trim();
            ThingDef? def = string.IsNullOrWhiteSpace(defName)
                ? null
                : DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            if (def is null || !TryParseIntVec3(container.Element("pos")?.Value, out IntVec3 pos))
            {
                continue;
            }

            Rot4 rot = ParseRotation(container.Element("rot")?.Value);
            foreach (IntVec3 cell in GenAdj.OccupiedRect(pos, rot, def.Size))
            {
                cells.Add(cell);
            }
        }

        if (cells.Count > 0)
        {
            RemoteProjectionStorageCells[mapId] = cells;
        }
        else
        {
            RemoteProjectionStorageCells.Remove(mapId);
        }
    }

    internal static bool IsProjectionStorageCell(Map map, IntVec3 cell)
    {
        if (map is null
            || !RemoteProjectionStorageCells.TryGetValue(map.uniqueID, out HashSet<IntVec3> cells)
            || !cells.Contains(cell))
        {
            return false;
        }

        return RemoteMapProjectionLoadScope.Active
            || map.Parent is RemoteSessionMapParent;
    }

    private static void CleanupStaleProjectionStorageCells()
    {
        if (RemoteProjectionStorageCells.Count == 0)
        {
            return;
        }

        HashSet<int> activeRemoteMapIds = (Current.Game?.Maps ?? new List<Map>())
            .Where(map => map?.Parent is RemoteSessionMapParent)
            .Select(map => map.uniqueID)
            .ToHashSet();

        foreach (int mapId in RemoteProjectionStorageCells.Keys.ToList())
        {
            if (!activeRemoteMapIds.Contains(mapId))
            {
                RemoteProjectionStorageCells.Remove(mapId);
            }
        }
    }

    private static void HandleRemoteMapLoaded(
        Map map,
        RemoteSessionMapParent carrier,
        string scope,
        ModSnapshotPackageMetadataDto package)
    {
        int forbidden = ForbidRemoteStoredThings(map);
        CleanupStaleProjectionStorageCells();

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

    private static bool IsAdaptiveStorageThingElement(XElement element)
    {
        string? className = element.Attribute("Class")?.Value?.Trim();
        return string.Equals(className, ThingClassName, StringComparison.Ordinal)
            || string.Equals(className, "AdaptiveStorageFramework.AdaptiveStorage.ThingClass", StringComparison.Ordinal)
            || element.Element("StoredThings") is not null;
    }

    private static bool TryParseIntVec3(string? value, out IntVec3 result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string[] parts = value!.Trim().Trim('(', ')').Split(',');
        if (parts.Length != 3
            || !int.TryParse(parts[0].Trim(), out int x)
            || !int.TryParse(parts[1].Trim(), out int y)
            || !int.TryParse(parts[2].Trim(), out int z))
        {
            return false;
        }

        result = new IntVec3(x, y, z);
        return true;
    }

    private static Rot4 ParseRotation(string? value)
    {
        string trimmed = (value ?? string.Empty).Trim();
        if (int.TryParse(trimmed, out int index))
        {
            return new Rot4(Mathf.Clamp(index, 0, 3));
        }

        return trimmed.ToLowerInvariant() switch
        {
            "east" => Rot4.East,
            "south" => Rot4.South,
            "west" => Rot4.West,
            _ => Rot4.North
        };
    }

    private static bool IsModLoaded(string packageId)
    {
        return LoadedModManager.RunningModsListForReading.Any(
            mod => string.Equals(mod.PackageIdPlayerFacing, packageId, StringComparison.OrdinalIgnoreCase));
    }

    internal static int ProjectionCellItemLimit => RemoteProjectionCellItemLimit;
}

public static class AdaptiveStorageRemoteCellLimitPatch
{
    public static void Postfix(IntVec3 c, Map map, ref int __result)
    {
        if (__result < AdaptiveStorageCompatibility.ProjectionCellItemLimit
            && AdaptiveStorageCompatibility.IsProjectionStorageCell(map, c))
        {
            __result = AdaptiveStorageCompatibility.ProjectionCellItemLimit;
        }
    }
}
