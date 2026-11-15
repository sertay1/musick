using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using MusicBot.Services;
using MusicBot.Tests.TestHelpers;
using Xunit;

namespace MusicBot.Tests;

public class DirectSpotifyModeTests
{
    [Fact]
    public async Task QueueSettingsService_PersistsAndBroadcasts_DirectSpotifyMode()
    {
        var inMemory = new Dictionary<string, string?>
        {
            ["Queue:DirectSpotifyMode"] = "false",
            ["Queue:MaxSize"] = "50"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemory).Build();
        var service = new QueueSettingsService(config, FakeHub.Instance);
        Assert.True(service.DirectSpotifyMode);

        await service.UpdateAsync(
            maxQueueSize: 50,
            maxSongsPerUser: 10,
            votingEnabled: false,
            presenceCheckEnabled: false,
            presenceCheckWarningSeconds: 30,
            presenceCheckConfirmSeconds: 30,
            saveDownloads: false,
            autoQueueEnabled: false,
            loudnessNormalizationEnabled: true,
            directSpotifyMode: false);

        Assert.True(service.DirectSpotifyMode);
    }
}
