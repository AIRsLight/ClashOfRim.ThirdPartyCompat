using AIRsLight.ClashOfRim.ThirdPartyCompat.Statues;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using HarmonyLib;
using Verse;

namespace AIRsLight.ClashOfRim.ThirdPartyCompat.HumanoidAlienRaces;

internal static class HumanoidAlienRacesCompatibility
{
    public static void Apply(Harmony harmony)
    {
        ClashOfRimCompatibilityApi.RegisterCompatibilityPackage(
            ThirdPartyCompatibilityPackageKeys.HumanoidAlienRacesPackageId,
            IsHumanoidAlienRacesLoaded,
            new[]
            {
                ThirdPartyCompatibilityPackageKeys.HumanoidAlienRacesStatueMetadata
            });

        if (!IsHumanoidAlienRacesLoaded())
        {
            return;
        }

        CompStatueThingReferenceStateCompatibility.Register();
        DevLog("Humanoid Alien Races compatibility registered.");
    }

    private static bool IsHumanoidAlienRacesLoaded()
    {
        return ModLister.GetActiveModWithIdentifier(
            ThirdPartyCompatibilityPackageKeys.HumanoidAlienRacesPackageId,
            ignorePostfix: true) is not null
            && AccessTools.TypeByName("AlienRace.HARStatueContainer") is not null;
    }

    private static void DevLog(string message)
    {
        if (Prefs.DevMode)
        {
            Log.Message("[ClashOfRim.Compat][HAR] " + message);
        }
    }
}
