using System.Xml.Linq;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.ThirdPartyCompat.ServerPlugin;

public sealed class VehicleFrameworkSaveIndexExtension : ISaveIndexExtension
{
    private const string VehiclePawnClassPrefix = "Vehicles.VehiclePawn";
    public const string VehicleSettlementAssetKind = "VehicleFramework.Vehicle";
    public const string VehicleCargoSettlementAssetKind = "VehicleFramework.Cargo";

    public IEnumerable<ThingSummary> ReadContainedThings(
        XElement containerThing,
        ThingSummary container,
        SaveIndexReadContext context)
    {
        if (!context.HasMod(ThirdPartyCompatibilityPackageKeys.VehicleFrameworkPackageId) || !IsVehiclePawn(containerThing, container))
        {
            yield break;
        }

        ThingSummary? vehicleAsset = TryReadVehicleSettlementAsset(containerThing, container);
        if (vehicleAsset is not null)
        {
            yield return vehicleAsset;
        }

        int index = 0;
        foreach (XElement item in VehicleCargoItems(containerThing))
        {
            string localId = Text(item, "id") ?? $"cargo-{index}";
            string containedLocalId = $"{container.LocalId}/vehicle-cargo:{localId}";
            string? originalContainedLocalId = ContainedOriginalThingId(
                container,
                item,
                "vehicle-cargo",
                localId);
            yield return new ThingSummary(
                containedLocalId,
                context.Identity.ThingKey(context.MapUniqueId, containedLocalId),
                context.MapUniqueId,
                ClassName(item),
                Text(item, "def"),
                container.Position,
                Text(item, "faction") ?? container.Faction,
                Text(item, "stackCount"),
                Text(item, "health"),
                Text(item, "stuff"),
                ReadThingQuality(item),
                IsPawnElement(item))
            {
                ContainerGlobalKey = container.GlobalKey,
                ContainerLocalId = container.LocalId,
                ClashOfRimOriginalThingId = originalContainedLocalId,
                SettlementAssetKind = VehicleCargoSettlementAssetKind
            };
            index++;
        }

        int passengerIndex = 0;
        foreach (XElement pawn in VehiclePassengerPawns(containerThing))
        {
            string localId = Text(pawn, "id") ?? $"passenger-{passengerIndex}";
            string containedLocalId = $"{container.LocalId}/vehicle-passenger:{localId}";
            string? originalContainedLocalId = ContainedOriginalThingId(
                container,
                pawn,
                "vehicle-passenger",
                localId);
            yield return new ThingSummary(
                containedLocalId,
                context.Identity.ThingKey(context.MapUniqueId, containedLocalId),
                context.MapUniqueId,
                ClassName(pawn),
                Text(pawn, "def"),
                container.Position,
                Text(pawn, "faction") ?? container.Faction,
                Text(pawn, "stackCount"),
                Text(pawn, "health"),
                Text(pawn, "stuff"),
                ReadThingQuality(pawn),
                IsPawnElement(pawn))
            {
                ContainerGlobalKey = container.GlobalKey,
                ContainerLocalId = container.LocalId,
                ClashOfRimOriginalThingId = originalContainedLocalId
            };
            passengerIndex++;
        }
    }

    private static ThingSummary? TryReadVehicleSettlementAsset(XElement vehicleElement, ThingSummary vehicle)
    {
        IReadOnlyList<int> componentHitPoints = VehicleComponentHitPoints(vehicleElement).ToList();
        if (componentHitPoints.Count == 0)
        {
            return null;
        }

        int currentHitPoints = componentHitPoints.Sum();
        if (currentHitPoints <= 0)
        {
            return null;
        }

        return new ThingSummary(
            vehicle.LocalId,
            vehicle.GlobalKey + "/settlement-asset:vehicle",
            vehicle.MapUniqueId,
            vehicle.Class,
            vehicle.Def,
            vehicle.Position,
            vehicle.Faction,
            "1",
            currentHitPoints.ToString(),
            vehicle.Stuff,
            vehicle.Quality,
            IsPawn: false)
        {
            ClashOfRimOriginalThingId = vehicle.ClashOfRimOriginalThingId,
            SettlementAssetKind = VehicleSettlementAssetKind,
            SettlementDamageOnly = true
        };
    }

    private static bool IsVehiclePawn(XElement thing, ThingSummary container)
    {
        string className = ClassName(thing) ?? container.Class ?? string.Empty;
        return className.StartsWith(VehiclePawnClassPrefix, StringComparison.Ordinal);
    }

    private static IEnumerable<XElement> VehicleCargoItems(XElement vehicle)
    {
        return vehicle
            .Element("inventory")
            ?.Element("innerContainer")
            ?.Element("innerList")
            ?.Elements("li")
            ?? Enumerable.Empty<XElement>();
    }

    private static IEnumerable<XElement> VehiclePassengerPawns(XElement vehicle)
    {
        foreach (XElement handler in vehicle.Element("handlers")?.Elements("li") ?? Enumerable.Empty<XElement>())
        {
            foreach (XElement pawn in handler
                         .Element("thingOwner")
                         ?.Element("innerList")
                         ?.Elements("li")
                     ?? Enumerable.Empty<XElement>())
            {
                yield return pawn;
            }
        }
    }

    private static IEnumerable<int> VehicleComponentHitPoints(XElement vehicle)
    {
        foreach (XElement component in vehicle
            .Element("statHandler")
            ?.Element("components")
            ?.Elements("li") ?? Enumerable.Empty<XElement>())
        {
            string? health = Text(component, "health");
            if (double.TryParse(
                    health,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double parsed)
                && parsed > 0)
            {
                yield return Math.Max(1, (int)Math.Round(parsed, MidpointRounding.AwayFromZero));
            }
        }
    }

    private static string? ContainedOriginalThingId(
        ThingSummary container,
        XElement item,
        string scope,
        string localId)
    {
        string? originalContainerId = container.ClashOfRimOriginalThingId;
        string? originalItemId = Text(item, "clashOfRimOriginalThingId") ?? Text(item, "clashOfRimOriginalTrapId");
        if (string.IsNullOrWhiteSpace(originalContainerId) && string.IsNullOrWhiteSpace(originalItemId))
        {
            return null;
        }

        string containerId = string.IsNullOrWhiteSpace(originalContainerId)
            ? container.LocalId
            : originalContainerId!.Trim();
        string itemId = string.IsNullOrWhiteSpace(originalItemId)
            ? localId
            : originalItemId!.Trim();
        return $"{containerId}/{scope}:{itemId}";
    }

    private static string? ClassName(XElement element)
    {
        return element.Attribute("Class")?.Value;
    }

    private static string? Text(XElement? element, string name)
    {
        return element?.Element(name)?.Value.Trim();
    }

    private static string? ReadThingQuality(XElement thing)
    {
        XElement? compQuality = thing
            .Descendants("li")
            .FirstOrDefault(element => (ClassName(element) ?? string.Empty)
                .IndexOf("CompQuality", StringComparison.OrdinalIgnoreCase) >= 0);
        return Text(compQuality, "quality");
    }

    private static bool IsPawnElement(XElement element)
    {
        return ClassName(element) == "Pawn" || element.Element("kindDef") != null;
    }
}
