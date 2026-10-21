using AiBuddy.Drivers;
using AiBuddy.Phantom;
using Xunit;

namespace AiBuddy.Tests;

/// <summary>
/// Session lifecycle contract (H1): phantoms and all driver state are scoped to a
/// session. State written into one session must be invisible in the next —
/// phantom NetIds are reused across lobbies, so leakage means fabricating with
/// stale data from a previous run.
/// </summary>
public class SessionTests
{
    [Fact]
    public void NewSession_HasNoPhantomStateFromPreviousSession()
    {
        PhantomSession first = PhantomSession.StartNewForTest();
        PhantomDescriptor phantom = first.RegisterPhantom("Buddy A");
        first.PendingChoices[phantom.NetId] = new ChoiceDriver.PendingPrompt(
            ChoiceDriver.PromptKind.Index, new List<MegaCrit.Sts2.Core.Models.CardModel>(), 0, 1, true);
        first.InFlightVotes[phantom.NetId] = new MegaCrit.Sts2.Core.Map.MapCoord(0, 0);

        PhantomSession second = PhantomSession.StartNewForTest();

        Assert.Empty(second.Phantoms);
        Assert.Empty(second.PendingChoices);
        Assert.Empty(second.InFlightVotes);
        Assert.Empty(second.ProceededEvents);
        Assert.False(second.IsPhantom(phantom.NetId));
    }

    [Fact]
    public void RegisterPhantom_AllocatesSequentialIdsFromBase()
    {
        PhantomSession session = PhantomSession.StartNewForTest();
        PhantomDescriptor a = session.RegisterPhantom("A");
        PhantomDescriptor b = session.RegisterPhantom("B");

        Assert.Equal(PhantomSession.DefaultBaseNetId, a.NetId);
        Assert.Equal(PhantomSession.DefaultBaseNetId + 1, b.NetId);
        Assert.True(session.IsPhantom(a.NetId));
        Assert.True(session.IsPhantom(b.NetId));
        Assert.False(session.IsPhantom(PhantomSession.DefaultBaseNetId + 2));
    }

    [Fact]
    public void RegisterPhantom_SameIdTwice_IsRejected()
    {
        PhantomSession session = PhantomSession.StartNewForTest();
        session.RegisterPhantom("A");
        ulong firstId = session.Phantoms[0].NetId;

        // Simulate M6 resume re-registering saved ids, then a stray duplicate.
        Assert.Throws<InvalidOperationException>(() => session.RegisterPhantom("dupe", firstId));
        Assert.Single(session.Phantoms);
    }
}
