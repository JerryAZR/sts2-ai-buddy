using AiBuddy.Policy;
using Xunit;

namespace AiBuddy.Tests;

public class PolicyTests
{
    [Fact]
    public void PickIndexes_RequiredSingleChoice_TakesFirst()
    {
        // e.g. mandatory card removal: exactly one of five.
        Assert.Equal(new List<int> { 0 }, TrivialPolicy.PickIndexes(optionCount: 5, minSelect: 1, maxSelect: 1, cancelable: false));
    }

    [Fact]
    public void PickIndexes_CancelableWithNoMinimum_TakesNothing()
    {
        // e.g. optional card pick: skipping is always mechanically safe.
        Assert.Empty(TrivialPolicy.PickIndexes(optionCount: 3, minSelect: 0, maxSelect: 1, cancelable: true));
    }

    [Fact]
    public void PickIndexes_MultiSelect_RespectsMinimum()
    {
        Assert.Equal(new List<int> { 0, 1 }, TrivialPolicy.PickIndexes(optionCount: 4, minSelect: 2, maxSelect: 2, cancelable: false));
    }

    /// <summary>
    /// The prompt's constraints can never be satisfied (fewer options than the
    /// minimum, or a max below the min). Fabricating a short selection would send a
    /// result the game considers invalid — the policy must refuse, and the driver
    /// must skip fabrication and log instead.
    /// </summary>
    [Fact]
    public void PickIndexes_UnsatisfiableConstraints_Refuse()
    {
        Assert.Null(TrivialPolicy.PickIndexes(optionCount: 0, minSelect: 1, maxSelect: 1, cancelable: false));
        Assert.Null(TrivialPolicy.PickIndexes(optionCount: 1, minSelect: 2, maxSelect: 3, cancelable: false));
        Assert.Null(TrivialPolicy.PickIndexes(optionCount: 5, minSelect: 3, maxSelect: 1, cancelable: false));
    }

    /// <summary>
    /// Contract matrix: whatever the policy returns, it must be a selection the
    /// game accepts — count within [min, min(max, options)], distinct indexes in
    /// range — or null (refusal). Never throws. Pins intent, not implementation.
    /// </summary>
    [Fact]
    public void PickIndexes_NeverReturnsAnInvalidSelection()
    {
        for (int optionCount = 0; optionCount <= 6; optionCount++)
        for (int min = 0; min <= 3; min++)
        for (int max = 0; max <= 4; max++)
        foreach (bool cancelable in new[] { false, true })
        {
            List<int>? result = TrivialPolicy.PickIndexes(optionCount, min, max, cancelable);
            if (result == null)
            {
                Assert.True(optionCount < min || max < min,
                    $"refused a satisfiable prompt: options={optionCount} min={min} max={max} cancelable={cancelable}");
                continue;
            }
            Assert.InRange(result.Count, min, Math.Min(max, optionCount));
            Assert.Equal(result.Count, result.Distinct().Count());
            Assert.All(result, i => Assert.InRange(i, 0, optionCount - 1));
        }
    }

    [Fact]
    public void PickIndex_AlwaysTakesFirst()
    {
        Assert.Equal(0, TrivialPolicy.PickIndex(1));
        Assert.Equal(0, TrivialPolicy.PickIndex(42));
    }
}
