using AiBuddy.Drivers;
using AiBuddy.Phantom;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace AiBuddy.Patches;

/// <summary>
/// The host-only "Buddy: act" button (design §3.5): arms every phantom to take one
/// combat action, waking any that already ended their turn. Host-local Godot UI,
/// invisible to vanilla guests.
/// </summary>
[HarmonyPatch(typeof(NCombatRoom), nameof(NCombatRoom._Ready))]
internal static class CombatActButtonPatch
{
    private const string ButtonName = "AiBuddyActButton";

    private static void Postfix(NCombatRoom __instance)
    {
        if (PhantomSession.Current == null || __instance.GetNodeOrNull<Button>(ButtonName) != null)
        {
            return;
        }
        var button = new Button
        {
            Name = ButtonName,
            Text = "Buddy: Act",
            Position = new Vector2(1640, 80),
            Size = new Vector2(220, 48),
        };
        button.Pressed += () =>
        {
            RunState? state = RunManager.Instance?.DebugOnlyGetState();
            if (state == null || PhantomSession.Current is not { } session)
            {
                return;
            }
            foreach (PhantomDescriptor phantom in session.Phantoms)
            {
                Player? player = state.GetPlayer(phantom.NetId);
                if (player != null)
                {
                    CombatDriver.ArmOrWake(player);
                }
            }
        };
        __instance.AddChild(button);
    }
}
