using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicBot.Core.Interfaces;
using MusicBot.Core.Models;
using MusicBot.Services;
using MusicBot.Services.Downloader;
using MusicBot.Tests.TestHelpers;
using Xunit;

namespace MusicBot.Tests;

public class DownloadDeduplicationTests
{
    [Fact]
    public async Task RepeatedBackgroundRequests_ShareOneOperation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MusicBot-download-test-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "local_test.m4a");
        await File.WriteAllBytesAsync(path, new byte[100_001]);
        var library = new BlockingLibrary(path);
        try
        {
            var service = new YtDlpDownloaderService(library,
                Options.Create(new MusicLibrarySettings { LibraryPath = directory, ToolsDirectory = directory }),
                new QueueSettingsService(new ConfigurationBuilder().Build(), FakeHub.Instance),
                null!, NullLogger<YtDlpDownloaderService>.Instance);
            var song = new Song { SpotifyUri = "local:test" };
            for (var i = 0; i < 20; i++) service.StartDownload(song);
            var waiting = service.GetOrStartDownloadAsync(song);
            Assert.Equal(1, library.Calls);
            library.Release.TrySetResult();
            Assert.Equal(path, await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            library.Release.TrySetResult();
            File.Delete(path);
            Directory.Delete(directory);
        }
    }

    private sealed class BlockingLibrary(string path) : ILocalLibraryService
    {
        public int Calls;
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<CachedTrack?> FindByTrackIdAsync(string id)
        {
            Interlocked.Increment(ref Calls);
            await Release.Task;
            return new CachedTrack { TrackId = id, FilePath = path };
        }
        public Task SaveAsync(CachedTrack track) => Task.CompletedTask;
        public Task UpdateDurationAsync(string id, int ms) => Task.CompletedTask;
        public Task UpdateFilePathAsync(string id, string file) => Task.CompletedTask;
        public Task DeleteByTrackIdAsync(string id) => Task.CompletedTask;
    }
}
