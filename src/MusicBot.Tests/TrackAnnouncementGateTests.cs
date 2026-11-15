using MusicBot.Services;
using Xunit;

namespace MusicBot.Tests;

public class TrackAnnouncementGateTests
{
    [Fact]
    public void TemporaryAlbumTrack_IsNotAnnouncedBeforeRequestedSong()
    {
        var gate = new TrackAnnouncementGate();
        Assert.False(gate.TryAccept("current", false));
        Assert.True(gate.TryAccept("current", false));
        Assert.False(gate.TryAccept("album-next", false));
        gate.ResetCandidate();
        Assert.False(gate.TryAccept("requested", false));
        Assert.True(gate.TryAccept("requested", false));
        Assert.False(gate.TryAccept("requested", false));
    }

    [Fact]
    public void StaleOldTrack_BetweenNewTrackObservations_DoesNotCreateAnotherAnnouncement()
    {
        var gate = new TrackAnnouncementGate();
        Assert.True(gate.TryAccept("new", true));
        Assert.False(gate.TryAccept("old", false));
        Assert.False(gate.TryAccept("new", false));
        Assert.False(gate.TryAccept("new", false));
    }

    [Fact]
    public void ConcurrentConfirmedCallbacks_AnnounceOnlyOnce()
    {
        var gate = new TrackAnnouncementGate();
        var accepted = 0;
        Parallel.For(0, 20, _ => { if (gate.TryAccept("song", true)) Interlocked.Increment(ref accepted); });
        Assert.Equal(1, accepted);
    }

    [Fact]
    public void ReturningToSongAfterAnotherSong_CanAnnounceItAgain()
    {
        var gate = new TrackAnnouncementGate();
        Assert.True(gate.TryAccept("a", true));
        Assert.True(gate.TryAccept("b", true));
        Assert.True(gate.TryAccept("a", true));
    }
}
