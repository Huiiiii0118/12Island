using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using TwelveIsland.Core;

namespace TwelveIsland.Native;

internal static class ClaudeDesktopReader
{
    public static ClaudeDesktopState Read(DataLocations locations)
    {
        var profiles = locations.ClaudeDesktopProfiles();
        bool failed = false;
        foreach (var profile in profiles)
        {
            try
            {
                var configPath = Path.Combine(profile, "config.json");
                if (!File.Exists(configPath)) continue;
                using var config = TwelveIsland.Core.SharedFile.Parse(configPath);
                var root = config.RootElement;
                // V2 is authoritative, including cleared entries after sign-out.
                var encrypted = root.TryGetProperty("oauth:tokenCacheV2", out var v2)
                    ? v2.Text() : root.Get("oauth:tokenCache").Text();
                if (encrypted.Length == 0) continue;
                var key = ChromiumSecrets.Key(Path.Combine(profile, "Local State"));
                byte[]? plain = null;
                try
                {
                    plain = ChromiumSecrets.Decrypt(key, encrypted);
                    using var cache = JsonDocument.Parse(plain);
                    var credentials = ClaudeDesktopCache.Parse(cache.RootElement, root.Get("lastKnownAccountUuid").Text(), DateTimeOffset.UtcNow);
                    if (credentials.Count > 0) return new(true, credentials);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(key);
                    if (plain is not null) CryptographicOperations.ZeroMemory(plain);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or CryptographicException)
            { failed = true; }
        }
        return new(profiles.Count > 0, [], failed);
    }
}
