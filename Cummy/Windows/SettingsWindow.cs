using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Cummy.Compositor;
using Cummy.Ipc;
using Cummy.Messaging;
using Cummy.Pairing;
using Cummy.State;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace Cummy.Windows;

/// Preferences: drying off, blocked parts, custom overlay art.
public sealed class SettingsWindow : ThemedWindow
{
    private const string ImageFilter = "Images{.png,.jpg,.jpeg,.tga,.bmp}";

    private readonly Configuration configuration;
    private readonly LayerState layers;
    private readonly PairingService pairing;
    private readonly OverlaySources overlaySources;
    private readonly CompositorService compositor;
    private readonly MoodlesIpc moodles;
    private readonly MoodleService moodleService;
    private readonly DripService drip;
    private readonly CummyApi api;
    private readonly FileDialogManager fileDialog = new();
    private string? lastImageDir;
    /// The look being edited: shown while a control is dragged, saved and applied only when it is released.
    private LayerLook? lookDraft;

    private readonly PluginRelay relay;
    private readonly PluginOverlays pluginOverlays;
    private readonly CreampieService creampies;
    private readonly DripRings dripRings;

    public SettingsWindow(Configuration configuration, LayerState layers, PairingService pairing, OverlaySources overlaySources,
                          CompositorService compositor, MoodlesIpc moodles, MoodleService moodleService, DripService drip, CummyApi api,
                          PluginRelay relay, PluginOverlays pluginOverlays, CreampieService creampies, DripRings dripRings)
        : base("Cummy Settings###CummySettings")
    {
        this.creampies = creampies;
        this.dripRings = dripRings;
        this.pluginOverlays = pluginOverlays;
        this.relay = relay;
        this.drip = drip;
        this.api = api;
        this.compositor = compositor;
        this.moodles = moodles;
        this.moodleService = moodleService;
        this.configuration = configuration;
        this.layers = layers;
        this.pairing = pairing;
        this.overlaySources = overlaySources;

        Size = new Vector2(440, 480);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(380, 300), MaximumSize = new Vector2(1000, 1400) };
    }

    protected override void DrawContents()
    {
        fileDialog.Draw();

        using var tabs = ImRaii.TabBar("##settingsTabs");
        if (!tabs) return;

        using (var tab = ImRaii.TabItem("General"))
            if (tab) DrawGeneral();
        using (var tab = ImRaii.TabItem("Blocked parts"))
            if (tab) DrawBlocked();
        using (var tab = ImRaii.TabItem("Custom overlays"))
            if (tab) DrawCustomOverlays();
#if DEBUG
        using (var tab = ImRaii.TabItem("Debug"))
            if (tab) DrawDebug();
#endif
    }

#if DEBUG
    // ── Debug (dev builds only) ──────────────────────────────────────────────

    /// Layer yourself as if a paired partner had sent the tell: same handling, blocks, notices and compositing;
    /// only the chat transport is skipped.
    private void DrawDebug()
    {
        ImGuiHelpers.ScaledDummy(4f);
        if (layers.Current is null)
        {
            Ui.Muted("Log in to a character to use this.");
            return;
        }
        Ui.MutedWrapped("Act as if a paired, close partner had sent the tell. Dev builds only.");
        ImGuiHelpers.ScaledDummy(2f);
        string[] modes = ["They layer me", "They lick me", "They confirm my lick", "Unsolicited confirmation"];
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        ImGui.Combo("##debugMode", ref debugMode, modes, modes.Length);
        ImGuiHelpers.ScaledDummy(4f);
        using (var table = ImRaii.Table("##debugParts", 2, ImGuiTableFlags.SizingStretchSame))
        {
            if (table)
            {
                foreach (var part in BodyParts.DisplayOrder)
                {
                    ImGui.TableNextColumn();
                    var stage = layers.Stage(part);
                    var shown = stage == 0 ? "" : $" {stage}/{part.StageCount()}";
                    if (ImGui.Button($"{part.DisplayName()}{shown}##debug{part.Id()}", new Vector2(-1, 0)))
                    {
                        switch (debugMode)
                        {
                            case 0: pairing.SimulateLayerFromPartner(part); break;
                            case 1: pairing.SimulateLickFromPartner(part); break;
                            case 2: pairing.SimulateLickConfirmation(part, solicited: true); break;
                            default: pairing.SimulateLickConfirmation(part, solicited: false); break;
                        }
                    }
                    if (stage > 0 && ImGui.IsItemHovered())
                        ImGui.SetTooltip(stage >= part.StageCount() ? "Fully covered" : $"Stage {stage} of {part.StageCount()}: another layer advances it");
                }
                // Full Body isn't a part: it can only be layered (one stage on every unblocked part).
                if (debugMode == 0)
                {
                    ImGui.TableNextColumn();
                    if (ImGui.Button($"{BodyPart.FullBody.DisplayName()}##debug{BodyPart.FullBody.Id()}", new Vector2(-1, 0)))
                        pairing.SimulateLayerFromPartner(BodyPart.FullBody);
                }
            }
        }
        ImGuiHelpers.ScaledDummy(4f);
        DrawDebugStranger();
        ImGuiHelpers.ScaledDummy(4f);
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Broom, "Clean everything"))
            layers.CleanAll(LayerChange.Of(ChangeCause.Clean));
        ImGui.SameLine();
        using (ImRaii.Disabled(!configuration.DripEnabled))
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Tint, "Drip now"))
                drip.DripNow();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(configuration.DripEnabled ? "Every pending drip lands now." : "Turn dripping on in General first.");

        ImGuiHelpers.ScaledDummy(4f);
        Ui.Heading("Partner distances");
        foreach (var peer in pairing.PeersSorted())
        {
            var d = Proximity.DistanceTo(peer.Name, peer.World);
            Ui.Muted(d is { } y
                ? $"{peer.Key}: {y:0.0}y (layer {(y <= Proximity.LayerRange ? "ok" : "no")}, lick {(y <= Proximity.LickRange ? "ok" : "no")})"
                : $"{peer.Key}: not loaded");
        }

        ImGuiHelpers.ScaledDummy(4f);
        DrawDebugApi();
    }

    /// Every Cummy.* endpoint called through the published client (Cummy.Api/CummyIpc.cs), as another plugin would,
    /// and what its event subscribers receive.
    private void DrawDebugApi()
    {
        var c = api.DebugClient;
        Ui.Heading($"Plugin API (v{c.ApiVersion()})");
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##debugApiTarget", "Name Surname@World", ref debugApiTarget, 64);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(60 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##debugApiPart", "part id", ref debugApiPart, 8);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80 * ImGuiHelpers.GlobalScale);
        ImGui.InputInt("stage##debugApiStage", ref debugApiStage);

        var at = debugApiTarget.IndexOf('@');
        var (name, world) = at > 0 ? (debugApiTarget[..at].Trim(), debugApiTarget[(at + 1)..].Trim()) : ("", "");
        var part = debugApiPart.Trim();
        void Show(string call, string outcome) => debugApiOutcome = $"{call}: {outcome}";

        if (ImGui.Button("SendLayer##debugApi")) Show("SendLayer", c.SendLayer(name, world, part));
        ImGui.SameLine();
        if (ImGui.Button("CanLayer##debugApi")) Show("CanLayer", c.CanLayer(name, world, part));
        ImGui.SameLine();
        if (ImGui.Button("LayerSelf##debugApi"))
            Show("LayerSelf", at > 0 ? c.LayerSelf(part, name, world) : c.LayerSelf(part, "Debug Partner", "Debug"));
        ImGui.SameLine();
        if (ImGui.Button("SendLick##debugApi")) Show("SendLick", c.SendLick(name, world, part));
        ImGui.SameLine();
        if (ImGui.Button("Invite##debugApi")) Show("Invite", c.Invite(name, world));
        ImGui.SameLine();
        if (ImGui.Button("SendCreampie##debugApi")) Show("SendCreampie", c.SendCreampie(name, world, part));
        ImGui.SameLine();
        if (ImGui.Button("CreampieSelf##debugApi"))
            Show("CreampieSelf", at > 0 ? c.CreampieSelf(part, name, world) : c.CreampieSelf(part, "Debug Partner", "Debug"));

        if (ImGui.Button("CleanSelf part##debugApi")) Show("CleanSelf", c.CleanSelf(part));
        ImGui.SameLine();
        if (ImGui.Button("CleanSelf all##debugApi")) Show("CleanSelf all", c.CleanSelf("all"));
        ImGui.SameLine();
        if (ImGui.Button("RinseSelf##debugApi")) Show("RinseSelf", c.RinseSelf());
        ImGui.SameLine();
        if (ImGui.Button("SetStage##debugApi")) Show("SetStage", c.SetStage(part, debugApiStage, name, world));
        ImGui.SameLine();
        if (ImGui.Button("LayerSelf off-thread##debugApi"))
            System.Threading.Tasks.Task.Run(() => c.LayerSelf(part, "Debug Partner", "Debug"))
                  .ContinueWith(t => Show("LayerSelf off-thread", t.Result));
        if (debugApiOutcome.Length > 0) Ui.Muted(debugApiOutcome);

        var cleaning = c.GetCleaning();
        Ui.Muted($"GetLayerCount: {c.GetLayerCount()}   Last OnLayersChanged: {api.DebugLastLayersChanged}");
        Ui.Muted($"GetStages: {{{string.Join(", ", c.GetStages().Select(kv => $"{kv.Key}:{kv.Value}"))}}}   "
                 + $"GetStage({part}): {c.GetStage(part)}");
        Ui.Muted($"GetBlockedParts: [{string.Join(", ", c.GetBlockedParts())}]   GetAccessMode: {c.GetAccessMode()}   "
                 + $"IsBlacklisted: {c.IsBlacklisted(name, world)}");
        Ui.Muted($"GetPartners: {string.Join("; ", c.GetPartners().Select(p => $"{p.Name}@{p.World} [{string.Join(",", p.Blocked)}]"))}");
        Ui.Muted($"GetNearby: {string.Join("; ", c.GetNearby().Select(n => $"{n.Name}@{n.World}{(n.Partner ? " (partner)" : "")}"))}");
        Ui.Muted($"GetCleaning: dry {cleaning.DryOff} {cleaning.DryMinutes}m, water {cleaning.WashWater}, rain {cleaning.WashRain}, "
                 + $"drip {cleaning.Drip}, manual {cleaning.ManualClean}");
        Ui.Muted($"GetCreampies: {{{string.Join(", ", c.GetCreampies().Select(kv => $"{kv.Key}:{kv.Value}s"))}}}  (holes: pussy, ass in the part box)");
        Ui.Muted($"GetParts: {string.Join(" ", c.GetParts().Select(p => p.IsTarget ? $"{p.Id}*" : $"{p.Id}/{p.Stages}{(p.Facial ? "f" : "")}"))}");
        DrawDebugMessaging(c, name, world);

        Ui.Muted("OnLayerReceived:");
        foreach (var line in api.DebugLog) Ui.Muted("  " + line);
        Ui.Muted("OnPartChanged:");
        foreach (var line in api.DebugPartLog) Ui.Muted("  " + line);
        Ui.Muted("Other events:");
        foreach (var line in api.DebugEventLog) Ui.Muted("  " + line);
    }

    /// The relay: the wire round-trip, a burst of sends (only two may wait at once), and a partner's message through the
    /// real receive checks.
    private void DrawDebugMessaging(CummyIpcClient.CummyIpc c, string name, string world)
    {
        ImGui.SetNextItemWidth(90 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##debugChannel", "channel", ref debugChannel, 32);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##debugPayload", "payload", ref debugPayload, 256);
        ImGui.SameLine();
        if (ImGui.Button("SendMessage##debugApi"))
            debugApiOutcome = $"SendMessage: {c.SendMessage(name, world, debugChannel, debugPayload)}";
        ImGui.SameLine();
        if (ImGui.Button("x4##debugApi"))
            debugApiOutcome = "SendMessage x4: " + string.Join(", ",
                Enumerable.Range(0, 4).Select(_ => c.SendMessage(name, world, debugChannel, debugPayload)));
        ImGui.SameLine();
        if (ImGui.Button("As them##debugApi"))
            debugApiOutcome = $"Simulated message: {relay.SimulateFrom(name, world, debugChannel, debugPayload)}";
        if (ImGui.Button("SetOverlay test##debugApi"))
        {
            // A shipped part image as Cummy's own test overlay, on the part in the box ("full" uses Chest's image whole).
            var art = BodyParts.TryParseId(debugApiPart.Trim(), out var p) && p != BodyPart.FullBody ? p : BodyPart.Chest;
            var file = Path.Combine(Plugin.PluginInterface.AssemblyLocation.Directory!.FullName, "Assets", art.Id(),
                                    OverlaySources.DiffuseFile);
            debugApiOutcome = $"SetOverlay: {c.SetOverlay("Cummy", "debug", debugApiPart.Trim(), file, "", debugOverlayOpacity)}";
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80 * ImGuiHelpers.GlobalScale);
        ImGui.InputInt("%##debugOverlayOpacity", ref debugOverlayOpacity);
        ImGui.SameLine();
        if (ImGui.Button("RemoveOverlay##debugApi")) debugApiOutcome = $"RemoveOverlay: {c.RemoveOverlay("Cummy", "debug")}";
        ImGui.SameLine();
        if (ImGui.Button("ClearOverlays##debugApi")) debugApiOutcome = $"ClearOverlays: {c.ClearOverlays("Cummy")}";
        Ui.Muted($"GetOverlays: {string.Join("; ", c.GetOverlays().Select(o => $"{o.Owner}/{o.Key} on {o.Target}"))}");

        var wire = Cummy.Messaging.Protocol.Message(debugChannel, debugPayload);
        Ui.Muted(wire == null
            ? $"Wire: invalid ({debugPayload.Length} chars)"
            : $"Wire: {wire} -> {(Cummy.Messaging.Protocol.Parse(wire) is { } m ? $"[{m.Channel}] {m.Payload}" : "unparsed")}");
    }

    /// Act as any `Name@World` (paired or not, blacklisted or not) through the real access checks: Public mode, pairing
    /// and the blacklist decide, as for a real tell. Range isn't checked.
    private void DrawDebugStranger()
    {
        Ui.Heading("As someone else");
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##debugStranger", "Name Surname@World", ref debugStranger, 64);
        var parsed = TryParseNameWorld(debugStranger, out var name, out var world);
        using (ImRaii.Disabled(!parsed))
        {
            ImGui.SameLine();
            if (ImGui.Button("Layer my chest##debugStranger")) pairing.SimulateLayerFrom(name, world, BodyPart.Chest);
            ImGui.SameLine();
            if (ImGui.Button("Invite me##debugStranger")) pairing.SimulateInviteFrom(name, world);
            if (ImGui.Button("Creampie my pussy##debugStranger")) pairing.SimulateCreampieFrom(name, world, Hole.Pussy);
            ImGui.SameLine();
            if (ImGui.Button("Creampie my ass##debugStranger")) pairing.SimulateCreampieFrom(name, world, Hole.Ass);
        }
        ImGui.SameLine();
        if (ImGui.Button("End drips##debugStranger"))
            Notifier.Info($"[debug] Ended {creampies.EndAll(CreampieCause.Clean)} drip(s).");
        Ui.Muted("Dripping: " + (creampies.Active.Count == 0 ? "nothing"
            : string.Join(", ", creampies.Active.Select(h => $"{h.ApiName()} {creampies.Remaining(h):mm\\:ss}")))
                 + $"   Ring via Glamourer: {(dripRings.CanWearRing ? "yes" : "no")}   Slots: "
                 + (dripRings.Assignment.Count == 0 ? "none" : string.Join(", ", dripRings.Assignment.Select(kv => $"{kv.Key.ApiName()}->{kv.Value}"))));
        Ui.Muted($"My presence tag: {Proximity.Heels?.Read(0, PresenceTag.Key) ?? "none"}"
                 + (Proximity.Heels is { IsAvailable: true } ? "" : " (SimpleHeels unavailable)"));
        Ui.Muted(pairing.IsBlacklisted(name, world) ? "They're blacklisted."
                 : pairing.FindPeer(name, world) != null ? "They're a partner."
                 : $"They're unpaired; you're in {(pairing.PublicLayering ? "Public" : "Pairing")} mode.");
    }

    private int debugMode;
    private string debugStranger = "";
    private string debugApiTarget = "";
    private string debugApiPart = "ch";
    private string debugApiOutcome = "";
    private int debugApiStage = 1;
    private string debugChannel = "scene";
    private string debugPayload = "start 42";
    private int debugOverlayOpacity = 100;
#endif

    // ── General ──────────────────────────────────────────────────────────────

    private void DrawGeneral()
    {
        ImGuiHelpers.ScaledDummy(4f);
        DrawAccess();
        ImGuiHelpers.ScaledDummy(8f);
        DrawPlugins();
        ImGuiHelpers.ScaledDummy(8f);

        Ui.Heading("Compositing");
        var own = configuration.UseOwnCompositorWithProteus;
        if (ImGui.Checkbox("Always use Cummy's own compositor", ref own))
        {
            configuration.UseOwnCompositorWithProteus = own;
            configuration.Save();
            compositor.Schedule();
        }
        ImGuiComponents.HelpMarker("When the Proteus plugin is loaded, Cummy normally hands your body layers to it as an "
                                 + "overlay pack (the \"Cummy layers\" options of the Cummy mod), so the two never fight over your skin. Tick "
                                 + "this to keep Cummy's own look instead; then keep Proteus below Cummy in Penumbra.");
        ImGuiHelpers.ScaledDummy(8f);

        DrawLook();
        ImGuiHelpers.ScaledDummy(8f);

        Ui.Heading("Drying off");
        Ui.MutedWrapped("While on, you can't clean yourself by hand.");

        var dryOff = configuration.DryOffEnabled;
        if (ImGui.Checkbox("Dry off over time", ref dryOff))
        {
            configuration.DryOffEnabled = dryOff;
            if (dryOff) layers.ResetDryTimers();
            configuration.Save();
        }
        ImGuiComponents.HelpMarker("Layered parts clean themselves this long after being layered.");

        using (ImRaii.Disabled(!configuration.DryOffEnabled))
        {
            var minutes = configuration.DryOffMinutes;
            ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
            if (ImGui.SliderInt("##dryMinutes", ref minutes, Configuration.MinDryMinutes, 240, "%d minutes"))
            {
                configuration.DryOffMinutes = Math.Max(Configuration.MinDryMinutes, minutes);
                configuration.Save();
            }
        }
        ImGuiHelpers.ScaledDummy(8f);

        Ui.Heading("Dripping");
        var dripping = configuration.DripEnabled;
        if (ImGui.Checkbox("Drip onto the parts below", ref dripping))
        {
            configuration.DripEnabled = dripping;
            configuration.Save();
        }
        ImGuiComponents.HelpMarker("Layered parts drip one stage onto the parts below them every interval (chest onto stomach "
                                 + "and arms, stomach onto groin and legs, legs onto feet, face onto chest...), never past "
                                 + "the stage above. Blocked parts are skipped and stop the drip. Only while you're logged in.");

        using (ImRaii.Disabled(!configuration.DripEnabled))
        {
            var dripMinutes = Math.Clamp(configuration.DripMinutes, Configuration.MinDripMinutes, Configuration.MaxDripMinutes);
            ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
            if (ImGui.SliderInt("##dripMinutes", ref dripMinutes, Configuration.MinDripMinutes, Configuration.MaxDripMinutes, "every %d minutes"))
            {
                configuration.DripMinutes = Math.Clamp(dripMinutes, Configuration.MinDripMinutes, Configuration.MaxDripMinutes);
                configuration.Save();
            }
        }
        if (configuration.DripEnabled && configuration.DryOffEnabled
            && configuration.DripInterval >= TimeSpan.FromMinutes(Math.Max(Configuration.MinDryMinutes, configuration.DryOffMinutes)))
        {
            Ui.Icon(FontAwesomeIcon.ExclamationTriangle, Ui.Warning);
            ImGui.SameLine();
            ImGui.PushTextWrapPos();
            ImGui.TextColored(Ui.Warning, "Layers dry off before they drip far: make the drip interval shorter than the drying time.");
            ImGui.PopTextWrapPos();
        }
        ImGuiHelpers.ScaledDummy(8f);

        DrawCreampie();
        ImGuiHelpers.ScaledDummy(8f);

        Ui.Heading("Washing off");
        Ui.MutedWrapped("While either is on, you can't clean yourself by hand.");
        var water = configuration.WashInWater;
        if (ImGui.Checkbox("Clean in water", ref water))
        {
            configuration.WashInWater = water;
            configuration.Save();
        }
        ImGuiComponents.HelpMarker("While you swim, dive, or sit in a bathtub, shower or hot spring at a house, your layers "
                                 + "rinse off one stage every few seconds. Wading in shallow water doesn't count.");
        var rain = configuration.WashInRain;
        if (ImGui.Checkbox("Clean in the rain", ref rain))
        {
            configuration.WashInRain = rain;
            configuration.Save();
        }
        ImGuiComponents.HelpMarker("Your layers rinse off one stage for each stretch of this long outdoors in rain, "
                                 + "showers or thunderstorms. Indoors and duties don't count; the time pauses between "
                                 + "showers and resets after 10 minutes without rain.");
        using (ImRaii.Disabled(!configuration.WashInRain))
        {
            var rainMinutes = Math.Clamp(configuration.RainWashMinutes, 1, 10);
            ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
            if (ImGui.SliderInt("##rainMinutes", ref rainMinutes, 1, 10, rainMinutes == 1 ? "%d minute of rain per stage" : "%d minutes of rain per stage"))
            {
                configuration.RainWashMinutes = Math.Clamp(rainMinutes, 1, 10);
                configuration.Save();
            }
        }
        ImGuiHelpers.ScaledDummy(8f);

        DrawMoodles();
        ImGuiHelpers.ScaledDummy(8f);

        Ui.Heading("Quick-layer button");
        var show = configuration.ShowQuickLayerButton;
        if (ImGui.Checkbox("Show quick-layer button", ref show))
        {
            configuration.ShowQuickLayerButton = show;
            configuration.Save();
        }
        ImGuiComponents.HelpMarker("A small droplet button, shown while someone is nearby: click it to pick a player "
                                 + "(your target, if nearby) and layer, lick or pair with them. Drag it to move it.");
        var locked = configuration.LockQuickLayerButton;
        if (ImGui.Checkbox("Lock position", ref locked))
        {
            configuration.LockQuickLayerButton = locked;
            configuration.Save();
        }
    }

    /// How long a creampie drips, and what the drip needs (Glamourer for the ring, an IVCS skeleton for the attach points).
    private void DrawCreampie()
    {
        Ui.Heading("Creampie");
        var minutes = Math.Clamp(configuration.CreampieMinutes, Configuration.MinCreampieMinutes, Configuration.MaxCreampieMinutes);
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderInt("##creampieMinutes", ref minutes, Configuration.MinCreampieMinutes, Configuration.MaxCreampieMinutes,
                            "drips for %d minutes"))
        {
            configuration.CreampieMinutes = Math.Clamp(minutes, Configuration.MinCreampieMinutes, Configuration.MaxCreampieMinutes);
            configuration.Save();
        }
        ImGuiComponents.HelpMarker("A partner's creampie makes your pussy or ass drip this long (another one restarts it) and "
                                 + "layers your groin or butt. Washing in water or a partner licking that part ends it early. "
                                 + "Block either in the Blocked tab.");
        if (!dripRings.CanWearRing)
            Ui.MutedWrapped("Glamourer isn't available: wear the Emperor's New Ring yourself (right hand) "
                          + "to see the drip.");
        Ui.MutedWrapped("The drip attaches to IVCS skeleton bones. Turn off the \"Dripping Pussy VFX\" mod if you have it, "
                      + "or you'll see its drip too.");
    }

    /// What other plugins may do through Cummy: message partners' plugins, and show their own overlays on you.
    private void DrawPlugins()
    {
        Ui.Heading("Other plugins");
        var relayOn = configuration.RelayPluginMessages;
        if (ImGui.Checkbox("Relay messages for other plugins", ref relayOn))
        {
            configuration.RelayPluginMessages = relayOn;
            configuration.Save();
        }
        ImGuiComponents.HelpMarker("Lets plugins built on Cummy talk to the same plugin on your partners' games, over "
                                 + "Cummy's hidden tells. Only between paired partners, never with anyone you blacklisted.");

        var show = configuration.ShowPluginOverlays;
        if (ImGui.Checkbox("Show overlays from other plugins", ref show))
        {
            configuration.ShowPluginOverlays = show;
            configuration.Save();
            pluginOverlays.SettingChanged();
        }
        ImGuiComponents.HelpMarker("Plugins built on Cummy can paint their own art on you (mud, paint...), above your "
                                 + "layers. They're never saved: each plugin puts its own back while it's running.");

        var all = pluginOverlays.All;
        if (all.Count == 0)
        {
            Ui.Muted("No plugin overlays right now.");
            return;
        }
        PluginOverlay? remove = null;
        foreach (var e in all)
        {
            using var id = ImRaii.PushId($"pluginOverlay{e.Id}");
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Trash)) remove = e;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Remove this overlay");
            ImGui.SameLine();
            var where = e.Part is { } part ? part.DisplayName() : "Full body";
            var state = !e.Ready ? " (checking...)" : !show ? " (hidden)" : "";
            ImGui.TextUnformatted($"{e.Owner} / {e.Key} - {where}, {e.Opacity}%{state}");
        }
        if (remove != null) pluginOverlays.Remove(remove.Owner, remove.Key);
    }

    /// Who can layer me: Pairing or Public mode, and the blacklist. Both belong to the logged-in character.
    private void DrawAccess()
    {
        Ui.Heading("Who can layer me");
        if (layers.Current is null)
        {
            Ui.Muted("Log in to a character to change this.");
            return;
        }

        var isPublic = pairing.PublicLayering;
        if (ImGui.RadioButton("Pairing", !isPublic)) pairing.SetPublicLayering(false);
        ImGui.SameLine();
        Ui.Muted("Only partners you've paired with can layer or lick you, and you them.");
        if (ImGui.RadioButton("Public", isPublic)) pairing.SetPublicLayering(true);
        ImGui.SameLine();
        Ui.Muted("Anyone with Cummy nearby can layer or lick you, without pairing.");
        if (isPublic)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, Ui.Warning))
                ImGui.TextWrapped("Your blocks and the usual range still apply. Strangers you can layer are other Cummy "
                                  + "users who are in Public mode too.");
        }
        // Strangers are only found through the SimpleHeels presence tag, which sync services carry.
        Ui.MutedWrapped(Proximity.Heels is { IsAvailable: true }
            ? "Other Cummy users nearby show in the droplet when you're synced with them (Lightless, Mare...) and both have SimpleHeels."
            : "Install SimpleHeels to see other Cummy users nearby - only partners are listed now.");

        ImGuiHelpers.ScaledDummy(4f);
        var entries = pairing.BlacklistEntries;
        Ui.Heading("Blacklist", entries.Count);
        Ui.MutedWrapped("Blacklisted players can never layer you, lick you or invite you, in either mode. "
                        + "Blacklisting a partner unpairs them.");
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        var submitted = ImGui.InputTextWithHint("##blacklistAdd", "Name Surname@World", ref blacklistInput, 64,
                                                ImGuiInputTextFlags.EnterReturnsTrue);
        var valid = TryParseNameWorld(blacklistInput, out var name, out var world);
        ImGui.SameLine();
        using (ImRaii.Disabled(!valid))
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Ban, "Add") || (submitted && valid))
            {
                pairing.Blacklist(name, world);
                blacklistInput = "";
            }
        }
        if (!valid && blacklistInput.Trim().Length > 0) Ui.Muted("Enter it as Name Surname@World.");

        BlacklistEntry? remove = null;
        foreach (var entry in entries)
        {
            Ui.Card($"blacklist{entry.Key}", CardStyle.Normal, () =>
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(entry.Name);
                ImGui.SameLine();
                Ui.Muted($"@{entry.World}");
                Ui.AlignRight(Ui.IconButtonWidth(FontAwesomeIcon.Times));
                if (ImGuiComponents.IconButton($"unblacklist{entry.Key}", FontAwesomeIcon.Times)) remove = entry;
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Remove {entry.Name} from your blacklist");
            });
        }
        if (remove != null) pairing.Unblacklist(remove);
    }

    private string blacklistInput = "";

    /// `Name Surname@World`, split at the last `@`, both sides trimmed and non-empty.
    private static bool TryParseNameWorld(string text, out string name, out string world)
    {
        var at = text.LastIndexOf('@');
        name = at > 0 ? text[..at].Trim() : "";
        world = at > 0 ? text[(at + 1)..].Trim() : "";
        return name.Length > 0 && world.Length > 0;
    }

    /// The moodle put on you when a partner's layer lands (its VFX is the one set on it in Moodles).
    private void DrawMoodles()
    {
        Ui.Heading("Moodles");
        var available = moodles.IsAvailable;
        if (!available)
            Ui.MutedWrapped("Moodles isn't loaded. Install or enable it to have a moodle applied when you're layered.");

        using (ImRaii.Disabled(!available))
        {
            var apply = configuration.ApplyMoodle;
            if (ImGui.Checkbox("Apply a moodle when I'm layered", ref apply))
            {
                configuration.ApplyMoodle = apply;
                configuration.Save();
            }
            ImGuiComponents.HelpMarker("When a partner's layer lands, this moodle is put on you, with the VFX, duration "
                                     + "and stacking you set on it in Moodles. It comes off once you're fully clean.");

            using (ImRaii.Disabled(!configuration.ApplyMoodle))
                DrawMoodlePicker();
        }

        if (!available) return;
        if (configuration.MoodleId != Guid.Empty && !moodles.Exists(configuration.MoodleId))
            Ui.Error($"\"{MoodleName()}\" no longer exists in Moodles. Choose another moodle.");
        if (moodleService.LastApplyIgnored)
            Ui.Error("Moodles ignored the last moodle. Turn on \"Allow remote apply\" in Moodles' settings.");
        else
            Ui.MutedWrapped("Moodles' \"Allow remote apply\" setting must be on.");
    }

    private void DrawMoodlePicker()
    {
        var iconSize = new Vector2(ImGui.GetTextLineHeight() * 0.75f, ImGui.GetTextLineHeight());
        ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);
        using var combo = ImRaii.Combo("Moodle##moodlePick", configuration.MoodleId == Guid.Empty ? "Choose a moodle" : MoodleName());
        if (!combo) return;

        if (ImGui.Selectable("None##moodleNone", configuration.MoodleId == Guid.Empty))
        {
            configuration.MoodleId = Guid.Empty;
            configuration.MoodleTitle = string.Empty;
            configuration.Save();
        }

        var list = moodles.Moodles(reload: ImGui.IsWindowAppearing());
        if (list.Count == 0) Ui.Muted("You have no saved moodles.");
        foreach (var m in list)
        {
            if (m.IconId != 0)
            {
                var icon = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(m.IconId)).GetWrapOrEmpty();
                ImGui.Image(icon.Handle, iconSize);
                ImGui.SameLine();
            }
            if (ImGui.Selectable($"{(m.Title.Length > 0 ? PlainTitle(m.Title) : m.Path)}##{m.Id}", m.Id == configuration.MoodleId))
            {
                configuration.MoodleId = m.Id;
                configuration.MoodleTitle = m.Title;
                configuration.Save();
            }
            if (m.Path.Length > 0 && m.Path != m.Title && ImGui.IsItemHovered())
                ImGui.SetTooltip(m.Path);
        }
    }

    /// The chosen moodle's title, live from Moodles when it can be, else the one saved when it was chosen.
    private string MoodleName()
    {
        foreach (var m in moodles.Moodles())
            if (m.Id == configuration.MoodleId)
                return m.Title.Length > 0 ? PlainTitle(m.Title) : m.Path;
        return configuration.MoodleTitle.Length > 0 ? PlainTitle(configuration.MoodleTitle) : "Unknown moodle";
    }

    /// A moodle title without Moodles' BBCode-style formatting tags ([color=white], [/color], [glow=...], [i]...).
    private static string PlainTitle(string title)
        => System.Text.RegularExpressions.Regex.Replace(title, @"\[/?[a-zA-Z]+(=[^\]]*)?\]", "");

    /// How layers look on your body. Every change recomposites your skin, so it applies when a control is released.
    private void DrawLook()
    {
        Ui.Heading("Look");
        var look = lookDraft ??= (configuration.Look ?? new LayerLook()).Clamped();
        var width = 220 * ImGuiHelpers.GlobalScale;
        var commit = false;

        var (r, g, b) = look.TintRgb();
        var colour = new Vector3(r, g, b) / 255f;
        ImGui.SetNextItemWidth(width);
        if (ImGui.ColorEdit3("Colour##lookTint", ref colour, ImGuiColorEditFlags.NoAlpha))
        {
            var c = Vector3.Clamp(colour, Vector3.Zero, Vector3.One) * 255f + new Vector3(0.5f);
            look.Tint = $"#{(int)c.X:X2}{(int)c.Y:X2}{(int)c.Z:X2}";
        }
        commit |= ImGui.IsItemDeactivatedAfterEdit();
        ImGuiComponents.HelpMarker("Multiplied into your layers' colour, so it shows best on light art; dark or strongly "
                                 + "coloured art barely changes. White leaves them as they are.");

        int Slider(string label, int value, int min, int max, string format)
        {
            ImGui.SetNextItemWidth(width);
            ImGui.SliderInt(label, ref value, min, max, format);
            commit |= ImGui.IsItemDeactivatedAfterEdit();
            return Math.Clamp(value, min, max);
        }
        look.Opacity = Slider("Opacity##lookOpacity", look.Opacity, -100, 100, "%d");
        ImGuiComponents.HelpMarker("Below 0 fades your layers; above 0 makes their thin, wet areas solid without "
                                 + "spreading them onto bare skin.");
        look.Shine = Slider("Shine##lookShine", look.Shine, 0, 100, "%d%%");
        look.SkinTone = Slider("Skin tone shows through##lookSkin", look.SkinTone, 0, 100, "%d%%");
        ImGuiComponents.HelpMarker("At 0% layers keep their own colour on any skin tone; higher lets your skin tone tint "
                                 + "them. With Proteus, its own skin-tone setting also applies.");

        var reset = false;
        using (ImRaii.Disabled(look.IsDefault))
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Undo, "Reset look"))
            {
                lookDraft = look = new LayerLook();
                reset = true;
            }
        }

        if (commit || reset)
        {
            var applied = look.Clamped();
            if (applied.Key != (configuration.Look ?? new LayerLook()).Clamped().Key)
            {
                configuration.Look = applied;
                configuration.Save();
                compositor.Schedule();
            }
        }
    }

    // ── Blocked parts ────────────────────────────────────────────────────────

    private void DrawBlocked()
    {
        ImGuiHelpers.ScaledDummy(4f);
        if (layers.Current is not { } me)
        {
            Ui.Muted("Log in to a character to change its blocked parts.");
            return;
        }

        Ui.MutedWrapped("Partners can't layer parts you block. They're told when this changes.");
        ImGuiHelpers.ScaledDummy(4f);

        ImGuiHelpers.ScaledDummy(2f);
        using (var table = ImRaii.Table("##blocked", 2, ImGuiTableFlags.SizingStretchSame))
        {
            if (!table) return;
            foreach (var part in BodyParts.DisplayOrder)
            {
                ImGui.TableNextColumn();
                var blocked = part.IsBlockedBy(me.BlockMask);
                if (ImGui.Checkbox($"{part.DisplayName()}##block{part.Id()}", ref blocked))
                {
                    me.BlockMask = blocked ? me.BlockMask | part.Bit() : me.BlockMask & ~part.Bit();
                    configuration.Save();
                    pairing.BroadcastBlocks(part);
                }
            }
        }

        ImGuiHelpers.ScaledDummy(6f);
        Ui.Heading("Creampie");
        using (var holes = ImRaii.Table("##blockedHoles", 2, ImGuiTableFlags.SizingStretchSame))
        {
            if (!holes) return;
            foreach (var hole in Holes.All)
            {
                ImGui.TableNextColumn();
                var blocked = hole.IsBlockedBy(me.BlockMask);
                if (ImGui.Checkbox($"{hole.DisplayName()}##blockHole{hole.Id()}", ref blocked))
                {
                    me.BlockMask = blocked ? me.BlockMask | hole.BlockBit() : me.BlockMask & ~hole.BlockBit();
                    configuration.Save();
                    pairing.BroadcastAllBlocks();
                }
            }
        }
    }

    // ── Custom overlays ──────────────────────────────────────────────────────

    private void DrawCustomOverlays()
    {
        ImGuiHelpers.ScaledDummy(4f);
        Ui.MutedWrapped("Use your own art for your layers, transparent everywhere except the art. Everyone sees your "
                      + "art on you; it isn't sent to partners.");
        ImGuiHelpers.ScaledDummy(6f);

        Ui.Heading("Full-body image");
        Ui.MutedWrapped("One image in the Bibo UV layout, split into the body parts for you.");
        ImGuiHelpers.ScaledDummy(2f);
        using (var table = ImRaii.Table("##fullImages", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.PadOuterX))
        {
            if (table)
            {
                SetupSourceColumns();
                DrawSourceRow(OverlayTarget.FullBody);
            }
        }

        ImGuiHelpers.ScaledDummy(8f);
        Ui.Heading("Per-part overrides");
        Ui.MutedWrapped("Replace a single part's art, whether shipped or from the full-body image. Body parts use the "
                      + "Bibo UV layout; Face uses the face-paint layout and is mirrored onto both sides of your face. "
                      + "Each shows only where its current stage has art.");
        ImGuiHelpers.ScaledDummy(4f);
        using (var table = ImRaii.Table("##overrides", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.PadOuterX))
        {
            if (!table) return;
            SetupSourceColumns();
            foreach (var part in BodyParts.DisplayOrder)
                DrawSourceRow(OverlayTarget.Of(part));
        }
    }

    private static void SetupSourceColumns()
    {
        ImGui.TableSetupColumn("Part", ImGuiTableColumnFlags.WidthFixed, 80 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Using", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed);
    }

    /// One art source: a full image or a part's override.
    private void DrawSourceRow(OverlayTarget target)
    {
        var id = target.Key;
        var state = overlaySources.State(target);
        var busy = overlaySources.IsBusy(target);

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(target.DisplayName);

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        DrawSourceStatus(target, state, busy);

        ImGui.TableNextColumn();
        using (ImRaii.Disabled(busy))
        {
            if (ImGuiComponents.IconButton($"img{id}", FontAwesomeIcon.FileImage))
                Pick(target, normal: false);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Choose the image (PNG, JPEG, TGA or BMP with transparency)");

            ImGui.SameLine();
            using (ImRaii.Disabled(state != CustomState.Custom))
            {
                if (ImGuiComponents.IconButton($"nrm{id}", FontAwesomeIcon.Mountain))
                    Pick(target, normal: true);
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(state == CustomState.Custom
                    ? "Optional: choose a normal map for relief. Without one the art adds no relief."
                    : "Choose an image first; then you can add its normal map.");

            ImGui.SameLine();
            using (ImRaii.Disabled(state == CustomState.None))
            {
                if (ImGuiComponents.IconButton($"rst{id}", FontAwesomeIcon.Undo))
                    overlaySources.Reset(target);
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && state != CustomState.None)
                ImGui.SetTooltip("Remove this custom art and its normal map");
        }

        if (overlaySources.Error(target) is { } error)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TableNextColumn();
            Ui.Error(error);
        }
    }

    private void DrawSourceStatus(OverlayTarget target, CustomState state, bool busy)
    {
        if (busy)
        {
            Ui.Muted("Importing...");
            return;
        }
        var fallback = "Shipped art";
        if (target.Part is { } part && !part.IsFacial() && overlaySources.State(OverlayTarget.FullBody) == CustomState.Custom)
            fallback = "Full-body image";
        switch (state)
        {
            case CustomState.Custom:
                ImGui.TextColored(Ui.Accent, overlaySources.Record(target)?.OriginalName ?? "Custom");
                if (overlaySources.HasNormal(target))
                {
                    ImGui.SameLine();
                    Ui.Muted("+ normal");
                }
                break;
            case CustomState.Missing:
                Ui.Error(target.Part == null ? "Image missing - choose it again or reset" : $"Override missing - using {fallback.ToLowerInvariant()}");
                break;
            default:
                Ui.Muted(fallback);
                break;
        }
    }

    private void Pick(OverlayTarget target, bool normal)
    {
        var title = $"{target.DisplayName} {(normal ? "normal map" : "image")}";
        var start = lastImageDir ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        fileDialog.OpenFileDialog(title, ImageFilter, (ok, paths) =>
        {
            if (!ok || paths.Count == 0) return;
            lastImageDir = Path.GetDirectoryName(paths[0]);
            overlaySources.Import(target, normal, paths[0]);
        }, 1, start, true);
    }
}
