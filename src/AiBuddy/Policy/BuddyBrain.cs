using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Map;

namespace AiBuddy.Policy;

/// <summary>
/// The brain contract (design §3.5). Pull-based: when the harness needs a decision,
/// it builds a detached snapshot VIEW (plain records, deep copies, zero engine
/// references — whatever the brain does to it cannot touch the game) and calls the
/// brain; the brain returns an intent expressed in ids/indexes; the control layer
/// resolves ids → engine objects and validates every intent against the legal set
/// before actuating. The brain makes decisions based on the current state — that
/// is all it can do.
///
/// The view's breadth follows the game's own save/sync schema (run: map, position,
/// visited coords, per-player decks/relics/hp/gold), not ad-hoc curation. Built at
/// decision points only (turn start, batch settle, event presented) — never per tick.
/// </summary>

// ===== Intents (brain → control layer; ids only) =====

public abstract record BuddyIntent;

/// <summary>Play these cards, in order. Kill-chain: the first rejected play aborts the rest of the batch.</summary>
public sealed record PlayBatch(IReadOnlyList<PlannedPlay> Plays) : BuddyIntent;

/// <summary>The brain is done this turn. Turn-end is the brain's decision, never the driver's.</summary>
public sealed record EndTurnIntent : BuddyIntent
{
    public static readonly EndTurnIntent Instance = new();
}

/// <summary>One intended play: hand index + card id (cross-checked on resolve) + optional target creature id.</summary>
public sealed record PlannedPlay(int HandIndex, string CardId, uint? TargetCombatId);

// ===== Feedback (control layer → brain) =====

/// <summary>What happened to one executed play.</summary>
public sealed record PlayOutcome(string CardId, bool Success, string? RejectionReason);

/// <summary>One removable-card option for the card-removal brain decision.</summary>
public sealed record RemovalOption(string CardId, bool IsCurseOrStatus);

/// <summary>One rest-site option for the rest-site brain decision. NeedsAllyTarget: options whose local flow opens ally targeting (Mend) — the trivial brain never picks them.</summary>
public sealed record RestSiteOptionView(int Index, string OptionId, bool NeedsAllyTarget);

/// <summary>One shop purchase candidate for the shop brain decision. Kind: "relic3" | "colorless" | "potion" | "removal".</summary>
public sealed record ShopCandidate(string Kind, int Cost);

// ===== Views (control layer → brain; detached data) =====

/// <summary>One player's run-level state (save-schema coverage).</summary>
public sealed record PlayerRunView(ulong NetId, string CharacterId, int Hp, int MaxHp, int Gold, int MaxEnergy,
    IReadOnlyList<string> Deck, IReadOnlyList<string> Relics, IReadOnlyList<string> Potions);

public sealed record MapPointView(MapCoord Coord, MapPointType Type, IReadOnlyList<MapCoord> Children);

/// <summary>Whole-run state: act map, position, visited coords, all players.</summary>
public sealed record RunView(int ActIndex, MapCoord? CurrentCoord, IReadOnlySet<MapCoord> VisitedCoords,
    IReadOnlyList<MapPointView> Map, IReadOnlyList<PlayerRunView> Players);

public sealed record CreatureView(uint? CombatId, bool IsPlayer, ulong? OwnerNetId,
    int Hp, int MaxHp, int Block, bool IsDead, bool Hittable, bool IntendsToAttack);

/// <summary>One hand card with legality precomputed by the control layer — the brain picks, it doesn't evaluate rules.</summary>
public sealed record CombatCardView(int HandIndex, string CardId, int Cost, bool CostsX,
    bool IntrinsicallyPlayable, TargetType TargetKind, IReadOnlyList<uint?> LegalTargetIds);

/// <summary>Combat decision view. RejectedCardIds: cards the engine refused this turn — re-proposing one is a brain contract violation.</summary>
public sealed record CombatView(int Energy, ulong SelfNetId, IReadOnlyList<CombatCardView> Hand,
    IReadOnlyList<CreatureView> Creatures, IReadOnlySet<string> RejectedCardIds, IReadOnlyList<PlayOutcome> LastOutcomes);

public sealed record EventOptionView(int Index, bool Locked, bool WillKill, string? RelicId);

public sealed record EventView(string EventId, IReadOnlyList<EventOptionView> Options);

/// <summary>Map vote view. MirrorHostVote: phantoms mirror the host's vote (locked design decision).</summary>
public sealed record MapPickView(bool MirrorHostVote, MapCoord? HostVote, IReadOnlyList<MapCoord> LegalDestinations);
