using System.Reflection;
using AiBuddy.Phantom;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace AiBuddy.Patches;

/// <summary>
/// Host-side RequestEnqueue stamps the enqueue sender with the HOST's NetId,
/// ignoring action.OwnerId (vanilla assumes only the local player originates actions).
/// For phantom-owned actions we enqueue directly with the phantom's OwnerId, so the
/// ActionQueueSet keys the queue correctly and the ActionEnqueuedMessage broadcast
/// carries the phantom's id. (KitLib HostEnqueuePatch pattern.)
///
/// The vanilla NotPlayPhase deferral for combat actions is preserved verbatim.
/// </summary>
[HarmonyPatch(typeof(ActionQueueSynchronizer), nameof(ActionQueueSynchronizer.RequestEnqueue))]
internal static class PhantomActionEnqueuePatch
{
    private static readonly FieldInfo DeferredActions =
        typeof(ActionQueueSynchronizer).GetField("_requestedActionsWaitingForPlayerTurn", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException("ActionQueueSynchronizer._requestedActionsWaitingForPlayerTurn");

    private static readonly MethodInfo EnqueueAction =
        typeof(ActionQueueSynchronizer).GetMethod("EnqueueAction", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingMethodException("ActionQueueSynchronizer.EnqueueAction");

    private static bool Prefix(ActionQueueSynchronizer __instance, GameAction action, INetGameService ____netService)
    {
        if (____netService.Type != NetGameType.Host || PhantomSession.Current?.IsPhantom(action.OwnerId) != true)
        {
            return true;
        }
        if (action.ActionType == GameActionType.CombatPlayPhaseOnly
            && __instance.CombatState == ActionSynchronizerCombatState.NotPlayPhase)
        {
            ((List<GameAction>)DeferredActions.GetValue(__instance)!).Add(action);
            Observability.EventLog.Write("mod", "phantom_action_deferred", new { action = action.GetType().Name, owner = action.OwnerId });
            return false;
        }
        Observability.EventLog.Write("mod", "phantom_action_enqueue", new { action = action.GetType().Name, owner = action.OwnerId });
        EnqueueAction.Invoke(__instance, new object[] { action, action.OwnerId });
        return false;
    }
}
