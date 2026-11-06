using System.IO;
using System.Security.Cryptography;
using TwelveIsland.Core;

namespace TwelveIsland.Native;

// Electron desktop apps (Claude, Grok Bot) store secrets with Chromium's os_crypt: the AES key sits in the profile's
// Local State, wrapped with DPAPI for the current user, and each value is "v10", a 12-byte nonce, the AES-256-GCM
// ciphertext and a 16-byte tag. Callers clear both the key and the decrypted bytes when done.
internal static class ChromiumSecrets
{
    public static byte[] Key(string localState)
    {
        using var state = SharedFile.Parse(localState);
        var wrappedKey = Convert.FromBase64String(state.RootElement.Get("os_crypt").Get("encrypted_key").Text());
        if (!wrappedKey.AsSpan().StartsWith("DPAPI"u8)) throw new CryptographicException();
        return ProtectedData.Unprotect(wrappedKey.AsSpan(5).ToArray(), null, DataProtectionScope.CurrentUser);
    }

    public static byte[] Decrypt(byte[] key, string base64)
    {
        var data = Convert.FromBase64String(base64);
        if (data.Length < 31 || !data.AsSpan().StartsWith("v10"u8)) throw new CryptographicException();
        var plain = new byte[data.Length - 31];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(data.AsSpan(3, 12), data.AsSpan(15, plain.Length), data.AsSpan(data.Length - 16), plain);
        return plain;
    }
}
