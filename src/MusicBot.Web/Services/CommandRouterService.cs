using System.IO;
using System.Web;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MusicBot.Core.Interfaces;
using MusicBot.Core.Models;
using MusicBot.Core.Services;
using MusicBot.Hubs;
using MusicBot.Services.Commands;

namespace MusicBot.Services;

public class CommandRouterService
{
    private readonly ILogger<CommandRouterService> _logger;
    private readonly PlaybackSyncService           _sync;
    private readonly IMetadataService              _metadata;
    private readonly ILocalLibraryService          _library;
    private readonly Downloader.YtDlpDownloaderService _downloader;
    private readonly KickVoteService               _kickVote;
    private readonly BannedSongService             _banned;
    private readonly PresenceCheckService          _presence;
    private readonly AutoQueueService              _autoQueue;
    private readonly CommandConfigService          _cmdConfig;
    private readonly QueueSettingsService          _queueSettings;
    private readonly IServiceScopeFactory          _scopeFactory;
    private readonly IHubContext<OverlayHub>       _hub;

    public CommandRouterService(
        ILogger<CommandRouterService> logger,
        PlaybackSyncService sync,
        IMetadataService metadata,
        ILocalLibraryService library,
        Downloader.YtDlpDownloaderService downloader,
        KickVoteService kickVote,
        BannedSongService banned,
        PresenceCheckService presence,
        AutoQueueService autoQueue,
        CommandConfigService cmdConfig,
        QueueSettingsService queueSettings,
        IServiceScopeFactory scopeFactory,
        IHubContext<OverlayHub> hub)
    {
        _logger        = logger;
        _sync          = sync;
        _metadata      = metadata;
        _library       = library;
        _downloader    = downloader;
        _kickVote      = kickVote;
        _banned        = banned;
        _presence      = presence;
        _autoQueue     = autoQueue;
        _cmdConfig     = cmdConfig;
        _queueSettings = queueSettings;
        _scopeFactory  = scopeFactory;
        _hub           = hub;
    }

    public async Task<CommandResult> HandleAsync(BotCommand command, UserServices services)
    {
        try
        {
            var cmdDef = command.Definition
                ?? _cmdConfig.Current.Commands.GetValueOrDefault(command.Type)
                ?? new CommandDefinition { Name = command.Type };

            var result = (command.Type.ToLowerInvariant() switch
            {
                "play" or "sr" or "istek"
                    => await HandlePlay(command, services, cmdDef),
                "skip" or "atla" or "gec"
                    => await HandleSkip(command, services, cmdDef),
                "selfskip"
                    => await HandleUserSkip(command, services, cmdDef),
                "sil" or "revoke" or "quitar" or "iptal"
                    => HandleRevoke(command, services, cmdDef),
                "simdi" or "song" or "cancion" or "current" or "calan"
                    => HandleSong(command, services, cmdDef),
                "like" or "love" or "sevdim"
                    => await HandleLike(command, services, cmdDef),
                "sira" or "queue" or "cola" or "liste"
                    => HandleQueue(command, services, cmdDef),
                "pos" or "position" or "konum"
                    => HandlePos(command, services, cmdDef),
                "gecmis" or "history" or "historial"
                    => await HandleHistory(command, services, cmdDef),
                "info" or "bilgi"
                    => HandleInfo(command, services, cmdDef),
                "burada" or "aqui" or "here"
                    => HandleAqui(command, cmdDef),
                "bump"
                    => HandleBump(command, services, cmdDef),
                "vote_yes" or "si" or "yes" or "evet"
                    => await HandleVote(command, true, cmdDef),
                "vote_no" or "no" or "hayir"
                    => await HandleVote(command, false, cmdDef),
                "keep" or "tut"
                    => HandleKeep(command, cmdDef),
                "ses" or "volume" or "vol"
                    => await HandleVolume(command, services, cmdDef),
                _ => _cmdConfig.TryResolveCustomCommand(command.Type, out var customDef)
                    ? await HandleCustomCommand(command, services, customDef!)
                    : CommandResult.Fail($"Bilinmeyen komut: {command.Type}")
            }) ?? CommandResult.Fail("Komut işlenemedi.");
            ActivityJournal.Record(command.Platform == "kick" ? "kick" : "musick", command.Type, command.RequestedBy, result.Message, result.Success);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling command {Type}", command.Type);
            await BroadcastErrorAsync(ex.Message);
            ActivityJournal.Record(command.Platform, command.Type, command.RequestedBy, ex.Message, false);
            var definition = command.Definition ?? _cmdConfig.Current.Commands.GetValueOrDefault(command.Type);
            return Failure(definition, "service_error", command, ex.Message);
        }
    }

    // ── Handlers ─────────────────────────────────────────────────────────────

    private async Task<CommandResult> HandlePlay(BotCommand command, UserServices services, CommandDefinition cmdDef)
    {
        if (string.IsNullOrWhiteSpace(command.Query))
        {
            var template = cmdDef.GetTemplate("empty_query", "@{user}, lütfen bir şarkı adı veya link belirtin. Örn: !istek sanatçı şarkı");
            var msg = CommandTemplateEngine.Render(template, new TemplateContext { User = command.RequestedBy });
            await BroadcastErrorAsync(msg);
            return CommandResult.Fail(msg);
        }

        var youtubePlaylist = YouTubeLinks.TryPlaylistId(command.Query, out _) && !TryExtractYouTubeVideoId(command.Query, out _);
        var collection = youtubePlaylist || TryExtractSpotifyPlaylistId(command.Query, out _) || TryExtractSpotifyAlbumId(command.Query, out _);
        if (collection && !_queueSettings.ChatPlaylistRequestsEnabled && (!youtubePlaylist || _queueSettings.YouTubeEnabled))
            return Failure(cmdDef, "playlist_disabled", command, "Sohbetten çalma listesi ve albüm ekleme kapalı. Tek bir şarkının bağlantısını gönderin.");

        if (YouTubeLinks.IsYouTube(command.Query))
        {
            if (!_queueSettings.YouTubeEnabled) return Failure(cmdDef, "youtube_disabled", command, "YouTube istekleri ayarlardan kapatılmış.");
            if (!youtubePlaylist && !TryExtractYouTubeVideoId(command.Query, out _)) return Failure(cmdDef, "invalid_link", command, "Geçerli bir YouTube bağlantısı girin.");
        }

        var roleProfile = _cmdConfig.GetRoleProfile(command.Badges, command.RequestedBy, command.Channel);
        if (cmdDef.AllowedRoles == null && !roleProfile.CanRequestSongs)
        {
            var msg = CommandTemplateEngine.Render(cmdDef.GetTemplate("permission_denied"), new TemplateContext { User = command.RequestedBy });
            await BroadcastErrorAsync(msg);
            return CommandResult.Fail(msg);
        }

        if (!roleProfile.BypassLimits)
        {
            if (_queueSettings.MaxQueueSizeEnabled && _queueSettings.MaxQueueSize > 0 && services.Queue.QueueLength >= _queueSettings.MaxQueueSize)
            {
                var msg = Failure(cmdDef, "queue_full", command, $"@{command.RequestedBy}, şarkı kuyruğu şu anda dolu (maksimum {_queueSettings.MaxQueueSize} şarkı).").Message;
                await BroadcastErrorAsync(msg);
                return CommandResult.Fail(msg);
            }

            int userLimit = roleProfile.MaxSongsInQueue > 0
                ? roleProfile.MaxSongsInQueue
                : (_queueSettings.MaxSongsPerUserEnabled ? _queueSettings.MaxSongsPerUser : 0);

            if (_queueSettings.MaxSongsPerUserEnabled && userLimit > 0)
            {
                var userSongs = services.Queue.GetUpcoming().Count(i => i.RequestedBy.Equals(command.RequestedBy, StringComparison.OrdinalIgnoreCase));
                if (userSongs >= userLimit)
                {
                    var msg = Failure(cmdDef, "user_limit", command, $"@{command.RequestedBy}, kuyrukta zaten {userSongs} şarkınız bulunuyor (maksimum {userLimit}).").Message;
                    await BroadcastErrorAsync(msg);
                    return CommandResult.Fail(msg);
                }
            }
        }

        if (youtubePlaylist)
        {
            var imported = await _downloader.ImportPlaylistAsync(command.Query, int.MaxValue);
            var count = 0;
            foreach (var track in imported.Songs)
            {
                if (_banned.IsBanned(track.SpotifyUri)) continue;
                try
                {
                    await _downloader.ValidateMusicVideoAsync(track.SpotifyUri);
                    services.Queue.AddSong(track, command.RequestedBy, command.Platform, bypassUserLimit: roleProfile.BypassLimits, maxPerUserOverride: roleProfile.MaxSongsInQueue > 0 ? roleProfile.MaxSongsInQueue : (_queueSettings.MaxSongsPerUserEnabled ? _queueSettings.MaxSongsPerUser : 0));
                    count++;
                }
                catch (InvalidOperationException) { }
            }
            if (services.Queue.GetPlayingItem() == null && services.Queue.GetUpcoming().Count > 0)
            {
                services.Queue.Advance();
                await _sync.StartCurrentTrackAsync(services, userInitiated: true);
            }
            return count > 0 ? CollectionReply(cmdDef, "youtube_playlist_added", command, count)
                : Failure(cmdDef, "playlist_empty", command, "Listeden uygun bir şarkı kuyruğa eklenemedi.");
        }

        if (TryExtractYouTubeVideoId(command.Query, out var validatedId)) await _downloader.ValidateMusicVideoAsync("youtube:" + validatedId);
        if (!_queueSettings.SpotifyEnabled && command.Query.Contains("spotify", StringComparison.OrdinalIgnoreCase)) return Failure(cmdDef, "spotify_disabled", command, "Spotify istekleri ayarlardan kapatılmış.");
        if (_queueSettings.DirectSpotifyMode && _queueSettings.SpotifyEnabled && !TryExtractYouTubeVideoId(command.Query, out _))
        {
            return await HandlePlayDirectSpotify(command, services, cmdDef);
        }

        // 1. Search: direct URL handling, then YouTube-first for text queries
        Song? song = null;

        if (TryExtractYouTubeVideoId(command.Query, out var ytVideoId))
        {
            song = await _downloader.FetchVideoMetadataAsync(ytVideoId);
            if (song != null)
                _logger.LogInformation("Metadata from YouTube URL: \"{Title}\" – {Artist}", song.Title, song.Artist);
        }
        else if (TryExtractSpotifyTrackId(command.Query, out var spotifyTrackId))
        {
            try
            {
                song = services.Spotify.IsAuthenticated
                    ? await services.Spotify.GetTrackAsync(spotifyTrackId)
                    : await services.Spotify.ScrapeEmbedTrackAsync(spotifyTrackId);

                song ??= await services.Spotify.ScrapeEmbedTrackAsync(spotifyTrackId);

                if (song != null)
                    _logger.LogInformation("Metadata from Spotify URL: \"{Title}\" – {Artist}", song.Title, song.Artist);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not fetch Spotify track {Id}. Trying embed scraper...", spotifyTrackId);
                song = await services.Spotify.ScrapeEmbedTrackAsync(spotifyTrackId);
            }
        }
        else if (TryExtractSpotifyAlbumId(command.Query, out var spotifyAlbumId))
        {
            try
            {
                var albumTracks = services.Spotify.IsAuthenticated
                    ? await services.Spotify.GetAlbumTracksAsync(spotifyAlbumId, int.MaxValue)
                    : await services.Spotify.ScrapeEmbedAlbumTracksAsync(spotifyAlbumId, int.MaxValue);

                if (albumTracks.Count == 0)
                    albumTracks = await services.Spotify.ScrapeEmbedAlbumTracksAsync(spotifyAlbumId, int.MaxValue);

                int count = 0;
                foreach (var t in albumTracks)
                {
                    if (_banned.IsBanned(t.SpotifyUri)) continue;
                    try
                    {
                        services.Queue.AddSong(t, command.RequestedBy, command.Platform, bypassUserLimit: roleProfile.BypassLimits, maxPerUserOverride: roleProfile.MaxSongsInQueue > 0 ? roleProfile.MaxSongsInQueue : (_queueSettings.MaxSongsPerUserEnabled ? _queueSettings.MaxSongsPerUser : 0));
                        _downloader.StartDownload(t);
                        count++;
                    }
                    catch { }
                }
                if (services.Queue.GetPlayingItem() == null && services.Queue.GetUpcoming().Count > 0)
                {
                    services.Queue.Advance();
                    await _sync.StartCurrentTrackAsync(services, userInitiated: true);
                }
                var albMsg = CollectionReply(cmdDef, "spotify_album_added", command, count).Message;
                return count > 0 ? CommandResult.Ok(albMsg) : Failure(cmdDef, "playlist_empty", command, "Listeden şarkı eklenemedi; kuyruk limitlerini ve tekrarları kontrol edin.");
            }
            catch (Exception ex)
            {
                var msg = Failure(cmdDef, "service_error", command, $"@{command.RequestedBy}, Spotify albümü eklenemedi: {ex.Message}").Message;
                await BroadcastErrorAsync(msg);
                return CommandResult.Fail(msg);
            }
        }
        else if (TryExtractSpotifyPlaylistId(command.Query, out var spotifyPlId))
        {
            try
            {
                var plTracks = services.Spotify.IsAuthenticated
                    ? await services.Spotify.GetPlaylistTracksAsync(spotifyPlId, int.MaxValue)
                    : await services.Spotify.ScrapeEmbedPlaylistTracksAsync(spotifyPlId, int.MaxValue);

                if (plTracks.Count == 0)
                    plTracks = await services.Spotify.ScrapeEmbedPlaylistTracksAsync(spotifyPlId, int.MaxValue);

                int count = 0;
                foreach (var t in plTracks)
                {
                    if (_banned.IsBanned(t.SpotifyUri)) continue;
                    try
                    {
                        services.Queue.AddSong(t, command.RequestedBy, command.Platform, bypassUserLimit: roleProfile.BypassLimits, maxPerUserOverride: roleProfile.MaxSongsInQueue > 0 ? roleProfile.MaxSongsInQueue : (_queueSettings.MaxSongsPerUserEnabled ? _queueSettings.MaxSongsPerUser : 0));
                        _downloader.StartDownload(t);
                        count++;
                    }
                    catch { }
                }
                if (services.Queue.GetPlayingItem() == null && services.Queue.GetUpcoming().Count > 0)
                {
                    services.Queue.Advance();
                    await _sync.StartCurrentTrackAsync(services, userInitiated: true);
                }
                var plMsg = CollectionReply(cmdDef, "spotify_playlist_added", command, count).Message;
                return count > 0 ? CommandResult.Ok(plMsg) : Failure(cmdDef, "playlist_empty", command, "Listeden şarkı eklenemedi; kuyruk limitlerini ve tekrarları kontrol edin.");
            }
            catch (Exception ex)
            {
                var msg = Failure(cmdDef, "service_error", command, $"@{command.RequestedBy}, Spotify listesi eklenemedi: {ex.Message}").Message;
                await BroadcastErrorAsync(msg);
                return CommandResult.Fail(msg);
            }
        }
        else
        {
            // Text query: search YouTube directly first
            song = await _downloader.SearchBestMatchAsync(command.Query);
            if (song != null)
            {
                _logger.LogInformation("Found on YouTube: \"{Title}\" – {Artist}", song.Title, song.Artist);

                // Enrich cover art from iTunes or Spotify
                var enrichQuery = $"{song.Title} {song.Artist}".Trim();
                var itunesResults = await _metadata.SearchAsync(enrichQuery, 1);
                if (itunesResults.Count > 0 && IsTitleSimilar(song.Title, itunesResults[0].Title))
                {
                    song.CoverUrl = itunesResults[0].CoverUrl;
                    _logger.LogInformation("Cover enriched from iTunes for \"{Title}\"", song.Title);
                }
                else if (services.Spotify.IsAuthenticated)
                {
                    try
                    {
                        var spotifyResults = await services.Spotify.SearchAsync(enrichQuery, 1);
                        if (spotifyResults.Count > 0 && IsTitleSimilar(song.Title, spotifyResults[0].Title))
                        {
                            song.CoverUrl = spotifyResults[0].CoverUrl;
                            _logger.LogInformation("Cover enriched from Spotify for \"{Title}\"", song.Title);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Spotify enrichment failed for \"{Title}\"", song.Title);
                    }
                }
            }
        }

        if (song == null)
        {
            var template = cmdDef.GetTemplate("not_found", "@{user}, \"{song}\" için sonuç bulunamadı.");
            var msg = CommandTemplateEngine.Render(template, new TemplateContext
            {
                User = command.RequestedBy,
                Song = command.Query
            });
            await BroadcastErrorAsync(msg);
            return CommandResult.Fail(msg);
        }

        // 2. Check if song is banned
        if (_banned.IsBanned(song.SpotifyUri))
        {
            var entry = _banned.GetEntry(song.SpotifyUri)!;
            var template = cmdDef.GetTemplate("banned", "⛔ @{user}, \"{song}\" kara listede olduğu için eklenemez.");
            var msg = CommandTemplateEngine.Render(template, new TemplateContext
            {
                User = command.RequestedBy,
                Song = entry.Title,
                Artist = entry.Artist
            });
            await BroadcastErrorAsync(msg);
            return CommandResult.Fail(msg);
        }

        // 3. Check custom per-user song limit from command config


        // 4. Check local library cache
        var cached = await _library.FindByTrackIdAsync(song.SpotifyUri);
        if (cached != null && File.Exists(cached.FilePath))
            song.LocalFilePath = cached.FilePath;

        // 5. Add to queue
        try
        {
            if (song.SpotifyUri.StartsWith("youtube:")) await _downloader.ValidateMusicVideoAsync(song.SpotifyUri);
            services.Queue.AddSong(song, command.RequestedBy, command.Platform, bypassUserLimit: roleProfile.BypassLimits, maxPerUserOverride: _queueSettings.MaxSongsPerUserEnabled ? roleProfile.MaxSongsInQueue : 0);
        }
        catch (Exception ex)
        {
            await BroadcastErrorAsync(ex.Message);
            return Failure(cmdDef, "queue_error", command, ex.Message);
        }

        // 6. Notify overlays: song added
        await _hub.Clients.Group($"user:{LocalUser.Id}").SendAsync("song:added", new
        {
            title       = song.Title,
            artist      = song.Artist,
            coverUrl    = song.CoverUrl,
            requestedBy = command.RequestedBy,
            platform    = command.Platform,
        });

        // 7. Start background download if not cached
        if (song.LocalFilePath == null)
            _downloader.StartDownload(song);

        // 8. If something is already playing, pre-warm the next songs in queue
        if (services.Queue.GetCurrentItem() != null)
        {
            var upcomingList = services.Queue.GetUpcoming();
            for (int i = 0; i < Math.Min(2, upcomingList.Count); i++)
            {
                var nextSong = upcomingList[i].Song;
                if (nextSong.LocalFilePath == null)
                    _ = Task.Run(() => PrewarmNextAsync(nextSong));
            }
        }

        // 9. If nothing is playing, advance and start immediately
        bool startedImmediately = false;
        if (services.Queue.GetPlayingItem() == null)
        {
            services.Queue.Advance();
            startedImmediately = true;
            await _sync.StartCurrentTrackAsync(services, userInitiated: true);
        }

        return BuildSongRequestSuccess(cmdDef, command, song, services.Queue, startedImmediately);
    }

    private async Task<CommandResult> HandlePlayDirectSpotify(BotCommand command, UserServices services, CommandDefinition cmdDef)
    {
        var roleProfile = _cmdConfig.GetRoleProfile(command.Badges, command.RequestedBy, command.Channel);
        if (cmdDef.AllowedRoles == null && !roleProfile.CanRequestSongs)
        {
            var msg = CommandTemplateEngine.Render(cmdDef.GetTemplate("permission_denied"), new TemplateContext { User = command.RequestedBy });
            await BroadcastErrorAsync(msg);
            return CommandResult.Fail(msg);
        }

        if (!roleProfile.BypassLimits)
        {
            if (_queueSettings.MaxQueueSizeEnabled && _queueSettings.MaxQueueSize > 0 && services.Queue.QueueLength >= _queueSettings.MaxQueueSize)
            {
                var msg = Failure(cmdDef, "queue_full", command, $"@{command.RequestedBy}, şarkı kuyruğu şu anda dolu (maksimum {_queueSettings.MaxQueueSize} şarkı).").Message;
                await BroadcastErrorAsync(msg);
                return CommandResult.Fail(msg);
            }

            int userLimit = roleProfile.MaxSongsInQueue > 0
                ? roleProfile.MaxSongsInQueue
                : (_queueSettings.MaxSongsPerUserEnabled ? _queueSettings.MaxSongsPerUser : 0);

            if (_queueSettings.MaxSongsPerUserEnabled && userLimit > 0)
            {
                var userSongs = services.Queue.GetUpcoming().Count(i => i.RequestedBy.Equals(command.RequestedBy, StringComparison.OrdinalIgnoreCase));
                if (userSongs >= userLimit)
                {
                    var msg = Failure(cmdDef, "user_limit", command, $"@{command.RequestedBy}, kuyrukta zaten {userSongs} şarkınız bulunuyor (maksimum {userLimit}).").Message;
                    await BroadcastErrorAsync(msg);
                    return CommandResult.Fail(msg);
                }
            }
        }

        if (!services.Spotify.IsAuthenticated)
        {
            var errMsg = Failure(cmdDef, "spotify_not_connected", command, "Spotify bağlı değil. Lütfen ayarlardan Spotify hesabınızı bağlayın.").Message;
            await BroadcastErrorAsync(errMsg);
            return CommandResult.Fail(errMsg);
        }

        if (TryExtractSpotifyAlbumId(command.Query, out var directAlbId))
        {
            try
            {
                var tracks = await services.Spotify.GetAlbumTracksAsync(directAlbId, int.MaxValue);
                int count = 0;
                foreach (var t in tracks)
                {
                    if (_banned.IsBanned(t.SpotifyUri)) continue;
                    try
                    {
                        services.Queue.AddSong(t, command.RequestedBy, command.Platform, bypassUserLimit: roleProfile.BypassLimits, maxPerUserOverride: roleProfile.MaxSongsInQueue > 0 ? roleProfile.MaxSongsInQueue : (_queueSettings.MaxSongsPerUserEnabled ? _queueSettings.MaxSongsPerUser : 0));
                        count++;
                    }
                    catch { }
                }

                // If idle, start first track
                try
                {
                    var pb = await services.Spotify.GetPlaybackStateAsync();
                    if ((pb == null || !pb.IsPlaying) && services.Queue.GetCurrentItem() == null)
                    {
                        var first = services.Queue.Advance();
                        if (first != null) await _sync.StartCurrentTrackAsync(services, userInitiated: true);
                    }
                }
                catch { }

                return count > 0 ? CollectionReply(cmdDef, "spotify_album_added", command, count) : Failure(cmdDef, "playlist_empty", command, "Listeden şarkı eklenemedi; kuyruk limitlerini ve tekrarları kontrol edin.");
            }
            catch (Exception ex)
            {
                return Failure(cmdDef, "service_error", command, ex.Message);
            }
        }

        if (TryExtractSpotifyPlaylistId(command.Query, out var directPlId))
        {
            try
            {
                var tracks = await services.Spotify.GetPlaylistTracksAsync(directPlId, int.MaxValue);
                int count = 0;
                foreach (var t in tracks)
                {
                    if (_banned.IsBanned(t.SpotifyUri)) continue;
                    try
                    {
                        services.Queue.AddSong(t, command.RequestedBy, command.Platform, bypassUserLimit: roleProfile.BypassLimits, maxPerUserOverride: roleProfile.MaxSongsInQueue > 0 ? roleProfile.MaxSongsInQueue : (_queueSettings.MaxSongsPerUserEnabled ? _queueSettings.MaxSongsPerUser : 0));
                        count++;
                    }
                    catch { }
                }

                // If idle, start first track
                try
                {
                    var pb = await services.Spotify.GetPlaybackStateAsync();
                    if ((pb == null || !pb.IsPlaying) && services.Queue.GetCurrentItem() == null)
                    {
                        var first = services.Queue.Advance();
                        if (first != null) await _sync.StartCurrentTrackAsync(services, userInitiated: true);
                    }
                }
                catch { }

                return count > 0 ? CollectionReply(cmdDef, "spotify_playlist_added", command, count) : Failure(cmdDef, "playlist_empty", command, "Listeden şarkı eklenemedi; kuyruk limitlerini ve tekrarları kontrol edin.");
            }
            catch (Exception ex)
            {
                return Failure(cmdDef, "service_error", command, ex.Message);
            }
        }

        Song? song = null;
        if (TryExtractSpotifyTrackId(command.Query, out var trackId))
        {
            try { song = await services.Spotify.GetTrackAsync(trackId); } catch { }
        }
        else
        {
            try
            {
                var results = await services.Spotify.SearchAsync(command.Query, 10);
                song = SongSearchMatcher.Select(command.Query, results);
                if (song == null) _logger.LogInformation("No reliable Spotify title/artist match for {Query}; rejected {Count} candidates", command.Query, results.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Spotify search failed for \"{Query}\"", command.Query);
            }
        }

        if (song == null)
        {
            var template = cmdDef.GetTemplate("not_found", "@{user}, \"{song}\" için güvenilir bir Spotify eşleşmesi bulunamadı. Şarkı adıyla sanatçıyı birlikte yazın veya şarkı bağlantısını gönderin.");
            var msg = CommandTemplateEngine.Render(template, new TemplateContext
            {
                User = command.RequestedBy,
                Song = command.Query
            });
            await BroadcastErrorAsync(msg);
            return CommandResult.Fail(msg);
        }

        // Check if banned
        if (_banned.IsBanned(song.SpotifyUri))
        {
            var entry = _banned.GetEntry(song.SpotifyUri)!;
            var template = cmdDef.GetTemplate("banned", "⛔ @{user}, \"{song}\" kara listede olduğu için eklenemez.");
            var msg = CommandTemplateEngine.Render(template, new TemplateContext
            {
                User = command.RequestedBy,
                Song = entry.Title,
                Artist = entry.Artist
            });
            await BroadcastErrorAsync(msg);
            return CommandResult.Fail(msg);
        }



        // Add to local queue (Musick is the queue master)
        services.Queue.AddSong(song, command.RequestedBy, command.Platform, bypassUserLimit: roleProfile.BypassLimits, maxPerUserOverride: _queueSettings.MaxSongsPerUserEnabled ? roleProfile.MaxSongsInQueue : 0);

        // Only start playback immediately if nothing is currently playing in Musick AND Spotify is idle
        bool startedImmediately = false;
        try
        {
            var isCurrentEmpty = services.Queue.GetCurrentItem() == null;
            var pb = _sync.UsesSpotify(services) ? await services.Spotify.GetPlaybackStateAsync() : null;
            var isSpotifyPlaying = pb != null && pb.IsPlaying && !string.IsNullOrEmpty(pb.TrackUri);
            if (pb != null && !string.IsNullOrEmpty(pb.TrackUri))
                services.Queue.UpdateProgress(pb.ProgressMs, pb.IsPlaying, new Song
                {
                    SpotifyUri = pb.TrackUri, Title = pb.Title, Artist = pb.Artist,
                    CoverUrl = pb.CoverUrl, DurationMs = pb.DurationMs
                });

            if (isCurrentEmpty && !isSpotifyPlaying)
            {
                var advanced = services.Queue.Advance();
                if (advanced != null)
                {
                    await _sync.StartCurrentTrackAsync(services, userInitiated: true);
                    startedImmediately = true;
                    _logger.LogInformation("Direct Spotify: Started playing immediately \"{Title}\" by {Artist} ({Uri})", song.Title, song.Artist, song.SpotifyUri);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Direct Spotify: Playback start failed: {Message}", ex.Message);
            return Failure(cmdDef, "service_error", command, ex.Message);
        }

        await _hub.Clients.Group($"user:{LocalUser.Id}").SendAsync("song:added", new
        {
            title       = song.Title,
            artist      = song.Artist,
            coverUrl    = song.CoverUrl,
            requestedBy = command.RequestedBy,
            platform    = command.Platform,
        });

        return BuildSongRequestSuccess(cmdDef, command, song, services.Queue, startedImmediately);
    }

    private static CommandResult BuildSongRequestSuccess(CommandDefinition definition, BotCommand command, Song song, IQueueService queue, bool startedImmediately)
    {
        var context = new TemplateContext
        {
            User = command.RequestedBy,
            Requester = command.RequestedBy,
            Song = song.Title,
            Artist = song.Artist,
            Position = "1",
            QueueLength = "1",
            WaitTime = "hemen"
        };

        if (!startedImmediately)
        {
            var upcoming = queue.GetUpcoming();
            var index = upcoming.FindIndex(item => item.Song.SpotifyUri == song.SpotifyUri ||
                (item.Song.Title == song.Title && item.RequestedBy == command.RequestedBy));
            index = index >= 0 ? index : Math.Max(0, upcoming.Count - 1);

            var playback = queue.GetNowPlaying();
            var current = playback.SpotifyTrack ?? playback.Item?.Song;
            var waitMs = upcoming.Take(index).Sum(item => (long)item.Song.DurationMs);
            if (current != null)
                waitMs += Math.Max(0, current.DurationMs - playback.ProgressMs);
            var minutes = (int)Math.Ceiling(waitMs / 60_000.0);

            context.Position = queue.GetUpcomingPosition(index).ToString();
            context.QueueLength = (upcoming.Count + (current != null ? 1 : 0)).ToString();
            context.WaitTime = minutes > 0 ? $"{minutes} dk" : "hemen";
        }

        return CommandResult.Ok(CommandTemplateEngine.Render(definition.GetTemplate("success"), context));
    }


    private async Task EnsureSpotifyPlayAsync(UserServices svc, Song song)
    {
        var spotifyUri = song.SpotifyUri;
        var cleanId = Spotify.SpotifyService.ExtractCleanTrackId(spotifyUri);
        if (string.IsNullOrEmpty(cleanId) || cleanId.Length != 22)
        {
            try
            {
                var query = $"{song.Title} {song.Artist}".Trim();
                var results = await svc.Spotify.SearchAsync(query, 10);
                var match = SongSearchMatcher.Select(query, results);
                if (match != null)
                {
                    spotifyUri = match.SpotifyUri;
                    song.SpotifyUri = spotifyUri;
                    if (string.IsNullOrEmpty(song.CoverUrl)) song.CoverUrl = match.CoverUrl;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve Spotify track for \"{Title}\"", song.Title);
            }
        }

        await svc.Spotify.PlayAsync(spotifyUri);
    }

    private async Task<CommandResult> HandleSkip(BotCommand command, UserServices services, CommandDefinition cmdDef)
    {
        var current = services.Queue.GetPlayingItem();
        if (current == null && _sync.UsesSpotify(services))
        {
            if (command.Platform == "http")
            {
                await _sync.SkipCurrentAsync(services);
                return CommandResult.Ok("Sıradaki şarkıya geçildi.");
            }
            return await _sync.TrySkipCurrentAsync(services)
                ? CommandResult.Ok(CommandTemplateEngine.Render(cmdDef.GetTemplate("success"), new TemplateContext { User = command.RequestedBy }))
                : CommandResult.Fail(cmdDef.GetTemplate("busy", ""));
        }
        if (current == null)
        {
            var template = cmdDef.GetTemplate("not_playing", "@{user}, şu anda çalan bir şarkı yok.");
            return CommandResult.Fail(CommandTemplateEngine.Render(template, new TemplateContext { User = command.RequestedBy }));
        }

        // Moderator or broadcaster can skip unconditionally; viewer can only skip if it's their own track
        var isModOrBroadcaster = CommandConfigService.IsRoleAllowed("moderator", command.Badges, command.RequestedBy, command.Channel);
        if (command.Platform is not ("http" or "web") && !isModOrBroadcaster && !current.IsPlaylistItem && !current.RequestedBy.Equals(command.RequestedBy, StringComparison.OrdinalIgnoreCase))
        {
            var template = cmdDef.GetTemplate("not_owner", "@{user}, bu şarkıyı @{requester} istedi. Sadece o veya moderatörler atlayabilir.");
            var msg = CommandTemplateEngine.Render(template, new TemplateContext
            {
                User = command.RequestedBy,
                Requester = current.RequestedBy,
                Song = current.Song.Title,
                Artist = current.Song.Artist
            });
            return CommandResult.Fail(msg);
        }

        if (command.Platform == "http")
        {
            await _sync.SkipCurrentAsync(services);
            return CommandResult.Ok("Sıradaki şarkıya geçildi.");
        }
        return await _sync.TrySkipCurrentAsync(services)
                ? CommandResult.Ok(CommandTemplateEngine.Render(cmdDef.GetTemplate("success"), new TemplateContext { User = command.RequestedBy }))
                : CommandResult.Fail(cmdDef.GetTemplate("busy", ""));
    }

    private async Task<CommandResult> HandleUserSkip(BotCommand command, UserServices services, CommandDefinition cmdDef)
    {
        return await HandleSkip(command, services, cmdDef);
    }

    private CommandResult HandleSong(BotCommand command, UserServices services, CommandDefinition cmdDef)
    {
        var current = services.Queue.GetPlayingItem();
        if (current == null)
            return CommandResult.Ok(cmdDef.GetTemplate("not_playing", "Şu anda çalan bir şarkı yok."));
        return CommandResult.Ok(CommandTemplateEngine.Render(cmdDef.GetTemplate("success", "#1: {song} - {artist} (İsteyen: {requester})"),
            new TemplateContext { User = command.RequestedBy, Song = current.Song.Title, Artist = current.Song.Artist,
                Requester = string.IsNullOrEmpty(current.RequestedBy) ? "Spotify" : current.RequestedBy, Position = "1" }));
    }

    private static CommandResult HandleRevoke(BotCommand command, UserServices services, CommandDefinition cmdDef)
    {
        if (string.IsNullOrWhiteSpace(command.RequestedBy))
            return CommandResult.Fail("RequestedBy gereklidir");

        var removed = services.Queue.Revoke(command.RequestedBy);
        if (removed == null)
        {
            var template = cmdDef.GetTemplate("not_found", "@{user}, kuyrukta bekleyen bir şarkınız bulunmuyor.");
            return CommandResult.Fail(CommandTemplateEngine.Render(template, new TemplateContext { User = command.RequestedBy }));
        }

        var templateSuccess = cmdDef.GetTemplate("success", "@{user}, kuyruktaki şarkınız (\"{song}\") silindi.");
        var msg = CommandTemplateEngine.Render(templateSuccess, new TemplateContext
        {
            User = command.RequestedBy,
            Song = removed.Song.Title,
            Artist = removed.Song.Artist
        });
        return CommandResult.Ok(msg);
    }

    private static CommandResult HandleInfo(BotCommand command, UserServices services, CommandDefinition cmdDef)
    {
        if (string.IsNullOrWhiteSpace(command.RequestedBy))
            return CommandResult.Fail("RequestedBy gereklidir");

        var upcoming = services.Queue.GetUpcoming();
        var userSongs = upcoming
            .Select((item, index) => (item, position: services.Queue.GetUpcomingPosition(index)))
            .Where(x => x.item.RequestedBy.Equals(command.RequestedBy, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (userSongs.Count == 0)
        {
            var template = cmdDef.GetTemplate("not_found", "@{user}, kuyrukta bekleyen şarkınız bulunmuyor.");
            return CommandResult.Ok(CommandTemplateEngine.Render(template, new TemplateContext { User = command.RequestedBy }));
        }

        var details = string.Join(", ", userSongs.Select(x => $"#{x.position} \"{x.item.Song.Title}\""));
        return CommandResult.Ok($"@{command.RequestedBy}, kuyrukta {userSongs.Count} şarkınız var: {details}");
    }

    private CommandResult HandleAqui(BotCommand command, CommandDefinition cmdDef)
    {
        if (string.IsNullOrWhiteSpace(command.RequestedBy))
            return CommandResult.Fail("RequestedBy gereklidir");

        var confirmed = _presence.ConfirmPresence(command.RequestedBy);
        if (confirmed)
        {
            var template = cmdDef.GetTemplate("success", "@{user} onaylandı! Şarkınız rezerve edildi.");
            return CommandResult.Ok(CommandTemplateEngine.Render(template, new TemplateContext { User = command.RequestedBy }));
        }

        var templateNotFound = cmdDef.GetTemplate("not_found", "@{user}, şu an onay bekleyen bir şarkınız yok.");
        return CommandResult.Fail(CommandTemplateEngine.Render(templateNotFound, new TemplateContext { User = command.RequestedBy }));
    }

    private static CommandResult HandleBump(BotCommand command, UserServices services, CommandDefinition cmdDef)
    {
        if (string.IsNullOrWhiteSpace(command.RequestedBy))
            return CommandResult.Fail("RequestedBy is required for bump");

        var moved = services.Queue.Bump(command.RequestedBy);
        return moved
            ? CommandResult.Ok($"@{command.RequestedBy} şarkısı kuyrukta öne taşındı.")
            : CommandResult.Fail($"@{command.RequestedBy} taşınacak şarkınız bulunamadı.");
    }

    private async Task<CommandResult?> HandleVote(BotCommand command, bool skip, CommandDefinition cmdDef)
    {
        if (string.IsNullOrWhiteSpace(command.RequestedBy))
            return CommandResult.Fail("Oy kullanmak için kullanıcı adı gereklidir");

        if (!_kickVote.VotingEnabled) return null;

        var result = await _kickVote.VoteAsync(command.RequestedBy, skip);
        return result switch
        {
            "ok" => CommandResult.Ok(CommandTemplateEngine.Render(
                cmdDef.GetTemplate("success", "@{user} şarkı için oy kullandı."),
                new TemplateContext { User = command.RequestedBy })),
            "already_voted" => CommandResult.Fail(CommandTemplateEngine.Render(
                cmdDef.GetTemplate("already_voted", "@{user}, zaten oy kullandınız."),
                new TemplateContext { User = command.RequestedBy })),
            "no_active" => CommandResult.Fail(CommandTemplateEngine.Render(
                cmdDef.GetTemplate("no_active_vote", "Şu anda aktif bir oylama yok."),
                new TemplateContext { User = command.RequestedBy })),
            _ => CommandResult.Fail("Oy kaydedilirken hata oluştu.")
        };
    }

    private CommandResult HandleKeep(BotCommand command, CommandDefinition cmdDef)
    {
        var kept = _presence.KeepSong();
        return kept
            ? CommandResult.Ok(cmdDef.GetTemplate("success", "Şarkı kaldı! Sohbet şarkıyı kurtardı."))
            : CommandResult.Fail(cmdDef.GetTemplate("not_found", "Onay bekleyen şarkı bulunmuyor."));
    }

    private async Task<CommandResult> HandleLike(BotCommand command, UserServices services, CommandDefinition cmdDef)
    {
        var current = services.Queue.GetPlayingItem();
        if (current == null)
        {
            var template = cmdDef.GetTemplate("not_playing", "Şu anda çalan bir şarkı yok.");
            return CommandResult.Fail(CommandTemplateEngine.Render(template, new TemplateContext { User = command.RequestedBy }));
        }

        var added = await _autoQueue.AddAsync(current.Song);
        var title = current.Song.Title;
        if (added)
        {
            var template = cmdDef.GetTemplate("success", "❤️ \"{song}\" otomatik çalma listesine eklendi.");
            return CommandResult.Ok(CommandTemplateEngine.Render(template, new TemplateContext { User = command.RequestedBy, Song = title }));
        }
        else
        {
            var template = cmdDef.GetTemplate("already_exists", "\"{song}\" zaten otomatik çalma listesinde mevcut.");
            return CommandResult.Fail(CommandTemplateEngine.Render(template, new TemplateContext { User = command.RequestedBy, Song = title }));
        }
    }

    private static CommandResult HandleQueue(BotCommand command, UserServices services, CommandDefinition cmdDef)
    {
        var upcoming = services.Queue.GetState().Upcoming;

        if (upcoming.Count == 0)
        {
            var playing = services.Queue.GetPlayingItem();
            if (playing != null) return CommandResult.Ok($"#1: {playing.Song.Title} - {playing.Song.Artist}. Bekleyen şarkı yok.");
            var template = cmdDef.GetTemplate("empty", "Kuyruk şu anda boş. !istek <şarkı> yazarak ilk şarkıyı sen ekle!");
            return CommandResult.Ok(CommandTemplateEngine.Render(template, new TemplateContext { User = command.RequestedBy }));
        }

        var nextSong = upcoming[0];
        var templateSuccess = cmdDef.GetTemplate("success", "Kuyrukta {queue_length} şarkı var. Sıradaki: \"{song}\" - {artist} (İsteyen: @{requester})");
        var msg = CommandTemplateEngine.Render(templateSuccess, new TemplateContext
        {
            User = command.RequestedBy,
            QueueLength = (upcoming.Count + (services.Queue.GetPlayingItem() != null ? 1 : 0)).ToString(),
            Song = nextSong.Song.Title,
            Artist = nextSong.Song.Artist,
            Requester = nextSong.RequestedBy,
            Position = services.Queue.GetUpcomingPosition(0).ToString()
        });
        return CommandResult.Ok(msg);
    }

    private static CommandResult HandlePos(BotCommand command, UserServices services, CommandDefinition cmdDef)
    {
        if (string.IsNullOrWhiteSpace(command.RequestedBy))
            return CommandResult.Fail("RequestedBy gereklidir");

        var playing = services.Queue.GetPlayingItem();
        if (playing != null && playing.RequestedBy.Equals(command.RequestedBy, StringComparison.OrdinalIgnoreCase))
            return CommandResult.Ok(CommandTemplateEngine.Render(cmdDef.GetTemplate("success", "@{user}, {song} #{position}. sırada ({wait_time})."),
                new TemplateContext { User = command.RequestedBy, Song = playing.Song.Title, Artist = playing.Song.Artist, Position = "1", WaitTime = "şu an çalıyor" }));

        var upcoming = services.Queue.GetUpcoming();
        var idx = upcoming.FindIndex(i =>
            i.RequestedBy.Equals(command.RequestedBy, StringComparison.OrdinalIgnoreCase));

        if (idx < 0)
        {
            var template = cmdDef.GetTemplate("not_found", "@{user}, kuyrukta bekleyen şarkınız bulunmuyor.");
            return CommandResult.Fail(CommandTemplateEngine.Render(template, new TemplateContext { User = command.RequestedBy }));
        }

        var item    = upcoming[idx];
        var waitMs  = upcoming.Take(idx).Sum(i => i.Song.DurationMs);
        var current = services.Queue.GetPlayingItem();
        var nowPlaying = services.Queue.GetNowPlaying();
        if (current != null && current.Song.DurationMs > 0)
        {
            waitMs += Math.Max(0, current.Song.DurationMs - nowPlaying.ProgressMs);
        }
        var waitMin = (int)Math.Ceiling(waitMs / 60_000.0);
        var waitStr = waitMin > 0 ? $"{waitMin} dk" : "hemen";

        var templateSuccess = cmdDef.GetTemplate("success", "@{user}, \"{song}\" #{position}. sırada (Tahmini bekleme: ~{wait_time}).");
        var msg = CommandTemplateEngine.Render(templateSuccess, new TemplateContext
        {
            User = command.RequestedBy,
            Song = item.Song.Title,
            Artist = item.Song.Artist,
            Position = services.Queue.GetUpcomingPosition(idx).ToString(),
            WaitTime = waitStr
        });
        return CommandResult.Ok(msg);
    }

    private async Task<CommandResult> HandleHistory(BotCommand command, UserServices services, CommandDefinition cmdDef)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBot.Data.MusicBotDbContext>();
        var recent = await db.PlayedSongs
            .OrderByDescending(p => p.PlayedAt)
            .Take(4)
            .ToListAsync();

        var playingUri = services.Queue.GetPlayingItem()?.Song.SpotifyUri;
        if (recent.FirstOrDefault()?.TrackId == playingUri && playingUri != null) recent.RemoveAt(0);
        recent = recent.Take(3).ToList();
        if (recent.Count == 0)
        {
            var template = cmdDef.GetTemplate("empty", "Henüz çalınan şarkı geçmişi yok.");
            return CommandResult.Ok(CommandTemplateEngine.Render(template, new TemplateContext { User = command.RequestedBy }));
        }

        var list = string.Join(", ", recent.Select(p =>
            string.IsNullOrEmpty(p.Artist) ? $"\"{p.Title}\"" : $"\"{p.Title}\" ({p.Artist})"));

        var templateSuccess = cmdDef.GetTemplate("success", "Son çalan şarkılar: {song}");
        var msg = CommandTemplateEngine.Render(templateSuccess, new TemplateContext
        {
            User = command.RequestedBy,
            Song = list
        });
        return CommandResult.Ok(msg);
    }

    private async Task<CommandResult> HandleVolume(BotCommand command, UserServices services, CommandDefinition cmdDef)
    {
        var isModOrBroadcaster = CommandConfigService.IsRoleAllowed("moderator", command.Badges, command.RequestedBy, command.Channel);
        if (!isModOrBroadcaster && !CommandConfigService.IsCommandAllowed(cmdDef.RequiredRole, cmdDef.AllowedRoles, command.Badges, command.RequestedBy, command.Channel))
        {
            var deniedTmpl = cmdDef.GetTemplate("permission_denied", "@{user}, ses seviyesini değiştirme yetkiniz bulunmuyor.");
            var deniedMsg = CommandTemplateEngine.Render(deniedTmpl, new TemplateContext { User = command.RequestedBy });
            await BroadcastErrorAsync(deniedMsg);
            return CommandResult.Fail(deniedMsg);
        }

        if (string.IsNullOrWhiteSpace(command.Query) ||
            !int.TryParse(command.Query.Trim().TrimEnd('%'), out var vol) ||
            vol < 0 || vol > 100)
        {
            var invalidTmpl = cmdDef.GetTemplate("invalid_range", "@{user}, lütfen 0 ile 100 arasında bir ses seviyesi belirtin. Örn: !ses 50");
            var msg = CommandTemplateEngine.Render(invalidTmpl, new TemplateContext { User = command.RequestedBy });
            await BroadcastErrorAsync(msg);
            return CommandResult.Fail(msg);
        }

        // Apply to the active playback source
        string? spotifyErr = null;
        if (_sync.UsesSpotify(services))
        {
            try
            {
                await services.Spotify.SetVolumeAsync(vol);
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("Premium"))
                    spotifyErr = cmdDef.GetTemplate("premium_required", "Spotify ses kontrolü için Spotify Premium hesabı gereklidir.");
                else spotifyErr = ex.Message;
            }
        }

        if (spotifyErr != null)
        {
            await BroadcastErrorAsync(spotifyErr);
            return CommandResult.Fail(spotifyErr);
        }

        // Set Local NAudio player volume
        services.Player.SetVolume(vol / 100f);

        // Notify all connected UI clients via SignalR
        try
        {
            await _hub.Clients.All.SendAsync("player:volume", vol / 100f);
        }
        catch { }

        services.VolumeSync.LocalChange(DateTimeOffset.UtcNow);

        var successTmpl = cmdDef.GetTemplate("success", "🔊 @{user}, ses seviyesi %{volume} olarak ayarlandı.");
        var successMsg = CommandTemplateEngine.Render(successTmpl, new TemplateContext
        {
            User = command.RequestedBy,
            Volume = vol.ToString()
        });

        return CommandResult.Ok(successMsg);
    }

    private Task<CommandResult> HandleCustomCommand(BotCommand command, UserServices services, CustomCommandDefinition customDef)
    {
        if (!customDef.Enabled)
            return Task.FromResult(CommandResult.Fail($"@{command.RequestedBy}, bu komut şu anda devre dışı."));

        if (!CommandConfigService.IsCommandAllowed(customDef.RequiredRole, customDef.AllowedRoles, command.Badges, command.RequestedBy, command.Channel))
        {
            return Task.FromResult(CommandResult.Fail($"@{command.RequestedBy}, bu komutu kullanma yetkiniz bulunmuyor."));
        }

        if (!_cmdConfig.CheckCooldownWithRole(customDef.Name, command.RequestedBy, customDef.CooldownSeconds, customDef.RoleCooldowns, customDef.ExemptRoles, command.Badges, command.Channel, out int remainingSeconds))
        {
            return Task.FromResult(CommandResult.Fail($"@{command.RequestedBy}, lütfen {remainingSeconds} saniye bekleyin."));
        }

        var current = services.Queue.GetPlayingItem();
        var upcoming = services.Queue.GetUpcoming();
        var responseText = customDef.ResponseText ?? "";

        var text = responseText
            .Replace("{kullanici}", command.RequestedBy)
            .Replace("{user}", command.RequestedBy)
            .Replace("{sarki}", current?.Song.Title ?? "Şu anda çalan şarkı yok")
            .Replace("{song}", current?.Song.Title ?? "Şu anda çalan şarkı yok")
            .Replace("{sanatci}", current?.Song.Artist ?? "")
            .Replace("{artist}", current?.Song.Artist ?? "")
            .Replace("{siradaki}", upcoming.FirstOrDefault()?.Song.Title ?? "Kuyruk boş")
            .Replace("{next}", upcoming.FirstOrDefault()?.Song.Title ?? "Kuyruk boş")
            .Replace("{sayi}", upcoming.Count.ToString())
            .Replace("{count}", upcoming.Count.ToString())
            .Replace("{kanal}", command.Channel ?? "")
            .Replace("{channel}", command.Channel ?? "");

        _cmdConfig.RecordSuccessfulCommand(customDef.Name, command.RequestedBy);
        return Task.FromResult(CommandResult.Ok(text));
    }

    private static CommandResult CollectionReply(CommandDefinition definition, string key, BotCommand command, int count) =>
        CommandResult.Ok(CommandTemplateEngine.Render(definition.GetTemplate(key, "@{user}, {queue_length} şarkı eklendi."), new TemplateContext { User = command.RequestedBy, QueueLength = count.ToString() }));

    private static CommandResult Failure(CommandDefinition? definition, string key, BotCommand command, string error)
    {
        var template = definition?.GetTemplate(key, "@{user}, {error}") ?? "@{user}, {error}";
        return CommandResult.Fail(CommandTemplateEngine.Render(template, new TemplateContext { User = command.RequestedBy, Error = error, Song = command.Query ?? "" }));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task PrewarmNextAsync(Song song)
    {
        if (song.SpotifyUri.StartsWith("youtube:", StringComparison.OrdinalIgnoreCase)) return;
        if (song.LocalFilePath != null && File.Exists(song.LocalFilePath)
            && new FileInfo(song.LocalFilePath).Length > 100_000)
            return;
        try
        {
            await _downloader.GetOrStartDownloadAsync(song);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Queue pre-warm download failed for \"{Title}\"", song.Title);
        }
    }

    private Task BroadcastErrorAsync(string message)
        => _hub.Clients.Group($"user:{LocalUser.Id}").SendAsync("queue:error", new { message });

    internal static bool TryExtractYouTubeVideoId(string query, out string videoId)
        => YouTubeLinks.TryVideoId(query, out videoId);

    internal static bool IsTitleSimilar(string ytTitle, string metaTitle)
    {
        if (string.IsNullOrEmpty(ytTitle) || string.IsNullOrEmpty(metaTitle)) return false;
        var a = ytTitle.ToLowerInvariant().Trim();
        var b = metaTitle.ToLowerInvariant().Trim();
        return a == b || a.Contains(b) || b.Contains(a);
    }

    internal static bool TryExtractSpotifyTrackId(string query, out string trackId)
    {
        trackId = string.Empty;
        if (!Uri.TryCreate(query.Trim(), UriKind.Absolute, out var uri)) return false;
        var host = uri.Host.ToLowerInvariant();
        if (uri.Scheme is not ("https" or "http") || host is not ("open.spotify.com" or "www.open.spotify.com")) return false;

        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length > 0 && (parts[0].StartsWith("intl-", StringComparison.OrdinalIgnoreCase) || parts[0] == "embed")) parts = parts.Skip(1).ToArray();
        if (parts.Length == 2 && parts[0] == "track")
        {
            trackId = parts[1];
            return !string.IsNullOrEmpty(trackId);
        }

        return false;
    }

    internal static bool TryExtractSpotifyAlbumId(string query, out string albumId)
    {
        albumId = string.Empty;
        if (!Uri.TryCreate(query.Trim(), UriKind.Absolute, out var uri)) return false;
        var host = uri.Host.ToLowerInvariant();
        if (uri.Scheme is not ("https" or "http") || host is not ("open.spotify.com" or "www.open.spotify.com")) return false;

        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length > 0 && (parts[0].StartsWith("intl-", StringComparison.OrdinalIgnoreCase) || parts[0] == "embed")) parts = parts.Skip(1).ToArray();
        if (parts.Length == 2 && parts[0] == "album")
        {
            albumId = parts[1];
            return !string.IsNullOrEmpty(albumId);
        }

        return false;
    }

    internal static bool TryExtractSpotifyPlaylistId(string query, out string playlistId)
    {
        playlistId = string.Empty;
        if (!Uri.TryCreate(query.Trim(), UriKind.Absolute, out var uri)) return false;
        var host = uri.Host.ToLowerInvariant();
        if (uri.Scheme is not ("https" or "http") || host is not ("open.spotify.com" or "www.open.spotify.com")) return false;

        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length > 0 && (parts[0].StartsWith("intl-", StringComparison.OrdinalIgnoreCase) || parts[0] == "embed")) parts = parts.Skip(1).ToArray();
        if (parts.Length == 2 && parts[0] == "playlist")
        {
            playlistId = parts[1];
            return !string.IsNullOrEmpty(playlistId);
        }

        return false;
    }
}
