using AiBuddy.Phantom;
using AiBuddy.Policy;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace AiBuddy.Drivers;

/// <summary>
/// Debug-only consequence test (AIBUDDY_TEST_COMBAT_CHOICE=1): injects Hologram —
/// a card whose OnPlay opens a combat-pile choice — into the first phantom's hand at
/// the first combat, then asserts the play COMPLETES (its choice pauses the action,
/// and without ChoiceResumePatch the queue softlocks — observed as the 150s
/// Hologram/NeowsFury hangs). PASS = a successful play_outcome for the card.
///
/// Loopback-only caveat: injects directly into the combat draw pile (no command,
/// no queue) — fine with no guests, would be a determinism violation on a live session.
/// </summary>
public static class CombatChoiceTestDriver
{
    private const string TestCardTypeName = "Hologram";
    private const double ResultTimeoutSeconds = 60;

    private static bool _injected;
    private static bool _reported;
    private static DateTime _injectedAt;

    static CombatChoiceTestDriver()
    {
        CombatDriver.PlayOutcomeObserved += OnPlayOutcome;
    }

    public static void Tick(RunState state)
    {
        if (_reported || System.Environment.GetEnvironmentVariable("AIBUDDY_TEST_COMBAT_CHOICE") != "1")
        {
            return;
        }
        if (_injected)
        {
            // Report failure if the injected play never resolves.
            if ((DateTime.UtcNow - _injectedAt).TotalSeconds > ResultTimeoutSeconds)
            {
                Report(false, "timeout waiting for the injected card's play to resolve");
            }
            return;
        }
        // Inject straight into the combat hand (no command, no queue pollution — see
        // the CardPileCmd.Add hang note). PlayPhase ticks are reliably observed.
        if (RunManager.Instance.ActionQueueSynchronizer.CombatState != MegaCrit.Sts2.Core.Entities.Multiplayer.ActionSynchronizerCombatState.PlayPhase)
        {
            return;
        }
        Player? phantom = PhantomSession.Current?.Phantoms is { Count: > 0 } phantoms
            ? state.GetPlayer(phantoms[0].NetId)
            : null;
        if (phantom == null)
        {
            return;
        }
        CardModel? canonical = ModelDb.AllCards.FirstOrDefault(c => c.GetType().Name == TestCardTypeName);
        if (canonical == null)
        {
            Report(false, $"card {TestCardTypeName} not found in ModelDb");
            return;
        }
        // ToMutable (no owner yet) → CombatState.AddCard assigns owner + registers;
        // CreateCard would assign the owner first and AddCard would throw ("already has an owner").
        CardModel card = canonical.ToMutable();
        card.AfterCreated();
        phantom.Creature.CombatState?.AddCard(card, phantom);
        _injected = true;
        _injectedAt = DateTime.UtcNow;
        Observability.EventLog.Write("test", "test_combat_choice_injected", new { netId = phantom.NetId, card = TestCardTypeName });
        PileType.Hand.GetPile(phantom).AddInternal(card, index: 0); // first in hand → the greedy plan always includes it
    }

    private static void OnPlayOutcome(ulong netId, PlayOutcome outcome)
    {
        if (_injected && !_reported && outcome.CardId.Contains(TestCardTypeName.ToUpperInvariant()))
        {
            Report(outcome.Success, outcome.Success ? null : $"play rejected: {outcome.RejectionReason}");
        }
    }

    private static void Report(bool pass, string? error)
    {
        _reported = true;
        Observability.EventLog.Write("test", "test_combat_choice", new { verdict = pass ? "PASS" : "FAIL", error });
        Log.Info($"[AiBuddy] CombatChoiceTest {(pass ? "PASS" : "FAIL")}{(error != null ? $": {error}" : "")}");
    }
}
