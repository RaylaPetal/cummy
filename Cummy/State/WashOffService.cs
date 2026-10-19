using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using HousingFurnitureSheet = Lumina.Excel.Sheets.HousingFurniture;
using HousingYardObjectSheet = Lumina.Excel.Sheets.HousingYardObject;
using TerritoryType = Lumina.Excel.Sheets.TerritoryType;

namespace Cummy.State;

/// Washing off (opt-in): layers rinse off one stage at a time while the wearer swims, dives or sits in a bath or
/// shower at a house, and once per rain interval outdoors in the rain. Wading is not detected (the only known check is
/// a raw memory offset). Framework thread.
public sealed class WashOffService : IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    /// Seconds in water per stage rinsed off.
    private const int WaterRinseSeconds = 5;

    /// A long enough dry spell forgets the rain so far.
    private const int DryResetSeconds = 10 * 60;

    /// Weather sheet rows (language-independent): Rain, Showers, Thunderstorms. 9 "Thunder" is dry lightning.
    private static readonly HashSet<byte> RainWeathers = [7, 8, 10];

    /// Territories that are open to the sky: cities, the open world, housing wards and the Island. Inns, housing
    /// interiors and duties report the zone's weather too, but nobody gets rained on there.
    private static readonly HashSet<TerritoryIntendedUse> OutdoorUses =
    [
        TerritoryIntendedUse.Town, TerritoryIntendedUse.Overworld, TerritoryIntendedUse.HousingOutdoor,
        TerritoryIntendedUse.IslandSanctuary,
    ];

    /// Furnishings that count as water (item IDs, checkable on Garland Tools): the Riviera, Glade, Oasis, Oriental and
    /// Carbuncle Bathtubs and the Shower Stand indoors; the Alpine Open-air Bath and Hingan Hot Spring in yards.
    private static readonly HashSet<uint> BathItemIds = [6591, 6592, 6593, 9719, 21833, 8783, 37397, 21866];

    /// How close counts as in a bath: horizontally from the item's centre, and vertically (so the tub upstairs doesn't).
    private const float BathRadius = 1.2f;
    private const float BathHeight = 1f;

    private readonly Configuration config;
    private readonly LayerState layers;

    /// The bath furnishings' sheet rows, by their low 16 bits (what placed furniture's id carries).
    private readonly HashSet<ushort> indoorBaths;
    private readonly HashSet<ushort> yardBaths;

    private DateTime nextTick = DateTime.MinValue;
    private int waterSeconds;
    private int rainSeconds;
    private int drySeconds;

    public WashOffService(Configuration config, LayerState layers)
    {
        this.config = config;
        this.layers = layers;
        indoorBaths = Plugin.DataManager.GetExcelSheet<HousingFurnitureSheet>()
            .Where(r => BathItemIds.Contains(r.Item.RowId)).Select(r => (ushort)r.RowId).ToHashSet();
        yardBaths = Plugin.DataManager.GetExcelSheet<HousingYardObjectSheet>()
            .Where(r => BathItemIds.Contains(r.Item.RowId)).Select(r => (ushort)r.RowId).ToHashSet();
        Plugin.Log.Debug($"Bath furniture rows: indoor {string.Join(", ", indoorBaths)}; yard {string.Join(", ", yardBaths)}.");
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose() => Plugin.Framework.Update -= OnUpdate;

    private void OnUpdate(IFramework framework)
    {
        if (DateTime.UtcNow < nextTick) return;
        nextTick = DateTime.UtcNow + Tick;
        if (!Plugin.ClientState.IsLoggedIn || Plugin.ObjectTable.LocalPlayer is not { } player || layers.Current is null)
        {
            waterSeconds = 0;
            return;
        }

        // Water: one stage every few seconds of each stay. Leaving keeps the stage reached; the next stay counts afresh.
        var swimming = Plugin.Condition[ConditionFlag.Swimming] || Plugin.Condition[ConditionFlag.Diving];
        var inWater = config.WashInWater && !layers.IsEmpty && (swimming || InBath(player.Position));
        if (!inWater)
        {
            waterSeconds = 0;
        }
        else if (++waterSeconds >= WaterRinseSeconds)
        {
            waterSeconds = 0;
            if (layers.Rinse() && layers.IsEmpty)
            {
                ResetRain();
                Notifier.Info(swimming ? "You rinsed clean in the water." : "You rinsed clean in the bath.");
                return;
            }
        }

        if (!config.WashInRain) return;
        if (!IsRaining())
        {
            if (++drySeconds >= DryResetSeconds) ResetRain();
            return;
        }
        if (!IsOutdoors() || layers.IsEmpty) return;   // sheltered or nothing to wash: the clock pauses

        drySeconds = 0;
        if (++rainSeconds < Math.Clamp(config.RainWashMinutes, 1, 10) * 60) return;
        rainSeconds = 0;
        if (layers.Rinse() && layers.IsEmpty) Notifier.Info("The rain washed you clean.");
    }

    private void ResetRain() => (rainSeconds, drySeconds) = (0, 0);

    /// Whether the wearer is in a placed bath or shower: the current house's furniture indoors, or the yard objects
    /// of the ward's plots outdoors.
    private unsafe bool InBath(System.Numerics.Vector3 at)
    {
        var housing = HousingManager.Instance();
        if (housing == null || housing->CurrentTerritory == null) return false;
        HousingFurnitureManager* furniture;
        HashSet<ushort> baths;
        switch (housing->CurrentTerritory->GetTerritoryType())
        {
            case HousingTerritoryType.Indoor when housing->IndoorTerritory != null:
                furniture = &housing->IndoorTerritory->FurnitureManager;
                baths = indoorBaths;
                break;
            case HousingTerritoryType.Outdoor when housing->OutdoorTerritory != null:
                furniture = &housing->OutdoorTerritory->FurnitureManager;
                baths = yardBaths;
                break;
            default:
                return false;
        }

        foreach (var item in furniture->FurnitureVector.AsSpan())
        {
            var f = item.Value;
            if (f == null || !baths.Contains((ushort)f->Id)) continue;
            var (dx, dz) = (f->Position.X - at.X, f->Position.Z - at.Z);
            if (dx * dx + dz * dz <= BathRadius * BathRadius && MathF.Abs(f->Position.Y - at.Y) <= BathHeight)
                return true;
        }
        return false;
    }

    private static unsafe bool IsRaining()
    {
        var weather = WeatherManager.Instance();
        return weather != null && RainWeathers.Contains(weather->GetCurrentWeather());
    }

    private static bool IsOutdoors()
    {
        if (Plugin.Condition[ConditionFlag.BoundByDuty] || Plugin.Condition[ConditionFlag.BoundByDuty56]
            || Plugin.Condition[ConditionFlag.BoundByDuty95])
            return false;
        var territory = Plugin.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(Plugin.ClientState.TerritoryType);
        return territory is { } t && OutdoorUses.Contains((TerritoryIntendedUse)t.TerritoryIntendedUse.RowId);
    }
}
