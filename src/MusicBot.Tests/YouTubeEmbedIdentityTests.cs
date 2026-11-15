using MusicBot.Services.Player;
using Xunit;
namespace MusicBot.Tests;
public class YouTubeEmbedIdentityTests
{
    [Theory]
    [InlineData("https://www.youtube.com/embed/9bZkp7q19f0?enablejsapi=1",true)]
    [InlineData("https://www.youtube-nocookie.com/embed/9bZkp7q19f0",true)]
    [InlineData("https://www.youtube.com/youtubei/v1/player",false)]
    [InlineData("https://www.youtube.com/watch?v=9bZkp7q19f0",false)]
    [InlineData("https://www.youtube.com.evil.test/embed/9bZkp7q19f0",false)]
    [InlineData("http://www.youtube.com/embed/9bZkp7q19f0",false)]
    [InlineData("https://evil.test/?next=https://www.youtube.com/embed/x",false)]
    public void AppIdentityOnlyAppliesToActualYouTubeEmbedDocuments(string url,bool expected)
    { Assert.Equal(expected,YouTubeEmbedIdentity.IsEmbedDocument(url));Assert.Equal("https",new Uri(YouTubeEmbedIdentity.Referrer).Scheme); }
}
