using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MusicBot.Core.Models;
using MusicBot.Core.Services;

namespace MusicBot.Services.Commands;

public class CommandConfigService : IDisposable
{
    private readonly ILogger<CommandConfigService> _logger;
    private readonly string _configFilePath;
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer _reloadDebounceTimer;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private CommandsConfiguration _currentConfig = CommandsConfiguration.CreateDefault();
    private Dictionary<string, (string CommandKey, CommandDefinition Definition)> _aliasMap = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, CustomCommandDefinition> _customAliasMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _cooldowns = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public CommandsConfiguration Current
    {
        get
        {
            lock (_lock) return _currentConfig;
        }
    }

    public event Action<CommandsConfiguration>? OnConfigurationChanged;

    public CommandConfigService(ILogger<CommandConfigService> logger, IConfiguration configuration)
    {
        _logger = logger;

        var dataDir = configuration["DataDirectory"]
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MusicBot");
        Directory.CreateDirectory(dataDir);

        _configFilePath = Path.Combine(dataDir, "commands.json");
        _reloadDebounceTimer = new Timer(_ => LoadConfiguration(logSuccess: true), null, Timeout.Infinite, Timeout.Infinite);

        // Load or create initial commands.json
        InitializeConfiguration();

        // Setup FileSystemWatcher for hot reload
        try
        {
            var directory = Path.GetDirectoryName(_configFilePath);
            var fileName = Path.GetFileName(_configFilePath);

            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
            {
                _watcher = new FileSystemWatcher(directory, fileName)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    EnableRaisingEvents = true
                };

                _watcher.Changed += OnConfigFileChanged;
                _watcher.Created += OnConfigFileChanged;
                _watcher.Renamed += OnConfigFileChanged;

                _logger.LogInformation("Watching for commands configuration changes at {Path}", _configFilePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not initialize FileSystemWatcher for {Path}. Hot-reload will be disabled.", _configFilePath);
        }
    }

    private void InitializeConfiguration()
    {
        lock (_lock)
        {
            if (!File.Exists(_configFilePath))
            {
                try
                {
                    var defaultConfig = CommandsConfiguration.CreateDefault();
                    var json = JsonSerializer.Serialize(defaultConfig, JsonOptions);
                    File.WriteAllText(_configFilePath, json);
                    _logger.LogInformation("Created default commands.json at {Path}", _configFilePath);
                    ApplyConfiguration(defaultConfig, logSuccess: false);
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to create default commands.json at {Path}. Using built-in defaults.", _configFilePath);
                    ApplyConfiguration(CommandsConfiguration.CreateDefault(), logSuccess: false);
                    return;
                }
            }

            LoadConfiguration(logSuccess: false);
        }
    }

    private void OnConfigFileChanged(object sender, FileSystemEventArgs e)
    {
        // Debounce multiple rapid file events to a single reload
        _reloadDebounceTimer.Change(300, Timeout.Infinite);
    }

    public bool LoadConfiguration(bool logSuccess = true)
    {
        lock (_lock)
        {
            if (!File.Exists(_configFilePath))
            {
                _logger.LogWarning("commands.json not found at {Path}. Keeping current configuration.", _configFilePath);
                return false;
            }

            // Retry file reading a few times to handle momentary file locks from editors
            string? json = null;
            for (var i = 0; i < 5; i++)
            {
                try
                {
                    using var stream = new FileStream(_configFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    json = reader.ReadToEnd();
                    break;
                }
                catch (IOException)
                {
                    Thread.Sleep(50);
                }
            }

            if (json == null)
            {
                _logger.LogWarning("Could not read {Path} due to file lock. Retaining previous configuration.", _configFilePath);
                return false;
            }

            try
            {
                var parsed = JsonSerializer.Deserialize<CommandsConfiguration>(json, JsonOptions);
                if (parsed == null || parsed.Commands == null || parsed.Commands.Count == 0)
                {
                    _logger.LogWarning("commands.json parsed to empty configuration. Retaining previous configuration.");
                    return false;
                }

                // ── Auto-merge newly introduced default commands & profiles ──
                var defaults = CommandsConfiguration.CreateDefault();
                bool needsPersist = false;

                foreach (var (defaultKey, defaultDef) in defaults.Commands)
                {
                    if (!parsed.Commands.ContainsKey(defaultKey))
                    {
                        parsed.Commands[defaultKey] = defaultDef;
                        needsPersist = true;
                        _logger.LogInformation("Added missing built-in command '{Key}' to commands.json", defaultKey);
                    }
                }

                if (parsed.RoleProfiles == null || parsed.RoleProfiles.Count == 0)
                {
                    parsed.RoleProfiles = defaults.RoleProfiles;
                    needsPersist = true;
                }
                else
                {
                    foreach (var (roleKey, roleProfile) in defaults.RoleProfiles)
                    {
                        if (!parsed.RoleProfiles.ContainsKey(roleKey))
                        {
                            parsed.RoleProfiles[roleKey] = roleProfile;
                            needsPersist = true;
                        }
                    }
                }

                if (needsPersist)
                {
                    try
                    {
                        var mergedJson = JsonSerializer.Serialize(parsed, JsonOptions);
                        File.WriteAllText(_configFilePath, mergedJson);
                        _logger.LogInformation("Saved merged commands.json at {Path}", _configFilePath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not persist merged commands.json to {Path}", _configFilePath);
                    }
                }

                ApplyConfiguration(parsed, logSuccess);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Syntax error in commands.json. Retaining previous configuration.");
                return false;
            }
        }
    }

    private void ApplyConfiguration(CommandsConfiguration config, bool logSuccess)
    {
        // Older editors deleted the key when a reply was removed. Keep that intent
        // explicit so fallback messages cannot resurrect it and the UI can edit it again.
        var defaults = CommandsConfiguration.CreateDefault();
        foreach (var (key, command) in config.Commands)
            if (defaults.Commands.TryGetValue(key, out var builtIn))
                foreach (var templateKey in builtIn.Templates.Keys)
                    command.Templates.TryAdd(templateKey, templateKey is "queue_full" or "user_limit" or "spotify_not_connected" or "youtube_playlist_added" or "spotify_album_added" or "spotify_playlist_added" or "youtube_disabled" or "spotify_disabled" or "invalid_link" or "playlist_disabled" or "service_error" or "queue_error" or "playlist_empty" ? builtIn.Templates[templateKey] : "");
        // Validate unknown placeholders
        var warnings = CommandTemplateEngine.FindUnknownPlaceholders(config);
        foreach (var (cmd, tmplKey, placeholder) in warnings)
        {
            _logger.LogWarning("commands.json warning: Unknown placeholder '{Placeholder}' in command '{Command}' ({TemplateKey})",
                placeholder, cmd, tmplKey);
        }

        var newAliasMap = new Dictionary<string, (string CommandKey, CommandDefinition Definition)>(StringComparer.OrdinalIgnoreCase);
        var newCustomAliasMap = new Dictionary<string, CustomCommandDefinition>(StringComparer.OrdinalIgnoreCase);

        // Check for alias conflicts
        var conflicts = CheckAliasConflicts(config);
        foreach (var c in conflicts)
        {
            _logger.LogWarning("Command alias conflict: {Conflict}", c);
        }

        foreach (var (key, def) in config.Commands)
        {
            if (string.IsNullOrWhiteSpace(def.Name))
                def.Name = key;

            // Register primary name as alias
            newAliasMap[def.Name.TrimStart('!')] = (key, def);

            // Register all aliases
            if (def.Aliases != null)
            {
                foreach (var alias in def.Aliases)
                {
                    var clean = alias.Trim().TrimStart('!');
                    if (!string.IsNullOrEmpty(clean))
                        newAliasMap[clean] = (key, def);
                }
            }
        }

        if (config.CustomCommands != null)
        {
            foreach (var (key, customDef) in config.CustomCommands)
            {
                if (string.IsNullOrWhiteSpace(customDef.Name))
                    customDef.Name = key;

                newCustomAliasMap[customDef.Name.TrimStart('!')] = customDef;
                if (customDef.Aliases != null)
                {
                    foreach (var a in customDef.Aliases)
                    {
                        var clean = a.Trim().TrimStart('!');
                        if (!string.IsNullOrEmpty(clean))
                            newCustomAliasMap[clean] = customDef;
                    }
                }
            }
        }

        if (config.RoleProfiles == null || config.RoleProfiles.Count == 0)
        {
            config.RoleProfiles = CommandsConfiguration.CreateDefault().RoleProfiles;
        }

        _currentConfig = config;
        _aliasMap = newAliasMap;
        _customAliasMap = newCustomAliasMap;

        if (logSuccess)
        {
            _logger.LogInformation("commands.json loaded successfully with {Count} commands, {CustomCount} custom commands and {Aliases} aliases.",
                config.Commands.Count, config.CustomCommands?.Count ?? 0, newAliasMap.Count + newCustomAliasMap.Count);
        }

        OnConfigurationChanged?.Invoke(config);
    }

    public List<string> CheckAliasConflicts(CommandsConfiguration config)
    {
        var conflicts = new List<string>();
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Register(string alias, string cmdName)
        {
            if (string.IsNullOrWhiteSpace(alias)) return;
            var clean = alias.Trim().TrimStart('!');
            if (seen.TryGetValue(clean, out var existingCmd))
            {
                if (!existingCmd.Equals(cmdName, StringComparison.OrdinalIgnoreCase))
                    conflicts.Add($"'{clean}' takma adı hem '{existingCmd}' hem '{cmdName}' komutunda tanımlı.");
            }
            else
            {
                seen[clean] = cmdName;
            }
        }

        if (config.Commands != null)
        {
            foreach (var (key, def) in config.Commands)
            {
                Register(def.Name, def.Name);
                if (def.Aliases != null)
                {
                    foreach (var a in def.Aliases) Register(a, def.Name);
                }
            }
        }

        if (config.CustomCommands != null)
        {
            foreach (var (key, def) in config.CustomCommands)
            {
                Register(def.Name, def.Name);
                if (def.Aliases != null)
                {
                    foreach (var a in def.Aliases) Register(a, def.Name);
                }
            }
        }

        return conflicts;
    }

    internal void RestoreInMemory(CommandsConfiguration configuration)
    {
        lock (_lock) ApplyConfiguration(configuration, logSuccess: false);
    }

    public async Task<bool> SaveConfigurationAsync(CommandsConfiguration config)
    {
        await _writeLock.WaitAsync();
        try
        {
            var json = JsonSerializer.Serialize(config, JsonOptions);
            var tempPath = _configFilePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, json);
            lock (_lock)
            {
                File.Move(tempPath, _configFilePath, overwrite: true);
                ApplyConfiguration(config, logSuccess: true);
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save commands.json to {Path}", _configFilePath);
            return false;
        }
        finally { _writeLock.Release(); }
    }

    public bool TryResolveCommand(string alias, out string commandKey, out CommandDefinition? definition)
    {
        lock (_lock)
        {
            var clean = alias.TrimStart('!', '.', '/').Trim().ToLowerInvariant();
            if (clean is "geçmiş" or "gecmis" or "history")
            {
                if (_currentConfig.Commands.TryGetValue("gecmis", out var history) || _currentConfig.Commands.TryGetValue("history", out history))
                {
                    commandKey = _currentConfig.Commands.ContainsKey("gecmis") ? "gecmis" : "history";
                    definition = history;
                    return true;
                }
            }
            if (_aliasMap.TryGetValue(clean, out var match))
            {
                commandKey = match.CommandKey;
                definition = match.Definition;
                return true;
            }

            // Fallback for ubiquitous Turkish & international aliases if not overridden
            if (clean is "sr" or "istek" or "ekle" or "play" or "songrequest")
            {
                if (_currentConfig?.Commands?.TryGetValue("play", out var playDef) == true)
                {
                    commandKey = "play";
                    definition = playDef;
                    return true;
                }
            }
            if (clean is "volume" or "vol" or "ses")
            {
                if (_currentConfig?.Commands?.TryGetValue("volume", out var volDef) == true)
                {
                    commandKey = "volume";
                    definition = volDef;
                    return true;
                }
            }
            if (clean is "song" or "current" or "np" or "calan" or "sarki")
            {
                if (_currentConfig?.Commands?.TryGetValue("song", out var songDef) == true)
                {
                    commandKey = "song";
                    definition = songDef;
                    return true;
                }
            }
            if (clean is "queue" or "liste" or "kuyruk" or "q" or "siradaki")
            {
                if (_currentConfig?.Commands?.TryGetValue("queue", out var qDef) == true)
                {
                    commandKey = "queue";
                    definition = qDef;
                    return true;
                }
            }
            if (clean is "skip" or "gec" or "atla" or "next")
            {
                if (_currentConfig?.Commands?.TryGetValue("skip", out var skipDef) == true)
                {
                    commandKey = "skip";
                    definition = skipDef;
                    return true;
                }
            }

            commandKey = string.Empty;
            definition = null;
            return false;
        }
    }

    public bool TryResolveCustomCommand(string alias, out CustomCommandDefinition? customDefinition)
    {
        lock (_lock)
        {
            var clean = alias.TrimStart('!', '.', '/');
            return _customAliasMap.TryGetValue(clean, out customDefinition);
        }
    }

    public bool CheckCooldown(string commandKey, string username, int cooldownSeconds, out int remainingSeconds)
    {
        if (cooldownSeconds <= 0)
        {
            remainingSeconds = 0;
            return true; // No cooldown
        }

        var key = $"{commandKey.ToLowerInvariant()}:{username.ToLowerInvariant()}";
        var now = DateTime.UtcNow;

        if (_cooldowns.TryGetValue(key, out var lastUsed))
        {
            var elapsed = now - lastUsed;
            var cooldownSpan = TimeSpan.FromSeconds(cooldownSeconds);

            if (elapsed < cooldownSpan)
            {
                remainingSeconds = (int)Math.Ceiling((cooldownSpan - elapsed).TotalSeconds);
                return false; // Still on cooldown
            }
        }

        remainingSeconds = 0;
        return true;
    }

    public void RecordSuccessfulCommand(string commandKey, string username) =>
        _cooldowns[$"{commandKey.ToLowerInvariant()}:{username.ToLowerInvariant()}"] = DateTime.UtcNow;

    public bool CheckCooldownWithRole(
        string commandKey,
        string username,
        int baseCooldownSeconds,
        Dictionary<string, int>? roleCooldowns,
        List<string>? exemptRoles,
        IReadOnlyCollection<string>? userBadges,
        string? channel,
        out int remainingSeconds,
        string? botUsername = null)
    {
        var userRole = GetUserHighestRole(userBadges, username, channel, botUsername);

        // An explicit matrix value takes precedence over inherited exemptions.
        if (roleCooldowns != null && roleCooldowns.TryGetValue(userRole, out var explicitSeconds))
            return CheckCooldown(commandKey, username, explicitSeconds, out remainingSeconds);

        // Check if role is exempt from cooldown
        if (exemptRoles != null && exemptRoles.Any(r => r.Equals(userRole, StringComparison.OrdinalIgnoreCase)))
        {
            remainingSeconds = 0;
            return true;
        }

        // Determine effective cooldown seconds
        int effectiveCooldown = baseCooldownSeconds;
        if (roleCooldowns != null && roleCooldowns.TryGetValue(userRole, out var roleCd))
        {
            effectiveCooldown = roleCd;
        }

        return CheckCooldown(commandKey, username, effectiveCooldown, out remainingSeconds);
    }

    public RoleProfile GetRoleProfile(IReadOnlyCollection<string>? badges, string? username, string? channel, string? botUsername = null)
    {
        var highest = GetUserHighestRole(badges, username, channel, botUsername);
        if (Current.RoleProfiles != null && Current.RoleProfiles.TryGetValue(highest, out var profile))
        {
            return profile;
        }

        return new RoleProfile
        {
            Role = highest,
            Name = highest switch
            {
                "broadcaster" => "Yayıncı",
                "moderator" => "Moderatör",
                "vip" => "VIP",
                "subscriber" => "Abone",
                _ => "İzleyici / Herkes"
            },
            CanRequestSongs = true,
            CooldownSeconds = highest is "broadcaster" or "moderator" ? 0 : 30,
            MaxSongsInQueue = highest == "broadcaster" ? 50 : (highest == "moderator" ? 10 : 3),
            BypassCooldown = highest is "broadcaster" or "moderator",
            BypassLimits = highest == "broadcaster"
        };
    }

    public static string GetUserHighestRole(IReadOnlyCollection<string>? userBadges, string? username, string? channel, string? botUsername = null)
    {
        var cleanUser = username?.TrimStart('@');
        var cleanChannel = channel?.TrimStart('@');
        var cleanBot = botUsername?.TrimStart('@');

        var isBroadcaster = (!string.IsNullOrEmpty(cleanUser) && (
                                (!string.IsNullOrEmpty(cleanChannel) && string.Equals(cleanUser, cleanChannel, StringComparison.OrdinalIgnoreCase)) ||
                                (!string.IsNullOrEmpty(cleanBot) && string.Equals(cleanUser, cleanBot, StringComparison.OrdinalIgnoreCase))
                             ))
                            || (userBadges != null && userBadges.Any(b =>
                                b.Equals("broadcaster", StringComparison.OrdinalIgnoreCase) ||
                                b.Equals("streamer", StringComparison.OrdinalIgnoreCase) ||
                                b.Equals("channel_owner", StringComparison.OrdinalIgnoreCase)));
        if (isBroadcaster) return "broadcaster";

        var badges = userBadges?.Select(b => b.ToLowerInvariant()).ToHashSet() ?? [];
        if (badges.Contains("moderator")) return "moderator";
        if (badges.Contains("vip") || badges.Contains("og")) return "vip";
        if (badges.Contains("subscriber") || badges.Contains("sub") || badges.Contains("founder")) return "subscriber";
        return "everyone";
    }

    public static bool IsCommandAllowed(string? requiredRole, List<string>? allowedRoles,
        IReadOnlyCollection<string>? badges, string? username, string? channel, string? botUsername = null)
    {
        if (allowedRoles == null) return IsRoleAllowed(requiredRole, badges, username, channel, botUsername);
        var role = GetUserHighestRole(badges, username, channel, botUsername);
        return allowedRoles.Contains(role, StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsRoleAllowed(string? requiredRole, IReadOnlyCollection<string>? userBadges, string? username, string? channel, string? botUsername = null)
    {
        if (string.IsNullOrWhiteSpace(requiredRole) ||
            requiredRole.Equals("everyone", StringComparison.OrdinalIgnoreCase) ||
            requiredRole.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var cleanUser = username?.TrimStart('@');
        var cleanChannel = channel?.TrimStart('@');
        var cleanBot = botUsername?.TrimStart('@');

        // Broadcaster / Streamer / Bot account has full access to all commands
        var isBroadcaster = (!string.IsNullOrEmpty(cleanUser) && (
                                (!string.IsNullOrEmpty(cleanChannel) && string.Equals(cleanUser, cleanChannel, StringComparison.OrdinalIgnoreCase)) ||
                                (!string.IsNullOrEmpty(cleanBot) && string.Equals(cleanUser, cleanBot, StringComparison.OrdinalIgnoreCase))
                             ))
                            || (userBadges != null && userBadges.Any(b =>
                                b.Equals("broadcaster", StringComparison.OrdinalIgnoreCase) ||
                                b.Equals("streamer", StringComparison.OrdinalIgnoreCase) ||
                                b.Equals("channel_owner", StringComparison.OrdinalIgnoreCase)));

        if (isBroadcaster) return true;

        var badges = userBadges?.Select(b => b.ToLowerInvariant()).ToHashSet() ?? [];

        var req = requiredRole.ToLowerInvariant();
        return req switch
        {
            "moderator" or "mod" => badges.Contains("moderator"),
            "subscriber" or "sub" => badges.Contains("subscriber") || badges.Contains("founder") || badges.Contains("sub") || badges.Contains("moderator"),
            "vip" => badges.Contains("vip") || badges.Contains("og") || badges.Contains("moderator"),
            "broadcaster" or "owner" or "streamer" => false, // Non-broadcaster reached here
            _ => true
        };
    }

    public void Dispose()
    {
        _reloadDebounceTimer.Dispose();
        _watcher?.Dispose();
    }
}
