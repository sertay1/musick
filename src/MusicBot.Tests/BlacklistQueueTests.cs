using MusicBot.Core.Services;
using MusicBot.Core.Models;
using Xunit;
namespace MusicBot.Tests;
public class BlacklistQueueTests
{
    private static Song Song(string id)=>new(){SpotifyUri=id,Title=id};
    [Fact] public void BanKeepsRowForUndoUntilItsTurnAndDoesNotStopCurrent()
    {
        var blocked=new HashSet<string>();var q=new QueueService(isBlocked:uri=>blocked.Contains(uri??""));
        q.PlayNow(Song("current"),"admin","web");q.AddSong(Song("waiting"),"a","kick");q.AddSong(Song("next"),"b","kick");
        blocked.Add("waiting");blocked.Add("current");
        Assert.Equal(2,q.GetUpcoming().Count);Assert.Equal("current",q.GetCurrentItem()!.Song.SpotifyUri);
        blocked.Remove("waiting");Assert.Equal("waiting",q.Advance()!.Song.SpotifyUri);
    }
    [Fact] public void BlockedQueuedAndCyclicSongsAreSkippedWithoutInfiniteLoop()
    {
        var blocked=new HashSet<string>();var q=new QueueService(isBlocked:uri=>blocked.Contains(uri??""));
        q.AddSong(Song("blocked"),"a","kick");q.AddSong(Song("allowed"),"b","kick");blocked.Add("blocked");
        Assert.Equal("allowed",q.Advance()!.Song.SpotifyUri);
        q.SetBackgroundPlaylist(new[]{Song("blocked")});Assert.Null(q.Advance());
        Assert.Null(q.TryStartFallback(Song("blocked")));
        Assert.Throws<InvalidOperationException>(()=>q.AddSong(Song("blocked"),"a","kick"));
        Assert.Throws<InvalidOperationException>(()=>q.PlayNow(Song("blocked"),"a","web"));
        blocked.Clear();Assert.Equal("blocked",q.Advance()!.Song.SpotifyUri);
    }
}
