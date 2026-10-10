using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using TerritoryType = Lumina.Excel.Sheets.TerritoryType;

namespace Cummy.State;

/// Washing off (opt-in): every layered part is cleaned when the wearer starts swimming or diving, and after a while
/// outdoors in the rain. Wading is not detected (the only known check is a raw memory offset). Framework thread.
public sealed class WashOffService : IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

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

    private readonly Configuration config;
    private readonly LayerState layers;

    private DateTime nextTick = DateTime.MinValue;
    private bool wasInWater;
    private int rainSeconds;
    private int drySeconds;

    public WashOffService(Configuration config, LayerState layers)
    {
        this.config = config;
        this.layers = layers;
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose() => Plugin.Framework.Update -= OnUpdate;

    private void OnUpdate(IFramework framework)
    {
        if (DateTime.UtcNow < nextTick) return;
        nextTick = DateTime.UtcNow + Tick;
        if (!Plugin.ClientState.IsLoggedIn || Plugin.ObjectTable.LocalPlayer == null || layers.Current is null)
        {
            wasInWater = false;
            return;
        }

        // Water: once per entry. Tracked even while the option is off, so turning it on mid-swim does nothing.
        var inWater = Plugin.Condition[ConditionFlag.Swimming] || Plugin.Condition[ConditionFlag.Diving];
        var entered = inWater && !wasInWater;
        wasInWater = inWater;
        if (entered && config.WashInWater && layers.Snapshot().Count > 0 && layers.CleanAll())
        {
            ResetRain();
            Notifier.Info("You washed off in the water.");
            return;
        }

        if (!config.WashInRain) return;
        if (!IsRaining())
        {
            if (++drySeconds >= DryResetSeconds) ResetRain();
            return;
        }
        if (!IsOutdoors() || layers.Snapshot().Count == 0) return;   // sheltered or nothing to wash: the clock pauses

        drySeconds = 0;
        if (++rainSeconds < Math.Clamp(config.RainWashMinutes, 1, 10) * 60) return;
        ResetRain();
        if (layers.CleanAll()) Notifier.Info("The rain washed you clean.");
    }

    private void ResetRain() => (rainSeconds, drySeconds) = (0, 0);

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
