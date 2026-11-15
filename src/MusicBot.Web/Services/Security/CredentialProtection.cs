using System.Text.Json.Nodes;
namespace MusicBot.Services.Security;
public static class CredentialProtection
{
    public static void MigrateFile(string path)
    {
        if (!File.Exists(path)) return;
        var json = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidOperationException("Ayar dosyası okunamadı.");
        var changed = false;
        foreach (var section in new[] { "Spotify", "Kick", "Google" })
        {
            var value = json[section]?["ClientSecret"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith(TokenProtection.EncryptedPrefix)) continue;
            json[section]!["ClientSecret"] = TokenProtection.Protect(value); changed = true;
        }
        if (!changed) return;
        var temporary = path + ".secure.tmp"; File.WriteAllText(temporary, json.ToJsonString(new() { WriteIndented = true })); File.Move(temporary, path, true);
    }
}
