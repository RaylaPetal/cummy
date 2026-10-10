using System;
using System.Collections.Generic;
using System.Linq;
using Cummy.Pairing;
using Cummy.State;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text;

namespace Cummy.Windows;

/// One "Cummy" right-click entry per player, opening a submenu: Pair / Layer... / Unpair on other players, the
/// layered parts and Clean everything on yourself.
public sealed class ContextMenuService : IDisposable
{
    private readonly PairingService pairing;
    private readonly LayerState layers;
    private readonly LayerPickerWindow picker;

    public ContextMenuService(PairingService pairing, LayerState layers, LayerPickerWindow picker)
    {
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
            // Read when opened, so the items match the pairing state at that moment.
            var peer = pairing.FindPeer(name, world);
            if (peer is null)
                return [Item(pairing.IsInvitePending(name, world) ? "Pair (invite sent)" : "Pair", _ => pairing.Invite(name, world))];
            return [Item("Layer...", _ => picker.Open(peer)), Item("Unpair", _ => pairing.Unpair(peer))];
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
