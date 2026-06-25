using AIRsLight.ClashOfRim.ThirdPartyCompat.AdaptiveStorage;
using AIRsLight.ClashOfRim.ThirdPartyCompat.FacialAnimation;
using AIRsLight.ClashOfRim.ThirdPartyCompat.HumanoidAlienRaces;
using AIRsLight.ClashOfRim.ThirdPartyCompat.MeleeAnimation;
using AIRsLight.ClashOfRim.ThirdPartyCompat.VanillaExpandedFramework;
using AIRsLight.ClashOfRim.ThirdPartyCompat.VehicleFramework;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using HarmonyLib;
using Verse;

namespace AIRsLight.ClashOfRim.ThirdPartyCompat;

public sealed class ClashOfRimThirdPartyCompatMod : Mod
{
    public const string HarmonyId = "AIRsLight.ClashOfRim.ThirdPartyCompat";
    private const string RegistrationOwner = "third-party-compat";

    public ClashOfRimThirdPartyCompatMod(ModContentPack content)
        : base(content)
    {
        Harmony harmony = new(HarmonyId);
        ClashOfRimCompatibilityApi.RevokeRegistrationsByOwner(RegistrationOwner);
        CompatibilityRegistrationToken token = ClashOfRimCompatibilityApi.CreateRegistrationToken(RegistrationOwner);
        ClashOfRimCompatibilityApi.UseRegistrationToken(token, () =>
        {
            AdaptiveStorageCompatibility.Apply(harmony);
            VehicleFrameworkCompatibility.Apply(harmony);
            MeleeAnimationCompatibility.Apply();
            VanillaExpandedFrameworkCompatibility.Apply(harmony);
            HumanoidAlienRacesCompatibility.Apply(harmony);
            FacialAnimationCompatibility.Apply(harmony);
        });
    }
}
