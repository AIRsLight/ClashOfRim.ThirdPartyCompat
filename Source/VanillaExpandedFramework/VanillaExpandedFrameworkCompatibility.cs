using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.RemoteMaps;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using HarmonyLib;
using Verse;

namespace AIRsLight.ClashOfRim.ThirdPartyCompat.VanillaExpandedFramework;

internal static class VanillaExpandedFrameworkCompatibility
{
    private const string CleanupTagPrefix = "ClashOfRim.RemoteSessionCleanup:";

    private static readonly HashSet<string> RemoteSessionMapParentDefNames = new(StringComparer.Ordinal)
    {
        "ClashOfRim_RemoteSessionMapParent",
        "ClashOfRim_RemoteScoutMapParent",
        "ClashOfRim_RemoteRaidObservationMapParent",
        "ClashOfRim_RemoteRaidBattleMapParent"
    };

    private static MethodInfo? mvcfManagerMethod;
    private static MethodInfo? mvcfInitializeMethod;

    public static void Apply(Harmony harmony)
    {
        ClashOfRimCompatibilityApi.RegisterCompatibilityPackage(
            ThirdPartyCompatibilityPackageKeys.VanillaExpandedFrameworkPackageId,
            IsVanillaExpandedFrameworkLoaded,
            new[]
            {
                ThirdPartyCompatibilityPackageKeys.VanillaExpandedFrameworkWorldAuthorityGuards,
                ThirdPartyCompatibilityPackageKeys.VanillaExpandedFrameworkRemoteMapRuntimeState,
                ThirdPartyCompatibilityPackageKeys.VanillaExpandedFrameworkPawnVerbRestore
            });

        if (!IsVanillaExpandedFrameworkLoaded())
        {
            DevLog("Vanilla Expanded Framework not loaded; compatibility patches skipped.");
            return;
        }

        PatchFactionDiscoveryLoadedGame(harmony);
        PatchKcsgSkyfallerSaveImpact(harmony);
        PrepareMvcfPawnVerbRestore();

        ClashOfRimCompatibilityApi.RegisterSnapshotSaveSanitizer(SanitizeSnapshotSave);
        ClashOfRimCompatibilityApi.RegisterPawnPostRestoreLocalizer(RestoreMvcfPawnVerbManager);
        DevLog("Vanilla Expanded Framework compatibility registered.");
    }

    private static void PatchFactionDiscoveryLoadedGame(Harmony harmony)
    {
        Type? loadedGamePatchType = AccessTools.TypeByName(
            "VEF.Factions.VanillaExpandedFramework_GameComponentUtility_LoadedGame_Patch+LoadedGame");
        MethodInfo? onGameLoaded = AccessTools.Method(loadedGamePatchType, "OnGameLoaded");
        if (onGameLoaded is null)
        {
            Log.Warning("[ClashOfRim.Compat][VEF] Could not find VEF loaded-game faction discovery hook.");
            return;
        }

        harmony.Patch(
            onGameLoaded,
            prefix: new HarmonyMethod(typeof(VefFactionDiscoveryLoadedGamePatch), nameof(VefFactionDiscoveryLoadedGamePatch.Prefix)));
    }

    private static void PatchKcsgSkyfallerSaveImpact(Harmony harmony)
    {
        Type? skyfallerType = AccessTools.TypeByName("KCSG.KCSG_Skyfaller");
        MethodInfo? saveImpact = AccessTools.Method(skyfallerType, "SaveImpact");
        if (saveImpact is null)
        {
            DevLog("KCSG skyfaller type was not found; save-impact guard skipped.");
            return;
        }

        harmony.Patch(
            saveImpact,
            prefix: new HarmonyMethod(typeof(KcsgSkyfallerSaveImpactPatch), nameof(KcsgSkyfallerSaveImpactPatch.Prefix)));
    }

    private static void PrepareMvcfPawnVerbRestore()
    {
        Type? pawnVerbUtilityType = AccessTools.TypeByName("MVCF.Utilities.PawnVerbUtility");
        mvcfManagerMethod = pawnVerbUtilityType
            ?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .FirstOrDefault(method =>
                method.Name == "Manager"
                && method.GetParameters().Length >= 1
                && typeof(Pawn).IsAssignableFrom(method.GetParameters()[0].ParameterType));
        Type? verbManagerType = AccessTools.TypeByName("MVCF.VerbManager");
        mvcfInitializeMethod = verbManagerType
            ?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(method =>
                method.Name == "Initialize"
                && method.GetParameters().Length >= 1
                && typeof(Pawn).IsAssignableFrom(method.GetParameters()[0].ParameterType));

        if (mvcfManagerMethod is null || mvcfInitializeMethod is null)
        {
            DevLog("MVCF pawn verb manager API was not found; pawn restore refresh skipped.");
        }
    }

    private static void RestoreMvcfPawnVerbManager(Pawn pawn)
    {
        if (pawn is null || mvcfManagerMethod is null || mvcfInitializeMethod is null)
        {
            return;
        }

        try
        {
            ParameterInfo[] managerParameters = mvcfManagerMethod.GetParameters();
            object?[] managerArgs = managerParameters.Length switch
            {
                1 => new object?[] { pawn },
                _ => new object?[] { pawn, false }
            };
            object? manager = mvcfManagerMethod.Invoke(null, managerArgs);
            if (manager is null)
            {
                return;
            }

            ParameterInfo[] initializeParameters = mvcfInitializeMethod.GetParameters();
            object?[] initializeArgs = initializeParameters.Length switch
            {
                1 => new object?[] { pawn },
                _ => new object?[] { pawn, true }
            };
            mvcfInitializeMethod.Invoke(manager, initializeArgs);
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim.Compat][VEF] Failed to refresh MVCF pawn verb manager for "
                + pawn.LabelShort
                + ": "
                + ex.Message);
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

        HashSet<string> transientThingIds = new(StringComparer.Ordinal);
        int clearedMapRuntime = 0;
        foreach (XElement map in remoteMaps)
        {
            AddTaggedTransientThingIds(map, transientThingIds);
            clearedMapRuntime += ClearGuidedProjectileRuntimeState(map);
        }

        int removedDraftData = RemoveTransientDraftedActionData(document, transientThingIds);
        int changes = clearedMapRuntime + removedDraftData;
        if (changes > 0)
        {
            DevLog("Sanitized VEF remote-map save state: maps="
                + remoteMaps.Count
                + ", guidedProjectiles="
                + clearedMapRuntime
                + ", draftedActions="
                + removedDraftData
                + ".");
        }

        return changes;
    }

    private static int ClearGuidedProjectileRuntimeState(XElement mapElement)
    {
        int cleared = 0;
        foreach (XElement component in mapElement
                     .Element("components")?
                     .Elements("li")
                     .Where(IsVefGuidedProjectilesComponent)
                     .ToList() ?? new List<XElement>())
        {
            XElement? launcherTargets = component.Element("launcherTargets");
            if (launcherTargets is not null && launcherTargets.Elements().Any())
            {
                launcherTargets.RemoveNodes();
                cleared++;
            }
        }

        return cleared;
    }

    private static bool IsVefGuidedProjectilesComponent(XElement component)
    {
        string className = component.Attribute("Class")?.Value ?? string.Empty;
        return string.Equals(className, "VEF.Weapons.GuidedProjectiles", StringComparison.Ordinal)
            || className.EndsWith(".GuidedProjectiles", StringComparison.Ordinal);
    }

    private static int RemoveTransientDraftedActionData(XDocument document, ISet<string> transientThingIds)
    {
        if (transientThingIds.Count == 0)
        {
            return 0;
        }

        int removed = 0;
        foreach (XElement entry in document.Descendants("draftedActions")
                     .Elements("li")
                     .Where(entry => IsTransientDraftedActionEntry(entry, transientThingIds))
                     .ToList())
        {
            entry.Remove();
            removed++;
        }

        return removed;
    }

    private static bool IsTransientDraftedActionEntry(XElement entry, ISet<string> transientThingIds)
    {
        string key = entry.Element("key")?.Value.Trim() ?? string.Empty;
        string pawnId = entry.Element("value")?.Element("pawnID")?.Value.Trim()
            ?? entry.Element("pawnID")?.Value.Trim()
            ?? string.Empty;
        return transientThingIds.Contains(key) || transientThingIds.Contains(pawnId);
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

    private static void AddTaggedTransientThingIds(XElement map, ISet<string> thingIds)
    {
        foreach (XElement thing in map.Descendants().Where(HasRemoteSessionCleanupTag))
        {
            string id = thing.Element("id")?.Value.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            thingIds.Add(id);
            thingIds.Add("Thing_" + id);
        }
    }

    private static bool HasRemoteSessionCleanupTag(XElement thing)
    {
        return thing.Element("questTags")?
            .Elements("li")
            .Any(tag => tag.Value.Trim().StartsWith(CleanupTagPrefix, StringComparison.Ordinal)) == true;
    }

    private static bool IsVanillaExpandedFrameworkLoaded()
    {
        return LoadedModManager.RunningModsListForReading.Any(mod =>
            string.Equals(
                mod.PackageIdPlayerFacing,
                ThirdPartyCompatibilityPackageKeys.VanillaExpandedFrameworkPackageId,
                StringComparison.OrdinalIgnoreCase))
            || AccessTools.TypeByName("VEF.VFEGlobal") is not null;
    }

    private static void DevLog(string message)
    {
        if (Prefs.DevMode)
        {
            Log.Message("[ClashOfRim.Compat][VEF] " + message);
        }
    }
}

public static class VefFactionDiscoveryLoadedGamePatch
{
    public static bool Prefix()
    {
        if (!ClashOfRimCompatibilityApi.IsActiveMultiplayerSession)
        {
            return true;
        }

        if (ClashOfRimCompatibilityApi.IsCurrentUserAdministrator)
        {
            if (Prefs.DevMode)
            {
                Log.Message("[ClashOfRim.Compat][VEF] Allowed administrator faction discovery during multiplayer session.");
            }

            return true;
        }

        if (Prefs.DevMode)
        {
            Log.Message("[ClashOfRim.Compat][VEF] Suppressed local faction discovery during multiplayer session.");
        }

        ClashOfRimCompatibilityApi.RequestServerWorldBaselineRefreshByKey("ClashOfRim.WorldCatalog.ReasonFactionDiscovery");
        return false;
    }
}

public static class KcsgSkyfallerSaveImpactPatch
{
    public static bool Prefix(object __instance)
    {
        if (__instance is Thing thing
            && Scribe.mode == LoadSaveMode.Saving
            && RemoteSessionGlobalStateGuard.IsRemoteThing(thing))
        {
            if (Prefs.DevMode)
            {
                Log.Message("[ClashOfRim.Compat][VEF] Suppressed KCSG skyfaller save impact on remote map.");
            }

            return false;
        }

        return true;
    }
}
