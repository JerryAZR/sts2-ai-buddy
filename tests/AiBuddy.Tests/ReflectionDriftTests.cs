using Xunit;

namespace AiBuddy.Tests;

/// <summary>
/// EA drift tripwire: the mod's reflection targets (internal fields/methods the
/// drivers and patches resolve with ?? throw) must resolve against the game
/// assembly the tests reference. Catches a game update renaming a member without
/// launching the game. ModEntry.Initialize itself is in-game only (Harmony, logs).
/// </summary>
public class ReflectionDriftTests
{
    [Fact]
    public void ReflectionTargets_ResolveAgainstCurrentGameAssembly()
    {
        Assert.Null(Record.Exception(ModEntry.ValidateReflectionTargets));
    }
}
