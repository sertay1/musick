using System.Text.Json;
using MusicBot.Services.Downloader;
using Xunit;
namespace MusicBot.Tests;
public class YouTubeMusicPolicyTests
{
    [Theory]
    [InlineData("{\"categories\":[\"Music\"],\"title\":\"Gangnam Style\"}")]
    [InlineData("{\"categories\":[\"Music\"],\"track\":\"Song\",\"artist\":\"Artist\"}")]
    public void MusicCategoryIsAccepted(string json) { using var doc=JsonDocument.Parse(json); YouTubeMusicPolicy.Validate(doc.RootElement); }
    [Theory]
    [InlineData("{\"track\":\"Song\"}")]
    [InlineData("{\"track\":\"Song\",\"artist\":\"Artist\",\"is_live\":true}")]
    [InlineData("{\"title\":\"Cartel - cartel (Bir Numara En Büyük) Video Clip\",\"categories\":[\"Film & Animation\"]}")]
    [InlineData("null")]
    public void NonMusicAndLiveVideosAreRejected(string json) { using var doc=JsonDocument.Parse(json); Assert.Throws<InvalidOperationException>(()=>YouTubeMusicPolicy.Validate(doc.RootElement)); }
}
