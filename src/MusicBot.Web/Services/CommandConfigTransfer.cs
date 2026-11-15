using System.Text.Json;
using MusicBot.Core.Models;

namespace MusicBot.Services;

public static class CommandConfigTransfer
{
    private static readonly HashSet<string> Roles = new(StringComparer.OrdinalIgnoreCase)
        { "broadcaster", "moderator", "vip", "subscriber", "everyone", "all", "og" };

    public static CommandsConfiguration Read(JsonElement document)
    {
        if (document.ValueKind != JsonValueKind.Object) throw new ArgumentException("Komut dosyası bir JSON nesnesi olmalı.");
        if (document.TryGetProperty("format", out var format))
        {
            if (format.GetString() != "musick.commands" || !document.TryGetProperty("version", out var version)
                || !version.TryGetInt32(out var number) || number != 1)
                throw new ArgumentException("Komut dosyasının biçimi veya sürümü desteklenmiyor.");
            if (!document.TryGetProperty("configuration", out document)) throw new ArgumentException("Komut yapılandırması bulunamadı.");
        }
        var config = document.Deserialize<CommandsConfiguration>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new ArgumentException("Komut yapılandırması okunamadı.");
        Validate(config);
        // Older exports may predate newly added built-in commands or role profiles.
        var defaults = CommandsConfiguration.CreateDefault();
        foreach (var entry in defaults.Commands) config.Commands.TryAdd(entry.Key, entry.Value);
        foreach (var entry in defaults.RoleProfiles) config.RoleProfiles.TryAdd(entry.Key, entry.Value);
        return config;
    }

    public static void Validate(CommandsConfiguration config)
    {
        if (config.Commands is not { Count: > 0 and <= 100 } || config.CustomCommands == null
            || config.CustomCommands.Count > 500 || config.RoleProfiles == null || config.RoleProfiles.Count > 10)
            throw new ArgumentException("Geçersiz komut veya rol listesi.");
        foreach (var (key, command) in config.Commands)
        {
            if (command == null) throw new ArgumentException("Boş komut kaydı.");
            Check(key, command.Name, command.Aliases, command.RequiredRole, command.AllowedRoles,
                command.CooldownSeconds, command.RoleCooldowns, command.ExemptRoles);
            if (command.MaxPerUser < 0 || command.Templates == null || command.Templates.Count > 100
                || command.Templates.Any(t => t.Key.Length > 100 || t.Value == null || t.Value.Length > 10000))
                throw new ArgumentException("Geçersiz komut limiti veya yanıt şablonu.");
        }
        foreach (var (key, command) in config.CustomCommands)
        {
            if (command == null) throw new ArgumentException("Boş özel komut kaydı.");
            Check(key, command.Name, command.Aliases, command.RequiredRole, command.AllowedRoles,
                command.CooldownSeconds, command.RoleCooldowns, command.ExemptRoles);
            if (command.ResponseText == null || command.ResponseText.Length > 10000)
                throw new ArgumentException("Özel komut yanıtı çok uzun veya eksik.");
        }
        foreach (var (key, role) in config.RoleProfiles)
            if (!Roles.Contains(key) || role == null || !Roles.Contains(role.Role) || role.Name == null
                || role.Name.Length > 100 || role.CooldownSeconds < 0 || role.MaxSongsInQueue < 0)
                throw new ArgumentException("Geçersiz rol profili.");
    }

    private static void Check(string key, string name, List<string> aliases, string role, List<string>? allowed,
        int cooldown, Dictionary<string, int> roleCooldowns, List<string> exempt)
    {
        static bool ValidName(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 100
            && !value.Any(char.IsWhiteSpace);
        if (!ValidName(key) || !ValidName(name) || aliases == null || aliases.Count > 100 || aliases.Any(a => !ValidName(a))
            || !Roles.Contains(role ?? "") || cooldown < 0 || roleCooldowns == null || exempt == null
            || roleCooldowns.Any(r => !Roles.Contains(r.Key) || r.Value < 0)
            || exempt.Any(r => r == null || !Roles.Contains(r))
            || allowed?.Any(r => r == null || !Roles.Contains(r)) == true)
            throw new ArgumentException("Geçersiz komut adı, rol veya bekleme süresi.");
    }
}
