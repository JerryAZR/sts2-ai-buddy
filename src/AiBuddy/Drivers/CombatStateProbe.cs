using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;

namespace AiBuddy.Drivers;

/// <summary>
/// Reflection access to CombatManager's internal CombatTurnState (the type is
/// internal; its members are public). Single point of access so EA drift has
/// exactly one place to fail, validated eagerly at mod load.
/// </summary>
public static class CombatStateProbe
{
    private static readonly FieldInfo TurnStateField =
        typeof(CombatManager).GetField("_turnState", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException("CombatManager._turnState");

    private static readonly PropertyInfo IsInProgressProp;
    private static readonly PropertyInfo ReadyToEndTurnProp;
    private static readonly PropertyInfo ReadyToBeginEnemyTurnProp;

    static CombatStateProbe()
    {
        Type turnStateType = TurnStateField.FieldType;
        IsInProgressProp = turnStateType.GetProperty("IsInProgress")
            ?? throw new MissingMemberException("CombatTurnState.IsInProgress");
        ReadyToEndTurnProp = turnStateType.GetProperty("PlayersReadyToEndTurn")
            ?? throw new MissingMemberException("CombatTurnState.PlayersReadyToEndTurn");
        ReadyToBeginEnemyTurnProp = turnStateType.GetProperty("PlayersReadyToBeginEnemyTurn")
            ?? throw new MissingMemberException("CombatTurnState.PlayersReadyToBeginEnemyTurn");
    }

    /// <summary>The live turn state, or null when no combat turn is running.</summary>
    public static object? GetTurnState()
        => TurnStateField.GetValue(CombatManager.Instance);

    public static bool IsInProgress(object turnState)
        => (bool)IsInProgressProp.GetValue(turnState)!;

    public static HashSet<Player> PlayersReadyToEndTurn(object turnState)
        => (HashSet<Player>)ReadyToEndTurnProp.GetValue(turnState)!;

    public static HashSet<Player> PlayersReadyToBeginEnemyTurn(object turnState)
        => (HashSet<Player>)ReadyToBeginEnemyTurnProp.GetValue(turnState)!;
}
