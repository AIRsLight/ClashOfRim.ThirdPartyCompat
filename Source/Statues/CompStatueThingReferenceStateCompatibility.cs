using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.ThirdPartyCompat.Statues;

internal static class CompStatueThingReferenceStateCompatibility
{
    private const string MetadataStateXmlBase64 = "clashofrim.thirdparty.compStatueStateXmlBase64";
    private const string RegistrationKey = "clashofrim.thirdparty.comp-statue-state";

    private static readonly MethodInfo? InitFakePawnMethod =
        AccessTools.Method(typeof(CompStatue), "InitFakePawn");

    private static bool registered;

    public static void Register()
    {
        if (registered)
        {
            return;
        }

        registered = true;
        ClashOfRimCompatibilityApi.RegisterThingReferenceMetadata(
            RegistrationKey,
            AppendThingReferenceMetadata,
            ThingReferenceMatches,
            ThingReferenceDtoMatches,
            TryApplyThingReferenceMetadata,
            ThingReferenceStrictness);
    }

    private static void AppendThingReferenceMetadata(Thing metadataThing, ModThingReferenceDto reference)
    {
        if (metadataThing?.TryGetComp<CompStatue>() is not { } comp || !comp.Active)
        {
            return;
        }

        string? xml = TrySaveCompState(comp);
        if (string.IsNullOrWhiteSpace(xml))
        {
            return;
        }

        SetMetadataText(reference, MetadataStateXmlBase64, Convert.ToBase64String(Encoding.UTF8.GetBytes(xml!)));
    }

    private static bool ThingReferenceMatches(ModThingReferenceDto requirement, Thing metadataThing)
    {
        return true;
    }

    private static bool ThingReferenceDtoMatches(ModThingReferenceDto requirement, ModThingReferenceDto candidate)
    {
        return true;
    }

    private static bool TryApplyThingReferenceMetadata(ModThingReferenceDto reference, Thing thing, out string? missingDefName)
    {
        missingDefName = null;
        string? encodedState = MetadataText(reference, MetadataStateXmlBase64);
        if (string.IsNullOrWhiteSpace(encodedState))
        {
            return true;
        }

        if (thing?.TryGetComp<CompStatue>() is not { } comp)
        {
            missingDefName = thing?.def?.defName;
            return false;
        }

        string xml;
        try
        {
            xml = Encoding.UTF8.GetString(Convert.FromBase64String(encodedState!));
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim.Compat][Statue] Invalid transferred statue state for "
                + (thing.def?.defName ?? "<unknown>")
                + ": "
                + ex.Message);
            missingDefName = thing.def?.defName;
            return false;
        }

        if (!TryLoadCompState(comp, xml, thing.def?.defName ?? "<unknown>"))
        {
            missingDefName = thing.def?.defName;
            return false;
        }

        return true;
    }

    private static int ThingReferenceStrictness(ModThingReferenceDto requirement)
    {
        return 0;
    }

    private static string? TrySaveCompState(CompStatue comp)
    {
        if (Scribe.mode != LoadSaveMode.Inactive)
        {
            return null;
        }

        string path = TemporaryPath();
        try
        {
            Scribe.saver.InitSaving(path, "root");
            if (Scribe.EnterNode("statue"))
            {
                comp.PostExposeData();
                Scribe.ExitNode();
            }

            Scribe.saver.FinalizeSaving();
            string xml = File.ReadAllText(path, Encoding.UTF8);
            return RemoveVolatileTaleReference(xml);
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim.Compat][Statue] Failed to save statue transfer state: " + ex.Message);
            Scribe.ForceStop();
            return null;
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static bool TryLoadCompState(CompStatue comp, string xml, string label)
    {
        if (Scribe.mode != LoadSaveMode.Inactive)
        {
            Log.Warning("[ClashOfRim.Compat][Statue] Cannot restore statue state while Scribe is active: " + label);
            return false;
        }

        string path = TemporaryPath();
        try
        {
            File.WriteAllText(path, xml, Encoding.UTF8);
            Scribe.loader.InitLoading(path);
            if (Scribe.EnterNode("statue"))
            {
                comp.PostExposeData();
                Scribe.ExitNode();
            }

            Scribe.loader.FinalizeLoading();
            InitFakePawnMethod?.Invoke(comp, Array.Empty<object>());
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim.Compat][Statue] Failed to restore statue transfer state for "
                + label
                + ": "
                + ex.GetType().Name
                + " "
                + ex.Message);
            Scribe.ForceStop();
            return false;
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static string RemoveVolatileTaleReference(string xml)
    {
        try
        {
            XDocument document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            document.Root?.Element("statue")?.Element("taleRef")?.Remove();
            return document.ToString(SaveOptions.DisableFormatting);
        }
        catch
        {
            return xml;
        }
    }

    private static string TemporaryPath()
    {
        return Path.Combine(
            Path.GetTempPath(),
            "clashofrim-statue-"
            + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture)
            + "-"
            + Guid.NewGuid().ToString("N")
            + ".xml");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Temporary transfer files are best-effort cleanup only.
        }
    }

    private static string? MetadataText(ModThingReferenceDto? reference, string key)
    {
        return reference?.Metadata is not null && reference.Metadata.TryGetValue(key, out string? value)
            ? value
            : null;
    }

    private static void SetMetadataText(ModThingReferenceDto? reference, string key, string? value)
    {
        if (reference is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            reference.Metadata?.Remove(key);
            return;
        }

        reference.Metadata ??= new Dictionary<string, string?>(StringComparer.Ordinal);
        reference.Metadata[key] = value;
    }
}
