namespace MusicBot.Core.Models;

public class RoleProfile
{
    public string Role { get; set; } = string.Empty; // broadcaster, moderator, vip, subscriber, everyone
    public string Name { get; set; } = string.Empty; // Yayıncı, Moderatör, VIP, Abone, Herkes
    public bool CanRequestSongs { get; set; } = true;
    public int CooldownSeconds { get; set; } = 30;
    public int MaxSongsInQueue { get; set; } = 3;
    public bool BypassCooldown { get; set; } = false;
    public bool BypassLimits { get; set; } = false;
}

public class CommandDefinition
{
    public string Name { get; set; } = string.Empty;
    public List<string> Aliases { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public int CooldownSeconds { get; set; } = 0;
    public List<string>? AllowedRoles { get; set; } // null preserves legacy role rules; empty denies every role
    public string RequiredRole { get; set; } = "everyone"; // everyone, subscriber, moderator, broadcaster
    public int MaxPerUser { get; set; } = 0; // 0 = no custom limit (use queue default)
    public Dictionary<string, int> RoleCooldowns { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> ExemptRoles { get; set; } = new() { "broadcaster", "moderator" };
    public Dictionary<string, string> Templates { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string GetTemplate(string key, string fallback = "")
    {
        // An explicitly empty template means the broadcaster disabled this reply.
        return Templates.TryGetValue(key, out var template) ? template ?? "" : fallback;
    }
}

public class CustomCommandDefinition
{
    public string Name { get; set; } = string.Empty;
    public List<string> Aliases { get; set; } = new();
    public string ResponseText { get; set; } = string.Empty;
    public List<string>? AllowedRoles { get; set; } // null preserves legacy role rules; empty denies every role
    public string RequiredRole { get; set; } = "everyone"; // everyone, subscriber, vip, moderator, broadcaster
    public int CooldownSeconds { get; set; } = 5;
    public Dictionary<string, int> RoleCooldowns { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> ExemptRoles { get; set; } = new() { "broadcaster", "moderator" };
    public bool Enabled { get; set; } = true;
}

public class CommandsConfiguration
{
    public Dictionary<string, CommandDefinition> Commands { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, CustomCommandDefinition> CustomCommands { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, RoleProfile> RoleProfiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static CommandsConfiguration CreateDefault()
    {
        var config = new CommandsConfiguration
        {
            RoleProfiles = new Dictionary<string, RoleProfile>(StringComparer.OrdinalIgnoreCase)
            {
                ["broadcaster"] = new()
                {
                    Role = "broadcaster",
                    Name = "Yayıncı",
                    CanRequestSongs = true,
                    CooldownSeconds = 0,
                    MaxSongsInQueue = 50,
                    BypassCooldown = true,
                    BypassLimits = true
                },
                ["moderator"] = new()
                {
                    Role = "moderator",
                    Name = "Moderatör",
                    CanRequestSongs = true,
                    CooldownSeconds = 0,
                    MaxSongsInQueue = 10,
                    BypassCooldown = true,
                    BypassLimits = false
                },
                ["vip"] = new()
                {
                    Role = "vip",
                    Name = "VIP",
                    CanRequestSongs = true,
                    CooldownSeconds = 15,
                    MaxSongsInQueue = 5,
                    BypassCooldown = false,
                    BypassLimits = false
                },
                ["subscriber"] = new()
                {
                    Role = "subscriber",
                    Name = "Abone",
                    CanRequestSongs = true,
                    CooldownSeconds = 30,
                    MaxSongsInQueue = 3,
                    BypassCooldown = false,
                    BypassLimits = false
                },
                ["everyone"] = new()
                {
                    Role = "everyone",
                    Name = "İzleyici / Herkes",
                    CanRequestSongs = true,
                    CooldownSeconds = 60,
                    MaxSongsInQueue = 2,
                    BypassCooldown = false,
                    BypassLimits = false
                }
            },
            Commands = new Dictionary<string, CommandDefinition>(StringComparer.OrdinalIgnoreCase)
            {
                ["play"] = new()
                {
                    Name = "play",
                    Aliases = new() { "sr", "istek", "play" },
                    Enabled = true,
                    CooldownSeconds = 5,
                    RequiredRole = "everyone",
                    MaxPerUser = 5,
                    Templates = new()
                    {
                        ["success"] = "@{user}, \"{song}\" - {artist} kuyruğa eklendi. (Sıra: #{position}, Bekleme: ~{wait_time})",
                        ["not_found"] = "@{user}, \"{song}\" için sonuç bulunamadı.",
                        ["banned"] = "⛔ @{user}, \"{song}\" kara listede olduğu için eklenemez.",
                        ["queue_full"] = "{error}",
                        ["user_limit"] = "{error}",
                        ["spotify_not_connected"] = "@{user}, {error}",
                        ["youtube_playlist_added"] = "@{user}, YouTube listesinden {queue_length} şarkı eklendi!",
                        ["spotify_album_added"] = "@{user}, Spotify albümünden {queue_length} şarkı eklendi!",
                        ["spotify_playlist_added"] = "@{user}, Spotify listesinden {queue_length} şarkı eklendi!",
                        ["youtube_disabled"] = "YouTube istekleri ayarlardan kapatılmış.",
                        ["spotify_disabled"] = "Spotify istekleri ayarlardan kapatılmış.",
                        ["invalid_link"] = "Geçerli bir YouTube bağlantısı girin.",
                        ["playlist_disabled"] = "Sohbetten çalma listesi ve albüm ekleme kapalı. Tek bir şarkının bağlantısını gönderin.",
                        ["service_error"] = "@{user}, {error}",
                        ["queue_error"] = "@{user}, {error}",
                        ["playlist_empty"] = "Listeden uygun bir şarkı eklenemedi; kuyruk limitlerini ve tekrarları kontrol edin.",
                        ["empty_query"] = "@{user}, lütfen bir şarkı adı veya YouTube linki belirtin. Örn: !istek sanatçı şarkı",
                        ["max_reached"] = "@{user}, ekleme sınırınıza ulaştınız! En fazla {position} şarkı ekleyebilirsiniz.",
                        ["cooldown"] = "@{user}, lütfen tekrar istek atmadan önce {wait_time} bekleyin.",
                        ["permission_denied"] = "@{user}, şarkı isteme yetkiniz bulunmuyor."
                    }
                },
                ["skip"] = new()
                {
                    Name = "skip",
                    Aliases = new() { "skip", "atla", "gec" },
                    Enabled = true,
                    CooldownSeconds = 5,
                    RequiredRole = "everyone",
                    Templates = new()
                    {
                        ["success"] = "@{user} şarkıyı atladı.",
                        ["not_playing"] = "@{user}, şu anda çalan bir şarkı yok.",
                        ["not_owner"] = "@{user}, bu şarkıyı @{requester} istedi. Sadece o veya moderatörler atlayabilir.",
                        ["cooldown"] = "@{user}, skip komutunu tekrar kullanmak için {wait_time} bekleyin.",
                        ["permission_denied"] = "@{user}, şarkı atlama yetkiniz bulunmuyor."
                    }
                },
                ["sira"] = new()
                {
                    Name = "sira",
                    Aliases = new() { "sira", "queue", "liste" },
                    Enabled = true,
                    CooldownSeconds = 10,
                    RequiredRole = "everyone",
                    Templates = new()
                    {
                        ["success"] = "Kuyrukta {queue_length} şarkı var. Sıradaki: \"{song}\" - {artist} (İsteyen: @{requester})",
                        ["empty"] = "Kuyruk şu anda boş. !istek <şarkı> yazarak ilk şarkıyı sen ekle!",
                        ["cooldown"] = "@{user}, lütfen {wait_time} bekleyin."
                    }
                },
                ["simdi"] = new()
                {
                    Name = "simdi",
                    Aliases = new() { "simdi", "calan", "song", "current" },
                    Enabled = true,
                    CooldownSeconds = 5,
                    RequiredRole = "everyone",
                    Templates = new()
                    {
                        ["success"] = "Şu an çalan: \"{song}\" - {artist} (İsteyen: @{requester})",
                        ["not_playing"] = "Şu anda çalan bir şarkı yok.",
                        ["cooldown"] = "@{user}, lütfen {wait_time} bekleyin."
                    }
                },
                ["sil"] = new()
                {
                    Name = "sil",
                    Aliases = new() { "sil", "iptal", "revoke" },
                    Enabled = true,
                    CooldownSeconds = 5,
                    RequiredRole = "everyone",
                    Templates = new()
                    {
                        ["success"] = "@{user}, kuyruktaki şarkınız (\"{song}\") silindi.",
                        ["not_found"] = "@{user}, kuyrukta bekleyen bir şarkınız bulunmuyor.",
                        ["cooldown"] = "@{user}, lütfen {wait_time} bekleyin."
                    }
                },
                ["pos"] = new()
                {
                    Name = "pos",
                    Aliases = new() { "pos", "position", "konum" },
                    Enabled = true,
                    CooldownSeconds = 10,
                    RequiredRole = "everyone",
                    Templates = new()
                    {
                        ["success"] = "@{user}, \"{song}\" #{position}. sırada (Tahmini bekleme: ~{wait_time}).",
                        ["not_found"] = "@{user}, kuyrukta bekleyen bir şarkınız bulunmuyor.",
                        ["cooldown"] = "@{user}, lütfen {wait_time} bekleyin."
                    }
                },
                ["like"] = new()
                {
                    Name = "like",
                    Aliases = new() { "like", "love", "sevdim" },
                    Enabled = true,
                    CooldownSeconds = 5,
                    RequiredRole = "everyone",
                    Templates = new()
                    {
                        ["success"] = "❤️ \"{song}\" otomatik çalma listesine eklendi.",
                        ["already_exists"] = "\"{song}\" zaten otomatik çalma listesinde mevcut.",
                        ["not_playing"] = "Şu anda çalan bir şarkı yok."
                    }
                },
                ["vote_yes"] = new()
                {
                    Name = "vote_yes",
                    Aliases = new() { "evet", "si", "yes" },
                    Enabled = true,
                    CooldownSeconds = 0,
                    RequiredRole = "everyone",
                    Templates = new()
                    {
                        ["success"] = "@{user} şarkının geçilmesi için oy verdi.",
                        ["already_voted"] = "@{user}, zaten oy kullandınız.",
                        ["no_active_vote"] = "Şu anda aktif bir oylama yok."
                    }
                },
                ["vote_no"] = new()
                {
                    Name = "vote_no",
                    Aliases = new() { "hayir", "no" },
                    Enabled = true,
                    CooldownSeconds = 0,
                    RequiredRole = "everyone",
                    Templates = new()
                    {
                        ["success"] = "@{user} şarkının kalması için oy verdi.",
                        ["already_voted"] = "@{user}, zaten oy kullandınız.",
                        ["no_active_vote"] = "Şu anda aktif bir oylama yok."
                    }
                },
                ["burada"] = new()
                {
                    Name = "burada",
                    Aliases = new() { "burada", "here", "aqui" },
                    Enabled = true,
                    CooldownSeconds = 0,
                    RequiredRole = "everyone",
                    Templates = new()
                    {
                        ["success"] = "@{user} onaylandı! Şarkınız rezerve edildi.",
                        ["not_found"] = "@{user}, şu an onay bekleyen bir şarkınız yok."
                    }
                },
                ["gecmis"] = new()
                {
                    Name = "gecmis",
                    Aliases = new() { "gecmis", "history", "historial" },
                    Enabled = true,
                    CooldownSeconds = 15,
                    RequiredRole = "everyone",
                    Templates = new()
                    {
                        ["success"] = "Son çalan şarkılar: {song}",
                        ["empty"] = "Henüz çalınan şarkı geçmişi yok."
                    }
                },
                ["ses"] = new()
                {
                    Name = "ses",
                    Aliases = new() { "ses", "volume", "vol" },
                    Enabled = true,
                    CooldownSeconds = 5,
                    RequiredRole = "moderator",
                    RoleCooldowns = new() { ["broadcaster"] = 0, ["moderator"] = 0, ["vip"] = 5, ["subscriber"] = 10, ["everyone"] = 15 },
                    ExemptRoles = new() { "broadcaster", "moderator" },
                    Templates = new()
                    {
                        ["success"] = "🔊 @{user}, ses seviyesi %{volume} olarak ayarlandı.",
                        ["invalid_range"] = "@{user}, lütfen 0 ile 100 arasında bir ses seviyesi belirtin. Örn: !ses 50",
                        ["permission_denied"] = "@{user}, ses seviyesini değiştirme yetkiniz bulunmuyor.",
                        ["premium_required"] = "Spotify ses kontrolü için Spotify Premium hesabı gereklidir.",
                        ["cooldown"] = "@{user}, lütfen {wait_time} bekleyin."
                    }
                },
                ["announce"] = new()
                {
                    Name = "announce",
                    Aliases = new() { "duyuru", "bildirim" },
                    Enabled = true,
                    CooldownSeconds = 0,
                    RequiredRole = "broadcaster",
                    Templates = new()
                    {
                        ["success"] = "🎵 Şimdi Çalıyor: \"{song}\" - {artist} (İsteyen: @{requester})",
                        ["success_no_requester"] = "🎵 Şimdi Çalıyor: \"{song}\" - {artist}"
                    }
                }
            }
        };
        foreach (var command in config.Commands.Values) command.Templates.TryAdd("service_error", "@{user}, {error}");
        return config;
    }
}
