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
    /// minimum). Fabricating a short selection would send a result the game
    /// considers invalid — the policy must refuse, and the driver must skip
    /// fabrication and log instead. (Red-first: current impl under-fills.)
    /// </summary>
    [Fact]
    public void PickIndexes_UnsatisfiableMinimum_Refuses()
    {
        Assert.Null(TrivialPolicy.PickIndexes(optionCount: 0, minSelect: 1, maxSelect: 1, cancelable: false));
        Assert.Null(TrivialPolicy.PickIndexes(optionCount: 1, minSelect: 2, maxSelect: 3, cancelable: false));
    }
}
