using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MusicBot.Core.Models;
using MusicBot.Data;
using MusicBot.Services;
using Xunit;

namespace MusicBot.Tests;

public sealed class LibraryAndAutoQueueTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly ServiceProvider _provider;
    private readonly AutoQueueService _autoQueue;
    private readonly PlaylistLibraryService _playlists;
    public LibraryAndAutoQueueTests()
    {
        _connection.Open();
        _provider = new ServiceCollection().AddDbContext<MusicBotDbContext>(o => o.UseSqlite(_connection)).BuildServiceProvider();
        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<MusicBotDbContext>().Database.EnsureCreated();
        var factory = _provider.GetRequiredService<IServiceScopeFactory>();
        _autoQueue = new AutoQueueService(factory, NullLogger<AutoQueueService>.Instance);
        _playlists = new PlaylistLibraryService(factory);
    }

    [Fact]
    public async Task AutoQueue_PlaysEntirePoolBeforeRepeating_AndInvalidatesDeletedSongs()
    {
        await _autoQueue.BulkAddAsync(Enumerable.Range(0, 20).Select(i => new Song { SpotifyUri = i.ToString() }));
        var seen = new HashSet<string>();
        for (var i = 0; i < 20; i++) Assert.True(seen.Add((await _autoQueue.GetRandomSongAsync())!.SpotifyUri));
        await _autoQueue.ClearAsync();
        Assert.Null(await _autoQueue.GetRandomSongAsync());
        await _autoQueue.AddAsync(new Song { SpotifyUri = "only" });
        Assert.Equal("only", (await _autoQueue.GetRandomSongAsync())!.SpotifyUri);
    }

    [Fact]
    public async Task AutoQueue_ImportsBeyondOneHundred_WithoutDuplicates()
    {
        var songs = Enumerable.Range(0, 650).Select(i => new Song { SpotifyUri = $"spotify:track:{i:D22}", Title = $"Song {i}" }).ToList();
        Assert.Equal(650, await _autoQueue.BulkAddAsync(songs));
        Assert.Equal(0, await _autoQueue.BulkAddAsync(songs));
        Assert.Equal(650, await _autoQueue.CountAsync());
    }

    [Fact]
    public async Task AutoQueue_DoesNotRepeatImmediatelyWhenAnotherSongExists()
    {
        await _autoQueue.BulkAddAsync(new[] { new Song { SpotifyUri = "a" }, new Song { SpotifyUri = "b" } });
        var previous = await _autoQueue.GetRandomSongAsync();
        for (var i = 0; i < 12; i++)
        {
            var next = await _autoQueue.GetRandomSongAsync();
            Assert.NotEqual(previous!.SpotifyUri, next!.SpotifyUri);
            previous = next;
        }
    }

    [Fact]
    public async Task Playlist_BulkImportPreservesAllSongsOrderAndDeduplicates()
    {
        var playlist = await _playlists.CreateAsync("Large playlist");
        var songs = Enumerable.Range(0, 1200).Select(i => new Song { SpotifyUri = $"youtube:{i}", Title = $"Song {i}" }).ToList();
        Assert.Equal(1200, await _playlists.BulkAddAsync(playlist.Id, songs));
        Assert.Equal(0, await _playlists.BulkAddAsync(playlist.Id, songs));
        var saved = await _playlists.GetSongsAsync(playlist.Id);
        Assert.Equal(songs.Select(s => s.SpotifyUri), saved.Select(s => s.SpotifyUri));
    }

    public void Dispose() { _provider.Dispose(); _connection.Dispose(); }

    [Fact]
    public async Task PoolSkipsBannedDisabledAndCurrentlyPlayingSongs()
    {
        var path = Path.Combine(Path.GetTempPath(), "musick-ban-pool-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var banned = new BannedSongService(path); banned.Ban("spotify:track:banned", "Banned", "");
            var settings = new QueueSettingsService(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), MusicBot.Tests.TestHelpers.FakeHub.Instance);
            settings.SetPlatforms(true, false);
            var pool = new AutoQueueService(_provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AutoQueueService>.Instance, banned, settings);
            await pool.BulkAddAsync(new[] { "spotify:track:banned", "youtube:aaaaaaaaaaa", "spotify:track:current", "spotify:track:allowed" }.Select(uri => new Song { SpotifyUri = uri }));
            Assert.Equal("spotify:track:allowed", (await pool.GetRandomSongAsync(new[] { "spotify:track:current" }))!.SpotifyUri);
            Assert.Null(await pool.GetRandomSongAsync(new[] { "spotify:track:current", "spotify:track:allowed" }));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
