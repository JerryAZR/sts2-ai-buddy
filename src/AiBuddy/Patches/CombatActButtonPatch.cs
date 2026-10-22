using AiBuddy.Drivers;
using AiBuddy.Phantom;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace AiBuddy.Patches;

/// <summary>
/// The host-only "Buddy: act" button (design §3.5): triggers one buddy turn
/// sequence (buddies act in registration order; ended ones are woken). Host-local
/// Godot UI, invisible to vanilla guests.
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
            if (state != null && PhantomSession.Current != null)
            {
                CombatDriver.TriggerSequence(state);
            }
        };
        __instance.AddChild(button);
    }
}
