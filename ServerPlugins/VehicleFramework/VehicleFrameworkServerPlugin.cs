using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Network.Plugins;

namespace AIRsLight.ClashOfRim.ThirdPartyCompat.ServerPlugin;

public sealed class VehicleFrameworkServerPlugin : IClashOfRimServerPlugin
{
    public ClashOfRimServerPluginDescriptor Describe()
    {
        return new ClashOfRimServerPluginDescriptor(
            Id: "AIRsLight.ClashOfRim.VehicleFramework",
            Name: "ClashOfRim Vehicle Framework Compatibility",
            Version: "0.1.0",
            AssemblyName: string.Empty,
            FileName: string.Empty,
            Capabilities: new[]
            {
                ThirdPartyCompatibilityPackageKeys.VehicleFrameworkRaidVehicleEntry,
                ThirdPartyCompatibilityPackageKeys.VehicleFrameworkDefenderPassengerProtection,
                ThirdPartyCompatibilityPackageKeys.VehicleFrameworkHitPointBaseline,
                ThirdPartyCompatibilityPackageKeys.VehicleFrameworkSaveIndex,
                ThirdPartyCompatibilityPackageKeys.VehicleFrameworkRaidSettlementDamage
            },
            RequiredPackageIds: new[]
            {
                ThirdPartyCompatibilityPackageKeys.VehicleFrameworkPackageId
            });
    }

    public void Configure(ClashOfRimServerPluginContext context)
    {
        context.RegisterSaveIndexExtension(new VehicleFrameworkSaveIndexExtension());
        context.RegisterAdminBaselineRequirementProvider(new VehicleFrameworkAdminBaselineRequirementProvider());
        context.RegisterRaidSettlementSnapshotEditorExtension(new VehicleFrameworkRaidSettlementSnapshotEditorExtension());
    }
}
