using MusicBot.Services.Spotify;
using Xunit;
namespace MusicBot.Tests;
public class SpotifyTrackIdentityTests
{
    [Fact] public void ObservedReissueMatchesDespiteDifferentIsrc() => Assert.True(
        new SpotifyTrackIdentity("Renkli Rüyalar Oteli",274226,["40KlwpvpKEQtZTJgbml8lT"],"TR0531305165")
        .IsEquivalentTo(new("Renkli Rüyalar Oteli",273213,["40KlwpvpKEQtZTJgbml8lT"],"TR0530601811")));
    [Theory]
    [InlineData("Song - Live",100000,"artist")]
    [InlineData("Song",103000,"artist")]
    [InlineData("Song",100000,"cover-artist")]
    [InlineData("Song",0,"artist")]
    [InlineData("Song",100000,"")]
    public void DifferentVersionsAndIncompleteMetadataDoNotMatch(string title,int duration,string artist) =>
        Assert.False(new SpotifyTrackIdentity("Song",100000,["artist"],"same").IsEquivalentTo(new(title,duration,[artist],"same")));
}
