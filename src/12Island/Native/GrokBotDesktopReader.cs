using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TwelveIsland.Core;

namespace TwelveIsland.Native;

// The Grok Bot desktop app keeps its sign-ins in %APPDATA%\Grok Bot\sand-secrets.json: "cursor-accounts" is a JSON
// string naming the active account, and each account's tokens are encrypted with the app's own os_crypt key. Only the
// active account's access token is read; the refresh token is never touched.
internal static class GrokBotDesktopReader
{
    public static GrokBotSignIn Read(DataLocations locations)
    {
        var folder = GrokBotUsage.Folder(locations.Roaming);
        var secrets = Path.Combine(folder, "sand-secrets.json");
        if (!File.Exists(secrets)) return new(Directory.Exists(folder), "");
        try
        {
            string accountsText;
            using (var file = SharedFile.Parse(secrets)) accountsText = file.RootElement.Get("cursor-accounts").Text();
            if (accountsText.Length == 0) return new(true, "");
            string encrypted;
            using (var accounts = JsonDocument.Parse(accountsText))
            {
                var root = accounts.RootElement;
                encrypted = root.Get("accounts").Get(root.Get("active").Text()).Get("cursor-access-token").Text();
            }
            if (encrypted.Length == 0) return new(true, "");
            var key = ChromiumSecrets.Key(Path.Combine(folder, "Local State"));
            byte[]? plain = null;
            try
            {
                plain = ChromiumSecrets.Decrypt(key, encrypted);
                return new(true, Encoding.UTF8.GetString(plain).Trim());
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                if (plain is not null) CryptographicOperations.ZeroMemory(plain);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or CryptographicException or ArgumentException)
        { return new(true, "", true); }
    }
}
