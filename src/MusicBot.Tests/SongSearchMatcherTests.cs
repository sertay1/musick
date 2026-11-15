using MusicBot.Core.Models;
using MusicBot.Services;
using Xunit;

namespace MusicBot.Tests;

public class SongSearchMatcherTests
{
    private static Song Track(string title, string artist = "Duman", string id = "a") => new() { Title = title, Artist = artist, SpotifyUri = "spotify:track:" + id };

    [Fact]
    public void BetterTitleWinsInsteadOfProvidersFirstUnrelatedTrack()
    {
        var desired = Track("Senden Daha Güzel");
        Assert.Same(desired, SongSearchMatcher.Select("senden daha güzel", [Track("Seni Kendime Sakladım"), desired, Track("Senden Daha Güzel Geceler", "Başka sanatçı")]));
    }
    [Theory]
    [InlineData("Duman Senden Daha Güzel")]
    [InlineData("Duman - Senden Daha Güzel")]
    [InlineData("Senden Daha Güzel - Duman")]
    [InlineData("senden daha guzel duman")]
    [InlineData("DUMAN SENDEN DAHA GÜZEL")]
    public void ArtistAndTitleIdentifyTheCorrectRecording(string query)
    {
        var desired = Track("Senden Daha Güzel");
        Assert.Same(desired, SongSearchMatcher.Select(query, [Track("Senden Daha Güzel", "Diğer sanatçı"), desired]));
    }
    [Fact]
    public void TurkishDottedAndDotlessLettersMatch()
    {
        var desired = Track("İzmir'in Işıkları", "Özgür");
        Assert.Same(desired, SongSearchMatcher.Select("OZGUR IZMIRIN ISIKLARI", [Track("Başka"), desired]));
    }
    [Theory]
    [InlineData("Karaoke")]
    [InlineData("Cover")]
    [InlineData("Remix")]
    [InlineData("Instrumental")]
    [InlineData("Slowed Reverb")]
    public void UnrequestedSpecialVersionsAreNotAutoSelected(string version)
    {
        Assert.Null(SongSearchMatcher.Select("Senden Daha Güzel", [Track("Senden Daha Güzel " + version)]));
    }
    [Fact]
    public void ExplicitlyRequestedVersionIsAllowed()
    {
        var remix = Track("Senden Daha Güzel Remix");
        Assert.Same(remix, SongSearchMatcher.Select("senden daha güzel remix", [Track("Senden Daha Güzel"), remix]));
    }
    [Fact]
    public void OriginalIsPreferredOverLiveAndRemaster()
    {
        var desired = Track("Bir Derdim Var", "mor ve ötesi");
        Assert.Same(desired, SongSearchMatcher.Select("bir derdim var", [Track("Bir Derdim Var Live"), Track("Bir Derdim Var Remastered"), desired]));
    }
    [Fact]
    public void ArtistOnlyOrUnrelatedResultsDoNotSilentlyPlayAnotherSong()
    {
        Assert.Null(SongSearchMatcher.Select("Senden Daha Güzel", [Track("Hayatı Yaşa"), Track("Senden Başka")]));
        Assert.Null(SongSearchMatcher.Select("Duman", [Track("Hayatı Yaşa")]));
        Assert.Null(SongSearchMatcher.Select("gül", [Track("Güller")]));
    }
    [Fact]
    public void SingleTypoIsAcceptedButWeakWordOverlapIsNot()
    {
        var desired = Track("Senden Daha Güzel");
        Assert.Same(desired, SongSearchMatcher.Select("sendem daha guzel", [Track("Senin Gibi"), desired]));
    }
    [Fact]
    public void YouTubeTitlesCanIncludeArtistAndOfficialVideoSuffix()
    {
        var desired = Track("Duman - Senden Daha Güzel (Official Video)", "Duman - Topic");
        desired.SpotifyUri = "youtube:abcdefghijk";
        Assert.Same(desired, SongSearchMatcher.Select("senden daha guzel", [Track("Duman - Haberin Yok Mu"), desired]));
    }
    [Theory]
    [InlineData("Dolu Kadehi Ters Tut - Gitme (OfficialAudio)", "netd müzik")]
    [InlineData("Gitme - Dolu Kadehi Ters Tut", "Dolu Kadehi Ters Tut")]
    public void ShortYouTubeTitleIsNotLostAmongArtistAndPublisherWords(string title, string channel)
    {
        var song = Track(title, channel); song.SpotifyUri = "youtube:abcdefghijk";
        Assert.Same(song, SongSearchMatcher.Select("gitme", [song]));
        Assert.Same(song, SongSearchMatcher.Select("dolu kadehi ters tut gitme", [song]));
    }
    [Fact]
    public void ExactTitleContainingDashRemainsValid()
    {
        var desired = Track("Sen - Ben", "Sanatçı");
        Assert.Same(desired, SongSearchMatcher.Select("Sen - Ben", [desired]));
    }
    [Fact]
    public void EmptyAndPlaylistCandidatesAreIgnored()
    {
        Assert.Null(SongSearchMatcher.Select("test", [new Song { Title = "test" }, new Song { Title = "test", SpotifyUri = "playlist", IsPlaylist = true }]));
        Assert.Null(SongSearchMatcher.Select("", [Track("test")]));
    }
}
