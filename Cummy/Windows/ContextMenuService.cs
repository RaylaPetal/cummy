using System;
using System.Collections.Generic;
using System.Linq;
using Cummy.Pairing;
using Cummy.State;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text;

namespace Cummy.Windows;

/// One "Cummy" right-click entry per player, opening a submenu: Pair / Layer... / Lick... / Unpair on other players
/// (Layer... and Lick... only within their range), the
/// layered parts and Clean everything on yourself (only while manual cleaning is allowed).
public sealed class ContextMenuService : IDisposable
{
    private readonly Configuration config;
    private readonly PairingService pairing;
    private readonly LayerState layers;
    private readonly LayerPickerWindow picker;

    public ContextMenuService(Configuration config, PairingService pairing, LayerState layers, LayerPickerWindow picker)
    {
        this.config = config;
        this.pairing = pairing;
        this.layers = layers;
        this.picker = picker;
        Plugin.ContextMenu.OnMenuOpened += OnMenuOpened;
    }

    public void Dispose() => Plugin.ContextMenu.OnMenuOpened -= OnMenuOpened;

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (args.MenuType != ContextMenuType.Default || args.Target is not MenuTargetDefault target) return;
        if (layers.Current is null) return;

        // Players only: they have a content id or a home world; NPCs and objects have neither.
        if (target.TargetObject is { } obj && obj.ObjectKind != ObjectKind.Pc) return;
        var name = target.TargetName;
        var worldId = target.TargetHomeWorld.RowId;
        if (string.IsNullOrEmpty(name) || worldId == 0) return;
        var world = target.TargetHomeWorld.Value.Name.ExtractText();

        if (IsSelf(target, name, world))
        {
            AddCleanMenu(args);
            return;
        }

        args.AddMenuItem(Submenu(() =>
        {
            // Read when opened, so the items match the pairing state and distance at that moment.
            var peer = pairing.FindPeer(name, world);
            if (peer is null)
                return [Item(pairing.IsInvitePending(name, world) ? "Pair (invite sent)" : "Pair", _ => pairing.Invite(name, world))];
            var items = new List<IMenuItem>();
            var distance = Proximity.DistanceTo(name, world);
            if (distance <= Proximity.LayerRange) items.Add(Item("Layer...", _ => picker.Open(peer)));
            if (distance <= Proximity.LickRange) items.Add(Item("Lick...", _ => picker.OpenLick(peer)));
            items.Add(Item("Unpair", _ => pairing.Unpair(peer)));
            return items;
        }));
    }

    private static bool IsSelf(MenuTargetDefault target, string name, string world)
    {
        var contentId = Plugin.PlayerState.ContentId;
        if (target.TargetContentId != 0 && target.TargetContentId == contentId) return true;
        var me = Plugin.ObjectTable.LocalPlayer;
        return me != null
            && string.Equals(me.Name.TextValue, name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(me.HomeWorld.Value.Name.ExtractText(), world, StringComparison.OrdinalIgnoreCase);
    }

    private void AddCleanMenu(IMenuOpenedArgs args)
    {
        if (!config.ManualCleanAllowed) return;
        var dirty = layers.Snapshot();
        if (dirty.Count == 0) return;

        args.AddMenuItem(Submenu(() =>
        {
            var items = new List<IMenuItem>();
            foreach (var part in BodyParts.DisplayOrder.Where(dirty.Contains))
            {
                var p = part;
                items.Add(Item($"Clean {p.DisplayName()}", _ =>
                {
                    if (layers.Clean(p)) Notifier.Info($"Cleaned your {p.DisplayName().ToLowerInvariant()}.");
                }));
            }
            items.Add(Item("Clean everything", _ =>
            {
                if (layers.CleanAll()) Notifier.Info("All cleaned up.");
            }));
            return items;
        }));
    }

    /// The single "Cummy" entry; its items are built when it is clicked.
    private static MenuItem Submenu(Func<List<IMenuItem>> items) => new()
    {
        Name = "Cummy",
        PrefixChar = 'C',
        IsSubmenu = true,
        OnClicked = clicked => clicked.OpenSubmenu(items()),
    };

    private static MenuItem Item(string name, Action<IMenuItemClickedArgs> onClicked)
        => new() { Name = name, PrefixChar = 'C', OnClicked = onClicked };
}
