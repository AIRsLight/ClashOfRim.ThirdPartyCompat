using AIRsLight.ClashOfRim.ThirdPartyCompat.Statues;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using HarmonyLib;
using Verse;

namespace AIRsLight.ClashOfRim.ThirdPartyCompat.FacialAnimation;

internal static class FacialAnimationCompatibility
{
    public static void Apply(Harmony harmony)
    {
        ClashOfRimCompatibilityApi.RegisterCompatibilityPackage(
            ThirdPartyCompatibilityPackageKeys.FacialAnimationPackageId,
            IsFacialAnimationLoaded,
            new[]
            {
                ThirdPartyCompatibilityPackageKeys.FacialAnimationStatueMetadata
            });

        if (!IsFacialAnimationLoaded())
        {
            return;
        }

        CompStatueThingReferenceStateCompatibility.Register();
        DevLog("Facial Animation compatibility registered.");
    }

    private static bool IsFacialAnimationLoaded()
    {
        return ModLister.GetActiveModWithIdentifier(
            ThirdPartyCompatibilityPackageKeys.FacialAnimationPackageId,
            ignorePostfix: true) is not null
            && AccessTools.TypeByName("FacialAnimation.HarmonyPatches+ExposableString") is not null;
    }

    private static void DevLog(string message)
    {
        if (Prefs.DevMode)
        {
            Log.Message("[ClashOfRim.Compat][FA] " + message);
        }
    }
}
