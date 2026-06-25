using System.Globalization;
using System.Xml.Linq;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.ThirdPartyCompat.ServerPlugin;

public sealed class VehicleFrameworkRaidSettlementSnapshotEditorExtension : IRaidSettlementSnapshotEditorExtension
{
    private const string VehicleCargoLocalIdMarker = "/vehicle-cargo:";

    public bool TryApplySettlementDamage(XElement thing, RaidSettlementLoss loss)
    {
        if (!string.Equals(
                loss.Thing.SettlementAssetKind,
                VehicleFrameworkSaveIndexExtension.VehicleSettlementAssetKind,
                StringComparison.Ordinal))
        {
            return false;
        }

        if (!IsVehiclePawn(thing))
        {
            return false;
        }

        List<XElement> components = VehicleComponents(thing).ToList();
        if (components.Count == 0)
        {
            return false;
        }

        var componentHealths = components
            .Select(component => new ComponentHealth(component, ReadComponentHealth(component)))
            .Where(entry => entry.Health > 0)
            .ToList();
        if (componentHealths.Count == 0)
        {
            return false;
        }

        int currentTotal = Math.Max(1, (int)Math.Round(componentHealths.Sum(entry => entry.Health), MidpointRounding.AwayFromZero));
        int targetTotal = Math.Clamp(
            loss.RemainingHitPointsAfterDamage ?? currentTotal,
            1,
            Math.Max(1, currentTotal - 1));
        if (targetTotal >= currentTotal)
        {
            return true;
        }

        IReadOnlyList<int> assignedHealths = AllocateComponentHealths(componentHealths, targetTotal);
        for (int index = 0; index < componentHealths.Count; index++)
        {
            SetComponentHealth(componentHealths[index].Element, assignedHealths[index]);
        }

        return true;
    }

    public void ApplyPostSettlementEdit(XElement targetMap, RaidSettlementDiffResult settlement)
    {
        Dictionary<string, XElement> vehiclesByLocalId = targetMap
            .Element("things")
            ?.Elements("thing")
            .Where(IsVehiclePawn)
            .Select(thing => new
            {
                Id = thing.Element("id")?.Value?.Trim(),
                Thing = thing
            })
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Id))
            .ToDictionary(entry => entry.Id!, entry => entry.Thing, StringComparer.Ordinal)
            ?? new Dictionary<string, XElement>(StringComparer.Ordinal);
        if (vehiclesByLocalId.Count == 0)
        {
            return;
        }

        HashSet<string> destroyedVehicleIds = settlement.Losses
            .Where(loss => loss.StolenStackCount > 0
                && string.Equals(
                    loss.Thing.SettlementAssetKind,
                    VehicleFrameworkSaveIndexExtension.VehicleSettlementAssetKind,
                    StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(loss.Thing.LocalId))
            .Select(loss => loss.Thing.LocalId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (string vehicleId in destroyedVehicleIds)
        {
            if (vehiclesByLocalId.TryGetValue(vehicleId, out XElement? vehicle))
            {
                ClearCargo(vehicle);
            }
        }

        foreach (RaidSettlementLoss cargoLoss in settlement.Losses.Where(IsVehicleCargoLoss))
        {
            if (string.IsNullOrWhiteSpace(cargoLoss.Thing.ContainerLocalId)
                || destroyedVehicleIds.Contains(cargoLoss.Thing.ContainerLocalId!)
                || !vehiclesByLocalId.TryGetValue(cargoLoss.Thing.ContainerLocalId!, out XElement? vehicle)
                || !TryVehicleCargoItemLocalId(cargoLoss.Thing, out string? cargoItemId))
            {
                continue;
            }

            ApplyCargoLoss(vehicle, cargoItemId!, cargoLoss.LossCount);
        }
    }

    private static IReadOnlyList<int> AllocateComponentHealths(
        IReadOnlyList<ComponentHealth> componentHealths,
        int targetTotal)
    {
        double currentTotal = componentHealths.Sum(entry => entry.Health);
        if (currentTotal <= 0)
        {
            return Enumerable.Repeat(0, componentHealths.Count).ToList();
        }

        double ratio = targetTotal / currentTotal;
        var assignments = componentHealths
            .Select((entry, index) =>
            {
                double exact = Math.Max(0, entry.Health * ratio);
                int floor = Math.Min((int)Math.Floor(entry.Health), (int)Math.Floor(exact));
                return new ComponentHealthAssignment(
                    index,
                    floor,
                    exact - floor,
                    Math.Max(0, (int)Math.Ceiling(entry.Health) - floor));
            })
            .ToList();

        int remaining = targetTotal - assignments.Sum(entry => entry.AssignedHealth);
        foreach (ComponentHealthAssignment assignment in assignments
            .OrderByDescending(entry => entry.FractionalRemainder)
            .ThenBy(entry => entry.Index))
        {
            if (remaining <= 0)
            {
                break;
            }

            int add = Math.Min(remaining, assignment.AvailableIncrease);
            assignment.AssignedHealth += add;
            remaining -= add;
        }

        return assignments
            .OrderBy(entry => entry.Index)
            .Select(entry => Math.Max(0, entry.AssignedHealth))
            .ToList();
    }

    private static IEnumerable<XElement> VehicleComponents(XElement vehicle)
    {
        return vehicle
            .Element("statHandler")
            ?.Element("components")
            ?.Elements("li") ?? Enumerable.Empty<XElement>();
    }

    private static IEnumerable<XElement> VehicleCargoItems(XElement vehicle)
    {
        return vehicle
            .Element("inventory")
            ?.Element("innerContainer")
            ?.Element("innerList")
            ?.Elements("li") ?? Enumerable.Empty<XElement>();
    }

    private static bool IsVehicleCargoLoss(RaidSettlementLoss loss)
    {
        return loss.LossCount > 0
            && !loss.Thing.IsPawn
            && !string.IsNullOrWhiteSpace(loss.Thing.ContainerLocalId)
            && loss.Thing.LocalId.IndexOf(VehicleCargoLocalIdMarker, StringComparison.Ordinal) >= 0;
    }

    private static void ClearCargo(XElement vehicle)
    {
        foreach (XElement item in VehicleCargoItems(vehicle).ToList())
        {
            item.Remove();
        }
    }

    private static void ApplyCargoLoss(XElement vehicle, string cargoItemId, int lossCount)
    {
        if (lossCount <= 0)
        {
            return;
        }

        XElement? item = VehicleCargoItems(vehicle)
            .FirstOrDefault(candidate => string.Equals(
                candidate.Element("id")?.Value?.Trim(),
                cargoItemId,
                StringComparison.Ordinal));
        if (item is null)
        {
            return;
        }

        int stackCount = ParseStackCount(item.Element("stackCount")?.Value);
        int remaining = stackCount - lossCount;
        if (remaining <= 0)
        {
            item.Remove();
            return;
        }

        XElement? stackElement = item.Element("stackCount");
        if (stackElement is null)
        {
            item.Add(new XElement("stackCount", remaining.ToString(CultureInfo.InvariantCulture)));
        }
        else
        {
            stackElement.Value = remaining.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static bool TryVehicleCargoItemLocalId(ThingSummary thing, out string? cargoItemId)
    {
        cargoItemId = null;
        int markerIndex = thing.LocalId.IndexOf(VehicleCargoLocalIdMarker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return false;
        }

        cargoItemId = thing.LocalId[(markerIndex + VehicleCargoLocalIdMarker.Length)..].Trim();
        return !string.IsNullOrWhiteSpace(cargoItemId);
    }

    private static bool IsVehiclePawn(XElement thing)
    {
        string className = thing.Attribute("Class")?.Value ?? string.Empty;
        return className.StartsWith("Vehicles.VehiclePawn", StringComparison.Ordinal);
    }

    private static double ReadComponentHealth(XElement component)
    {
        string? value = component.Element("health")?.Value?.Trim();
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? Math.Max(0, parsed)
            : 0;
    }

    private static void SetComponentHealth(XElement component, int health)
    {
        XElement? healthElement = component.Element("health");
        string value = health.ToString(CultureInfo.InvariantCulture);
        if (healthElement is null)
        {
            component.Add(new XElement("health", value));
        }
        else
        {
            healthElement.Value = value;
        }
    }

    private static int ParseStackCount(string? value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0
            ? parsed
            : 1;
    }

    private sealed record ComponentHealth(XElement Element, double Health);

    private sealed class ComponentHealthAssignment
    {
        public ComponentHealthAssignment(int index, int assignedHealth, double fractionalRemainder, int availableIncrease)
        {
            Index = index;
            AssignedHealth = assignedHealth;
            FractionalRemainder = fractionalRemainder;
            AvailableIncrease = availableIncrease;
        }

        public int Index { get; }

        public int AssignedHealth { get; set; }

        public double FractionalRemainder { get; }

        public int AvailableIncrease { get; }
    }
}
