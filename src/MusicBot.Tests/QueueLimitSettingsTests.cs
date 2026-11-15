using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MusicBot.Core.Models;
using MusicBot.Services;
using MusicBot.Tests.TestHelpers;
using Xunit;

namespace MusicBot.Tests;

public class QueueLimitSettingsTests
{
    [Theory]
    [InlineData("MaxQueueSizeEnabled")]
    [InlineData("MaxSizeEnabled")]
    public void DisabledLimits_RemainDisabledWhenUserIsCreated(string sizeKey)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"Queue:{sizeKey}"] = "false", ["Queue:MaxSize"] = "1",
            ["Queue:MaxSongsPerUserEnabled"] = "false", ["Queue:MaxSongsPerUser"] = "1"
        }).Build();
        Assert.False(new QueueSettingsService(config, FakeHub.Instance).MaxQueueSizeEnabled);

        using var provider = new ServiceCollection().AddHttpClient().BuildServiceProvider();
        var users = new UserContextManager(provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IHttpClientFactory>(), NullLoggerFactory.Instance, config, FakeHub.Instance);
        try
        {
            var queue = users.GetOrCreate(LocalUser.Id).Queue;
            queue.AddSong(new Song { SpotifyUri = "first" }, "viewer", "kick");
            queue.AddSong(new Song { SpotifyUri = "second" }, "viewer", "kick");
            Assert.Equal(2, queue.QueueLength);
        }
        finally { users.Remove(LocalUser.Id); }
    }

    [Fact]
    public void CurrentSetting_TakesPrecedenceOverLegacySetting()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Queue:MaxQueueSizeEnabled"] = "false", ["Queue:MaxSizeEnabled"] = "true"
        }).Build();
        Assert.False(new QueueSettingsService(config, FakeHub.Instance).MaxQueueSizeEnabled);
    }
}
