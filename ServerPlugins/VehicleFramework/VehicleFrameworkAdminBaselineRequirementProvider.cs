using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Network.Plugins;

namespace AIRsLight.ClashOfRim.ThirdPartyCompat.ServerPlugin;

public sealed class VehicleFrameworkAdminBaselineRequirementProvider : IAdminBaselineRequirementProvider
{
    public IEnumerable<AdminBaselineExtensionRequirement> GetRequirements(AdminBaselineRequirementContext context)
    {
        if (context.ServerPackageIds.Contains(ThirdPartyCompatibilityPackageKeys.VehicleFrameworkPackageId))
        {
            yield return new AdminBaselineExtensionRequirement(
                ProviderId: ThirdPartyCompatibilityPackageKeys.VehicleFrameworkPackageId,
                Kind: "hitPointBaseline",
                RequiredPackageId: ThirdPartyCompatibilityPackageKeys.VehicleFrameworkPackageId,
                DisplayName: "Vehicle Framework hit point baseline");
        }
    }
}
