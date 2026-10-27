using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;

namespace AiBuddy.Patches;

/// <summary>
/// M6 defensive guard (design §3.6): CanonicalizeSave full-roundtrips EVERY player
/// through RunState.FromSerializable, so any phantom-data validation surprise would
/// make vanilla rename the save .corrupt mid-campaign — destructive and irreversible
/// from the host's perspective. Suppress the rename: the load still fails with the
/// same ValidationFailed result (behavior change is limited to leaving the file in
/// place), and the failure is logged loudly instead.
/// </summary>
[HarmonyPatch(typeof(RunSaveManager), nameof(RunSaveManager.RenameBrokenMultiplayerRunSave))]
internal static class SaveGuardPatch
{
    private static bool Prefix(ReadSaveStatus status)
    {
        Log.Error($"[AiBuddy] Suppressed multiplayer save corrupt-rename (status {status}). " +
                  "The load result is unchanged; the save file is left in place.");
        Observability.EventLog.Write("mod", "save_corrupt_rename_suppressed", new { status = status.ToString() });
        return false;
    }
}
