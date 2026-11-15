using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MusicBot.Core.Interfaces;
using MusicBot.Core.Models;
using MusicBot.Core.Services;
using MusicBot.Services;
using MusicBot.Services.Player;
using MusicBot.Tests.TestHelpers;
using Xunit;

namespace MusicBot.Tests;

public class SongRequestSelectionTests
{
    [Fact]
    public async Task ChatRequestSearchesMultipleCandidates_AndOnlyQueuesTheRequestedSong()
    {
        var path = Path.Combine(Path.GetTempPath(), "musick-search-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            var builder = WebHost.CreateBuilder([]);
            builder.Configuration["DataDirectory"] = path;
            builder.Configuration["ConnectionStrings:DefaultConnection"] = "Data Source=" + Path.Combine(path, "test.db");
            // Background services are never started. The Spotify proxy rejects any unexpected call.
            await using var app = WebHost.Configure(builder);
            var spotify = DispatchProxy.Create<ISpotifyService, SpotifyProbe>();
            var probe = (SpotifyProbe)(object)spotify;
            probe.Results = [new Song { SpotifyUri = "spotify:track:" + new string('a', 22), Title = "Seni Kendime Sakladım", Artist = "Duman" },
                new Song { SpotifyUri = "spotify:track:" + new string('b', 22), Title = "Senden Daha Güzel", Artist = "Duman" }];
            var queue = new QueueService();
            queue.PlayNow(new Song { SpotifyUri = "spotify:track:" + new string('c', 22), Title = "Current", Artist = "Artist", DurationMs = 200000 }, "Admin", "web");
            using var player = new HybridPlayerService(NullLogger<LocalPlayerService>.Instance, FakeHub.Instance);
            var services = new UserServices(LocalUser.Id, queue, spotify, player);
            var router = app.Services.GetRequiredService<CommandRouterService>();
            var command = new BotCommand { Type = "play", Query = "duman senden daha güzel", RequestedBy = "test", Platform = "kick", Badges = ["broadcaster"] };
            var commandConfig = app.Services.GetRequiredService<MusicBot.Services.Commands.CommandConfigService>();
            var templates = commandConfig.Current.Commands["play"].Templates;
            templates["success"] = "Yeni: {song} #{position} ({wait_time})";
            templates["added"] = "Eski yanıt";
            var result = await router.HandleAsync(command, services);
            Assert.True(result.Success, result.Message);
            Assert.Equal("Yeni: Senden Daha Güzel #2 (4 dk)", result.Message);
            Assert.Equal(10, Assert.Single(probe.Limits));
            Assert.Equal(probe.Results[1].SpotifyUri, Assert.Single(queue.GetUpcoming()).Song.SpotifyUri);
            probe.Results = [probe.Results[0]];
            result = await router.HandleAsync(command, services);
            Assert.False(result.Success);
            Assert.Single(queue.GetUpcoming());
            command.Query = "https://open.spotify.com/track/" + new string('d', 22);
            result = await router.HandleAsync(command, services);
            Assert.True(result.Success, result.Message);
            Assert.Equal(2, probe.Limits.Count); // Exact links bypass text matching entirely.
            Assert.Contains(queue.GetUpcoming(), item => item.Song.SpotifyUri == "spotify:track:" + new string('d', 22));
            command.Query = "https://open.spotify.com/intl-tr/track/" + new string('e',22) + "?si=share";
            templates["success"] = "";
            var silent = await router.HandleAsync(command, services);
            Assert.True(silent.Success);
            Assert.Empty(silent.Message);
            var settings = app.Services.GetRequiredService<QueueSettingsService>();
            Assert.False(settings.ChatPlaylistRequestsEnabled);
            foreach(var link in new[]{"https://open.spotify.com/intl-tr/playlist/test", "https://open.spotify.com/album/test", "https://music.youtube.com/playlist?list=PLtest"})
            {
                command.Query = link;
                var rejected = await router.HandleAsync(command, services);
                Assert.False(rejected.Success);
                Assert.Contains("çalma listesi ve albüm ekleme kapalı", rejected.Message);
            }
            command.RequestedBy = "playlist-test";
            settings.SetChatPlaylistRequests(true);
            command.Query = "https://open.spotify.com/intl-tr/playlist/test";
            Assert.True((await router.HandleAsync(command, services)).Success);
            Assert.Contains(queue.GetUpcoming(), item => item.Song.SpotifyUri == probe.Results[0].SpotifyUri);
            settings.SetChatPlaylistRequests(false);
            Assert.False((await router.HandleAsync(command, services)).Success);
            settings.SetChatPlaylistRequests(true);
            probe.Results = Enumerable.Range(0,90).Select(i=>new Song { SpotifyUri=$"spotify:track:{i:D22}",Title=$"Track {i}",CoverUrl=$"https://images.test/{i}.jpg" }).ToList();
            command.RequestedBy="large-playlist-test";
            var before=queue.GetUpcoming().Count;
            var large=await router.HandleAsync(command,services);
            Assert.True(large.Success,large.Message);
            Assert.Equal(int.MaxValue,probe.CollectionLimit);
            Assert.Equal(90,queue.GetUpcoming().Count-before);
            Assert.Equal(90,queue.GetUpcoming().Where(item=>item.RequestedBy==command.RequestedBy).Select(item=>item.Song.CoverUrl).Distinct().Count());


        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(path, true); }
    }

    public class SpotifyProbe : DispatchProxy
    {
        public List<Song> Results = [];
        public List<int> Limits = [];
        public int CollectionLimit;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "get_IsAuthenticated") return true;
            if (method.Name == nameof(ISpotifyService.SearchAsync)) { Limits.Add((int)args![1]!); return Task.FromResult(Results); }
            if (method.Name == nameof(ISpotifyService.GetPlaylistTracksAsync)) { CollectionLimit=(int)args![1]!; return Task.FromResult(Results); }
            if (method.Name == nameof(ISpotifyService.GetTrackAsync)) return Task.FromResult<Song?>(new Song { SpotifyUri = "spotify:track:" + args![0], Title = "Exact link song", Artist = "Artist" });
            if (method.Name == nameof(ISpotifyService.GetPlaybackStateAsync)) return Task.FromResult<PlaybackState?>(new PlaybackState { TrackUri = "spotify:track:" + new string('c', 22), Title = "Current", Artist = "Artist", DurationMs = 200000, IsPlaying = true });
            throw new InvalidOperationException("Unexpected Spotify call: " + method.Name);
        }
    }
}
