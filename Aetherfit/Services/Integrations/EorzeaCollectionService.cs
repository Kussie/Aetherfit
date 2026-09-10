using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Aetherfit.Services.Game;
using Glamourer.Api.Enums;
using Newtonsoft.Json.Linq;

namespace Aetherfit.Services.Integrations;

public enum EorzeaCollectionImportPhase
{
    Idle,
    Fetching,
    Error,
    Closed,
}

// Imports a design from an Eorzea Collection glamour page (e.g.
// ffxiv.eorzeacollection.com/glamour/353454/the-lovely-adventurer) via its unauthenticated JSON API.
// The rendered HTML page sits behind a Cloudflare JS challenge no plain HttpClient can solve, but the
// API endpoint (already used by Glamaholic, confirmed via its open-source Interop/EorzeaCollection.cs)
// only rejects HttpClient's default user-agent string, not a normal browser one.
public sealed class EorzeaCollectionService
{
    private const string ApiBaseUrl = "https://ffxiv.eorzeacollection.com/api/glamour/";
    private const string ChromeUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";
    private static readonly HttpClient Http = new();

    private static readonly (string JsonKey, EquipmentSlot Slot)[] GearSlotMap =
    {
        ("head", EquipmentSlot.Head),
        ("body", EquipmentSlot.Body),
        ("hands", EquipmentSlot.Hands),
        ("legs", EquipmentSlot.Legs),
        ("feet", EquipmentSlot.Feet),
        ("weapon", EquipmentSlot.MainHand),
        ("offhand", EquipmentSlot.OffHand),
        ("earrings", EquipmentSlot.Ears),
        ("necklace", EquipmentSlot.Neck),
        ("bracelets", EquipmentSlot.Wrists),
        ("left_ring", EquipmentSlot.LFinger),
        ("right_ring", EquipmentSlot.RFinger),
    };

    private readonly GameDataService gameData;

    public EorzeaCollectionService(GameDataService gameData)
    {
        this.gameData = gameData;
    }

    public EorzeaCollectionImportPhase Phase { get; private set; } = EorzeaCollectionImportPhase.Idle;
    public string? ErrorMessage { get; private set; }

    public void Reset()
    {
        Phase = EorzeaCollectionImportPhase.Idle;
        ErrorMessage = null;
    }

    public static bool IsEorzeaCollectionUrl(string url) => TryExtractGlamourId(url, out _);

    private static bool TryExtractGlamourId(string url, out long id)
    {
        id = 0;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return false;
        if (!uri.Host.Equals("ffxiv.eorzeacollection.com", StringComparison.OrdinalIgnoreCase))
            return false;

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2
            && segments[0].Equals("glamour", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(segments[1], out id);
    }

    public async Task ImportAsync(Plugin plugin, string url)
    {
        if (Phase == EorzeaCollectionImportPhase.Fetching)
            return;

        if (!TryExtractGlamourId(url, out var id))
        {
            Phase = EorzeaCollectionImportPhase.Error;
            ErrorMessage = "That doesn't look like an Eorzea Collection glamour link.";
            return;
        }

        Phase = EorzeaCollectionImportPhase.Fetching;
        ErrorMessage = null;

        JObject json;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBaseUrl}{id}");
            request.Headers.Add("User-Agent", ChromeUserAgent);
            using var response = await Http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                var challenged = response.Headers.Contains("cf-mitigated")
                    || body.Contains("Just a moment", StringComparison.OrdinalIgnoreCase);
                await Fail(challenged
                    ? "Eorzea Collection blocked this request - try again in a moment."
                    : $"Eorzea Collection returned an error ({(int)response.StatusCode}).");
                return;
            }

            json = JObject.Parse(body);
        }
        catch (Exception ex)
        {
            await Fail($"Failed to fetch that glamour: {ex.Message}");
            return;
        }

        var name = json["name"]?.ToString();
        if (string.IsNullOrWhiteSpace(name))
        {
            await Fail("Eorzea Collection returned an unexpected response.");
            return;
        }

        var gear = json["gear"] as JObject;
        var warnings = new List<string>();
        var equipment = new List<CachedEquipmentSlot>();

        foreach (var (jsonKey, slot) in GearSlotMap)
        {
            if (gear?[jsonKey] is not JObject entry)
                continue;

            var itemName = entry["name"]?.ToString();
            if (string.IsNullOrWhiteSpace(itemName))
                continue;

            if (!gameData.TryResolveItemIdByName(itemName, out var itemId))
            {
                warnings.Add($"{slot}: item '{itemName}' not found - skipped.");
                continue;
            }

            var (stain, stain2, applyStain) = ResolveDyes(entry["dyes"]?.ToString(), slot.ToString(), warnings);
            equipment.Add(new CachedEquipmentSlot
            {
                Slot = slot,
                ItemId = itemId,
                Stain = stain,
                Stain2 = stain2,
                Apply = true,
                ApplyStain = applyStain,
            });
        }

        // Facewear lives in Glamourer's Bonus section, not a normal equipment slot, and carries no dye
        // data at all (see GlamourerJsonSchema.ApplySingleBonusItem) - only the item itself is resolved.
        CachedBonusItem? bonusItem = null;
        if (gear?["facewear"] is JObject facewear
            && facewear["name"]?.ToString() is { } facewearName
            && !string.IsNullOrWhiteSpace(facewearName))
        {
            if (gameData.TryResolveBonusItemIdByName(facewearName, out var bonusId))
                bonusItem = new CachedBonusItem { Slot = "Glasses", ItemId = bonusId, Apply = true };
            else
                warnings.Add($"Facewear: item '{facewearName}' not found - skipped.");
        }

        var addResult = GlamourerApiEc.Success;
        var newId = Guid.Empty;
        string? addError = null;

        await Plugin.Framework.RunOnFrameworkThread(() =>
        {
            var (stateResult, state) = plugin.Glamourer.GetState();
            if (stateResult != GlamourerApiEc.Success || state == null)
            {
                addError = $"Couldn't read current Glamourer state ({stateResult}).";
                return;
            }

            var designJson = GlamourerJsonSchema.BuildEquipmentOnlyDesign(state, GlamourerJsonSchema.BuildEquipmentSection(equipment));
            if (bonusItem != null)
                GlamourerJsonSchema.ApplySingleBonusItem(designJson, bonusItem);

            (addResult, newId) = plugin.Glamourer.AddDesign(designJson, name);
        });

        if (addError != null)
        {
            await Fail(addError);
            return;
        }
        if (addResult != GlamourerApiEc.Success)
        {
            await Fail(addResult.ToString());
            return;
        }

        await Plugin.Framework.RunOnFrameworkThread(() =>
        {
            Plugin.ChatGui.Print($"{Plugin.ChatPrefix}Imported \"{name}\" from Eorzea Collection.");
            foreach (var warning in warnings)
                Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}{warning}");

            plugin.MainWindow.RefreshDesigns();
            plugin.MainWindow.OpenDesign(newId);
            Phase = EorzeaCollectionImportPhase.Closed;
        });
    }

    // "regal-purple" -> "regal purple"; "opo-opo-brown" -> "opo-opo brown" - only the last hyphen is a
    // real word separator, any earlier ones are part of the dye's own name.
    private static string DyeSlugToName(string slug)
    {
        var idx = slug.LastIndexOf('-');
        return idx < 0 ? slug : slug[..idx] + " " + slug[(idx + 1)..];
    }

    private (byte Stain, byte Stain2, bool ApplyStain) ResolveDyes(string? dyes, string slotLabel, List<string> warnings)
    {
        if (string.IsNullOrEmpty(dyes))
            return (0, 0, false);

        var parts = dyes.Split(',');
        var stain = parts.Length > 0 ? ResolveDye(parts[0], slotLabel, warnings) : (byte)0;
        var stain2 = parts.Length > 1 ? ResolveDye(parts[1], slotLabel, warnings) : (byte)0;
        return (stain, stain2, stain != 0 || stain2 != 0);
    }

    private byte ResolveDye(string slug, string slotLabel, List<string> warnings)
    {
        if (slug.Equals("none", StringComparison.OrdinalIgnoreCase))
            return 0;

        var name = DyeSlugToName(slug);
        if (gameData.TryResolveStainIdByName(name, out var stainId))
            return stainId;

        warnings.Add($"{slotLabel}: dye '{name}' not found - skipped.");
        return 0;
    }

    private Task Fail(string message) => Plugin.Framework.RunOnFrameworkThread(() =>
    {
        Phase = EorzeaCollectionImportPhase.Error;
        ErrorMessage = message;
    });
}
