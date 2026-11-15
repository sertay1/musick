using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using MusicBot.Services.Platforms;
using Xunit;

namespace MusicBot.Tests;

public class KickChatSpyReaderLiveTests
{
    [Fact]
    public async Task Reader_ConnectsToKickChannel_AndSetsIsConnected()
    {
        var reader = new KickChatSpyReader(NullLogger<KickChatSpyReader>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        try
        {
            await reader.ConnectAsync("sertay", cts.Token);
            Assert.True(reader.IsConnected, "reader.IsConnected should be true after successful connect");

            // Wait 2 seconds to ensure stability
            await Task.Delay(2000, cts.Token);
            Assert.True(reader.IsConnected, "reader.IsConnected should remain true");

            await reader.DisconnectAsync();
            Assert.False(reader.IsConnected, "reader.IsConnected should be false after disconnect");
        }
        finally
        {
            await reader.DisposeAsync();
        }
    }
}
