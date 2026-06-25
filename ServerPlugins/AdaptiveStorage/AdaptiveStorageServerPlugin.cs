using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Network.Plugins;

namespace AIRsLight.ClashOfRim.ThirdPartyCompat.ServerPlugin;

public sealed class AdaptiveStorageServerPlugin : IClashOfRimServerPlugin
{
    public ClashOfRimServerPluginDescriptor Describe()
    {
        return new ClashOfRimServerPluginDescriptor(
            Id: "AIRsLight.ClashOfRim.AdaptiveStorage",
            Name: "ClashOfRim Adaptive Storage Compatibility",
            Version: "0.1.0",
            AssemblyName: string.Empty,
            FileName: string.Empty,
            Capabilities: new[]
            {
                ThirdPartyCompatibilityPackageKeys.AdaptiveStorageRemoteMapPackedContents,
                ThirdPartyCompatibilityPackageKeys.AdaptiveStorageSaveIndex
            });
    }

    public void Configure(ClashOfRimServerPluginContext context)
    {
        context.RegisterSaveIndexExtension(new AdaptiveStorageSaveIndexExtension());
    }
}
