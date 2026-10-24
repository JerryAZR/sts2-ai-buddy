using AiBuddy.Drivers;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace AiBuddy.Patches;

/// <summary>
/// Room-exit hygiene for phantoms (vanilla mirror). Vanilla's BeforeLeavingRoom
/// force-skips the LOCAL player's leftover reward sets at the exact exit moment
/// (old room still the current location); phantoms have no "local" anywhere, so
/// this postfix does the same for their leftover sets, forged to guests.
/// Cleanup only — never pacing: the vote gate means the phantom's brain already
/// declared itself done before the party could leave.
/// </summary>
[HarmonyPatch(typeof(RewardsSetSynchronizer), nameof(RewardsSetSynchronizer.BeforeLeavingRoom))]
internal static class RoomExitHygienePatch
{
    private static void Postfix() => RewardsDriver.SkipPhantomLeftoverSets();
}
