using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MusicBot.Core.Models;
using MusicBot.Data;
using MusicBot.Services;
using MusicBot.Services.Library;
using MusicBot.Tests.TestHelpers;
using Xunit;

namespace MusicBot.Tests;

public class QueuePersistenceTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly ServiceProvider _provider;
    private readonly UserContextManager _users;
    private readonly QueuePersistenceService _persistence;

    public QueuePersistenceTests()
    {
        _connection.Open();
        _provider = new ServiceCollection()
            .AddDbContext<MusicBotDbContext>(o => o.UseSqlite(_connection))
            .AddHttpClient().BuildServiceProvider();
        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<MusicBotDbContext>().Database.EnsureCreated();
        var factory = _provider.GetRequiredService<IServiceScopeFactory>();
        _users = new UserContextManager(factory, _provider.GetRequiredService<IHttpClientFactory>(),
            NullLoggerFactory.Instance, new ConfigurationBuilder().Build(), FakeHub.Instance);
        _persistence = new QueuePersistenceService(_users,
            new LocalLibraryService(factory, NullLogger<LocalLibraryService>.Instance),
            new PlaylistLibraryService(factory), factory, NullLogger<QueuePersistenceService>.Instance);
    }

    [Fact]
    public async Task RestoredQueueDoesNotRetainObsoletePreparationLocks()
    {
        var user = _users.GetOrCreate(LocalUser.Id);
        await _persistence.StartAsync(default);
        var item = user.Queue.AddSong(new() { SpotifyUri = "spotify:track:prepared" }, "viewer", "kick");
        Assert.False(user.Queue.TryReserveNextTrack(item.Id));
        await _persistence.StopAsync(default);
        using var scope = _provider.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<MusicBotDbContext>().PersistedQueueItems.SingleAsync();
        Assert.False(row.PreparedForSpotify);
        var restored = (QueueItem)await (Task<QueueItem>)typeof(QueuePersistenceService).GetMethod("ToQueueItemAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(_persistence, new object[] { row })!;
        Assert.False(restored.PreparedForSpotify);
    }

    [Fact]
    public async Task ExistingUser_QueueChangesArePersistedBeforeStopReturns()
    {
        var user = _users.GetOrCreate(LocalUser.Id);
        await _persistence.StartAsync(default);
        user.Queue.AddSong(new Song { SpotifyUri = "youtube:first", Title = "First" }, "viewer", "kick");
        await _persistence.StopAsync(default);

        using var scope = _provider.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<MusicBotDbContext>().PersistedQueueItems.ToListAsync();
        Assert.Equal("youtube:first", Assert.Single(rows).TrackId);
    }

    [Fact]
    public async Task StartupRestoresBeforeReturning_AndShutdownPreservesQueue()
    {
        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
            db.PersistedQueueItems.Add(new PersistedQueueItem
            {
                Id = Guid.NewGuid(), UserId = LocalUser.Id, Position = 1,
                TrackId = "youtube:saved", Title = "Saved", RequestedBy = "viewer", Platform = "kick"
            });
            await db.SaveChangesAsync();
        }

        await _persistence.StartAsync(default);
        Assert.Equal("youtube:saved", Assert.Single(_users.GetOrCreate(LocalUser.Id).Queue.GetUpcoming()).Song.SpotifyUri);
        await _persistence.StopAsync(default);
        using var check = _provider.CreateScope();
        Assert.Equal("youtube:saved", (await check.ServiceProvider.GetRequiredService<MusicBotDbContext>()
            .PersistedQueueItems.SingleAsync()).TrackId);
    }

    [Fact]
    public async Task BurstOfChanges_PersistsFinalOrderOnShutdown()
    {
        await _persistence.StartAsync(default);
        var queue = _users.GetOrCreate(LocalUser.Id).Queue;
        for (var i = 0; i < 30; i++)
            queue.AddSong(new Song { SpotifyUri = $"youtube:{i}" }, "viewer", "kick", bypassUserLimit: true);
        queue.RemoveByUri("youtube:0");
        queue.Reorder("youtube:29", 0);
        await _persistence.StopAsync(default);

        using var scope = _provider.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<MusicBotDbContext>().PersistedQueueItems
            .OrderBy(r => r.Position).ToListAsync();
        Assert.Equal(queue.GetUpcoming().Select(i => i.Song.SpotifyUri), rows.Select(r => r.TrackId));
    }

    public void Dispose()
    {
        _users.Remove(LocalUser.Id);
        _provider.Dispose();
        _connection.Dispose();
    }
}
