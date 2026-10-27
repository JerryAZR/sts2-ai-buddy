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

    [Fact]
    public void IsPhantomRangeId_OnlyThePhantomBand()
    {
        // Resume identifies phantoms by saved net_id alone: the band must cover the
        // allocation range and exclude everything real — ENet test peers count up
        // from 1; SteamID64s are 17 digits (design hard rule: never fabricate for
        // real clients, KitLib's NetId-1001 collision).
        Assert.False(PhantomSession.IsPhantomRangeId(1));   // ENet host
        Assert.False(PhantomSession.IsPhantomRangeId(4));   // ENet guest
        Assert.False(PhantomSession.IsPhantomRangeId(1000));
        Assert.True(PhantomSession.IsPhantomRangeId(PhantomSession.DefaultBaseNetId));
        Assert.True(PhantomSession.IsPhantomRangeId(PhantomSession.DefaultBaseNetId + 3));
        Assert.False(PhantomSession.IsPhantomRangeId(PhantomSession.DefaultBaseNetId + 1000));
        Assert.False(PhantomSession.IsPhantomRangeId(76561198000000000UL)); // SteamID64
    }

    [Fact]
    public void ResumeRegistration_ExplicitIdsStayStableAndAllocationSkipsThem()
    {
        // M6: resume re-registers the ids recorded in the save — identity is stable
        // per save, and any later allocation must not collide with a resumed id.
        PhantomSession session = PhantomSession.StartNewForTest();
        session.RegisterPhantom("Buddy 1001", PhantomSession.DefaultBaseNetId);
        session.RegisterPhantom("Buddy 1002", PhantomSession.DefaultBaseNetId + 1);

        Assert.Equal(PhantomSession.DefaultBaseNetId + 2, session.PeekNextId());
        Assert.Equal(new[] { PhantomSession.DefaultBaseNetId, PhantomSession.DefaultBaseNetId + 1 },
            session.Phantoms.Select(p => p.NetId));
    }
}
