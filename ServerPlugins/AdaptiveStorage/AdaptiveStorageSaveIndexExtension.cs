using System.Xml.Linq;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.ThirdPartyCompat.ServerPlugin;

public sealed class AdaptiveStorageSaveIndexExtension : ISaveIndexExtension
{
    private const string AdaptiveStorageThingClass = "AdaptiveStorage.ThingClass";

    public IEnumerable<ThingSummary> ReadContainedThings(
        XElement containerThing,
        ThingSummary container,
        SaveIndexReadContext context)
    {
        if (!context.HasMod(ThirdPartyCompatibilityPackageKeys.AdaptiveStoragePackageId)
            || !IsAdaptiveStorageContainer(containerThing)
            || containerThing.Element("StoredThings") is not XElement storedThings
            || storedThings.Element("Things") is not XElement things)
        {
            yield break;
        }

        int index = 0;
        foreach (XElement item in things.Elements("li"))
        {
            string localId = Text(item, "id") ?? $"stored-{index}";
            string containedLocalId = $"{container.LocalId}/stored:{localId}";
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
                ContainerLocalId = container.LocalId
            };
            index++;
        }
    }

    private static bool IsAdaptiveStorageContainer(XElement thing)
    {
        return string.Equals(ClassName(thing), AdaptiveStorageThingClass, StringComparison.Ordinal)
            || thing.Element("StoredThings") is not null;
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
