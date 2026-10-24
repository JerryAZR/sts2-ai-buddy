#!/usr/bin/env python3
"""Seed a fresh StS2 headless (`--force-steam off`) profile past first-run onboarding.

Why: the AiBuddy headless harness (`--force-steam off --fastmp ...`) uses the isolated
`default/1` profile, which starts with first-run deviations that change the run the mod
sees. This script edits the profile's `progress.save` so the first headless run matches a
mature profile. Verified against decompiled sts2.dll v0.111.0.

Gotchas addressed (see README "First-run onboarding"):
  1. No Neow        -> reveal NEOW_EPOCH (else ActModel.GetUnlockedAncients removes Neow).
                       All other epochs seeded as not_obtained so timeline slots exist and
                       the game does not log "slot missing but parent NEOW_EPOCH Revealed".
  2. Fixed first-run map -> ActModel.ApplyActDiscoveryOrderModifications forces a curated
                       room order when NumberOfRuns == 0; NumberOfRuns = Wins+Losses, derived
                       from per-character stats. Bump IRONCLAD total_losses to 1.
  3. FTUE popups    -> seed ftue_completed with the standard tutorial FTUE ids.

NOT handled here (one-time, in-game): the mods-warning agreement dialog
(mod_settings.mods_enabled). Click "Load mods" once in a GUI run; the game quits itself
and the flag persists. After that this script's profile will load mods headless.

Idempotent. Backs up to progress.save.bak before writing.
"""
import json, os, shutil, sys, time

ROOT = os.path.expanduser("~/.local/share/SlayTheSpire2/default/1")
TARGETS = [
    os.path.join(ROOT, "profile1/saves/progress.save"),
    os.path.join(ROOT, "modded/profile1/saves/progress.save"),
]

EPOCH_IDS = [
    "NEOW_EPOCH", "COLORLESS1_EPOCH", "COLORLESS2_EPOCH", "COLORLESS3_EPOCH",
    "COLORLESS4_EPOCH", "COLORLESS5_EPOCH", "RELIC1_EPOCH", "RELIC2_EPOCH",
    "RELIC3_EPOCH", "RELIC4_EPOCH", "RELIC5_EPOCH", "POTION1_EPOCH", "POTION2_EPOCH",
    "EVENT1_EPOCH", "EVENT2_EPOCH", "EVENT3_EPOCH", "IRONCLAD2_EPOCH", "IRONCLAD3_EPOCH",
    "IRONCLAD4_EPOCH", "IRONCLAD5_EPOCH", "IRONCLAD6_EPOCH", "IRONCLAD7_EPOCH",
    "SILENT1_EPOCH", "SILENT2_EPOCH", "SILENT3_EPOCH", "SILENT4_EPOCH", "SILENT5_EPOCH",
    "SILENT6_EPOCH", "SILENT7_EPOCH", "REGENT1_EPOCH", "REGENT2_EPOCH", "REGENT3_EPOCH",
    "REGENT4_EPOCH", "REGENT5_EPOCH", "REGENT6_EPOCH", "REGENT7_EPOCH", "DEFECT1_EPOCH",
    "DEFECT2_EPOCH", "DEFECT3_EPOCH", "DEFECT4_EPOCH", "DEFECT5_EPOCH", "DEFECT6_EPOCH",
    "DEFECT7_EPOCH", "NECROBINDER1_EPOCH", "NECROBINDER2_EPOCH", "NECROBINDER3_EPOCH",
    "NECROBINDER4_EPOCH", "NECROBINDER5_EPOCH", "NECROBINDER6_EPOCH", "NECROBINDER7_EPOCH",
    "OROBAS_EPOCH", "DARV_EPOCH", "ACT2_B_EPOCH", "ACT3_B_EPOCH", "UNDERDOCKS_EPOCH",
    "CUSTOM_AND_SEEDS_EPOCH", "DAILY_RUN_EPOCH",
]

FTUE_COMPLETED = [
    "accept_tutorials_ftue", "map_select_ftue", "combat_rules_ftue", "shuffle_ftue",
    "cannot_play_card_ftue", "can_play_cards_ftue", "power_card_ftue", "obtain_potion_ftue",
    "combat_reward_ftue", "rest_site_ftue", "obtain_relic_ftue", "merchant_ftue",
    "ascension_multiplayer_ftue", "ascension_singleplayer_ftue",
]


def seed(path: str) -> str:
    if not os.path.exists(path):
        return f"skip (missing): {path}"
    d = json.load(open(path))
    shutil.copy(path, path + ".bak")

    present = {e.get("id") for e in d["epochs"]}
    now = int(time.time())
    for eid in EPOCH_IDS:
        if eid not in present:
            state = "revealed" if eid == "NEOW_EPOCH" else "not_obtained"
            d["epochs"].append({"id": eid, "obtain_date": now, "state": state})

    char = next((c for c in d["character_stats"] if c.get("id") == "CHARACTER.IRONCLAD"), None)
    if char is None:
        char = {"id": "CHARACTER.IRONCLAD", "badges": [], "best_win_streak": 0,
                "current_streak": 0, "fastest_win_time": 0, "max_ascension": 0,
                "playtime": 0, "preferred_ascension": 0, "total_losses": 0, "total_wins": 0}
        d["character_stats"].append(char)
    if char.get("total_wins", 0) + char.get("total_losses", 0) == 0:
        char["total_losses"] = 1

    # Union (not replace): the game appends FTUEs during play, so a non-empty list
    # doesn't mean all first-run FTUEs are present.
    merged = list(dict.fromkeys(list(d.get("ftue_completed", [])) + FTUE_COMPLETED))
    d["ftue_completed"] = merged

    json.dump(d, open(path, "w"))
    runs = char.get("total_wins", 0) + char.get("total_losses", 0)
    return f"seeded: {path}  (epochs={len(d['epochs'])}, ironclad runs={runs}, ftues={len(d['ftue_completed'])})"


for t in TARGETS:
    print(seed(t))
