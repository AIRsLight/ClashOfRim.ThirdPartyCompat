using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using Verse;

namespace AIRsLight.ClashOfRim.ThirdPartyCompat.MeleeAnimation;

internal static class MeleeAnimationCompatibility
{
    private const string CleanupTagPrefix = "ClashOfRim.RemoteSessionCleanup:";

    private static readonly HashSet<string> RemoteSessionMapParentDefNames = new(StringComparer.Ordinal)
    {
        "ClashOfRim_RemoteSessionMapParent",
        "ClashOfRim_RemoteScoutMapParent",
        "ClashOfRim_RemoteRaidObservationMapParent",
        "ClashOfRim_RemoteRaidBattleMapParent"
    };

    public static void Apply()
    {
        ClashOfRimCompatibilityApi.RegisterCompatibilityPackage(
            ThirdPartyCompatibilityPackageKeys.MeleeAnimationPackageId,
            () => IsModLoaded(ThirdPartyCompatibilityPackageKeys.MeleeAnimationPackageId),
            new[] { ThirdPartyCompatibilityPackageKeys.MeleeAnimationRemoteMapRuntimeState });

        if (!IsModLoaded(ThirdPartyCompatibilityPackageKeys.MeleeAnimationPackageId))
        {
            DevLog("Melee Animation not loaded; compatibility sanitizer skipped.");
            return;
        }

        ClashOfRimCompatibilityApi.RegisterRemoteMapProjectionSanitizer(SanitizeProjectedRemoteMap);
        ClashOfRimCompatibilityApi.RegisterSnapshotSaveSanitizer(SanitizeSnapshotSave);
        DevLog("Melee Animation compatibility sanitizer registered.");
    }

    private static void SanitizeProjectedRemoteMap(
        ModSnapshotPackageMetadataDto package,
        XElement mapElement,
        XElement referencePawnsElement)
    {
        int cleared = ClearMeleeAnimationMapRuntimeState(mapElement);
        if (cleared > 0)
        {
            DevLog("Cleared Melee Animation projected remote-map runtime state: entries=" + cleared + ".");
        }
    }

    private static int SanitizeSnapshotSave(XDocument document)
    {
        HashSet<string> remoteParentLoadIds = FindRemoteSessionParentLoadIds(document);
        if (remoteParentLoadIds.Count == 0)
        {
            return 0;
        }

        List<XElement> remoteMaps = document.Root?
            .Element("game")?
            .Element("maps")?
            .Elements("li")
            .Where(map => remoteParentLoadIds.Contains(map.Element("mapInfo")?.Element("parent")?.Value?.Trim() ?? string.Empty))
            .ToList() ?? new List<XElement>();
        if (remoteMaps.Count == 0)
        {
            return 0;
        }

        HashSet<string> transientThingLoadIds = new(StringComparer.Ordinal);
        int clearedRuntimeState = 0;
        foreach (XElement map in remoteMaps)
        {
            AddTaggedTransientThingLoadIds(map, transientThingLoadIds);
            clearedRuntimeState += ClearMeleeAnimationMapRuntimeState(map);
        }

        int removedPawnData = RemoveTransientPawnMeleeData(document, transientThingLoadIds);
        if (clearedRuntimeState > 0 || removedPawnData > 0)
        {
            DevLog("Sanitized Melee Animation remote-map save state: maps="
                + remoteMaps.Count
                + ", animationState="
                + clearedRuntimeState
                + ", pawnData="
                + removedPawnData
                + ".");
        }

        return clearedRuntimeState + removedPawnData;
    }

    private static int ClearMeleeAnimationMapRuntimeState(XElement mapElement)
    {
        int cleared = 0;
        foreach (XElement component in mapElement
                     .Element("components")?
                     .Elements("li")
                     .Where(IsMeleeAnimationMapComponent)
                     .ToList() ?? new List<XElement>())
        {
            XElement? animationRenderers = component.Element("animationRenderers");
            if (animationRenderers is not null && animationRenderers.Elements().Any())
            {
                animationRenderers.RemoveNodes();
                cleared++;
            }
            else if (animationRenderers is null)
            {
                component.Add(new XElement("animationRenderers"));
            }
        }

        return cleared;
    }

    private static bool IsMeleeAnimationMapComponent(XElement component)
    {
        string className = component.Attribute("Class")?.Value ?? string.Empty;
        return string.Equals(className, "AM.AnimationManager", StringComparison.Ordinal)
            || className.EndsWith(".AnimationManager", StringComparison.Ordinal);
    }

    private static HashSet<string> FindRemoteSessionParentLoadIds(XDocument document)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (XElement worldObject in document.Descendants("li").Where(IsRemoteSessionWorldObject))
        {
            string id = worldObject.Element("ID")?.Value.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(id))
            {
                result.Add("WorldObject_" + id);
            }
        }

        return result;
    }

    private static bool IsRemoteSessionWorldObject(XElement element)
    {
        string def = element.Element("def")?.Value.Trim() ?? string.Empty;
        string className = element.Attribute("Class")?.Value ?? string.Empty;
        return RemoteSessionMapParentDefNames.Contains(def)
            || className.IndexOf("RemoteSessionMapParent", StringComparison.Ordinal) >= 0;
    }

    private static void AddTaggedTransientThingLoadIds(XElement map, ISet<string> thingLoadIds)
    {
        foreach (XElement thing in map.Descendants().Where(HasRemoteSessionCleanupTag))
        {
            string id = thing.Element("id")?.Value.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            thingLoadIds.Add(id);
            thingLoadIds.Add("Thing_" + id);
        }
    }

    private static bool HasRemoteSessionCleanupTag(XElement thing)
    {
        return thing.Element("questTags")?
            .Elements("li")
            .Any(tag => tag.Value.Trim().StartsWith(CleanupTagPrefix, StringComparison.Ordinal)) == true;
    }

    private static int RemoveTransientPawnMeleeData(XDocument document, ISet<string> transientThingLoadIds)
    {
        if (transientThingLoadIds.Count == 0)
        {
            return 0;
        }

        int removed = 0;
        foreach (XElement pawnData in document.Descendants("pawnMeleeData")
                     .Elements("li")
                     .Where(element => transientThingLoadIds.Contains(element.Element("pawn")?.Value.Trim() ?? string.Empty))
                     .ToList())
        {
            pawnData.Remove();
            removed++;
        }

        return removed;
    }

    private static bool IsModLoaded(string packageId)
    {
        return LoadedModManager.RunningModsListForReading.Any(
            mod => string.Equals(mod.PackageIdPlayerFacing, packageId, StringComparison.OrdinalIgnoreCase));
    }

    private static void DevLog(string message)
    {
        if (Prefs.DevMode)
        {
            Log.Message("[ClashOfRim.Compat] " + message);
        }
    }
}
