using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TwelveIsland.Native;

// The mailbox password, kept for the signed-in user only (DPAPI): the settings file holds the address, never the password.
internal static class MailSecret
{
    private static string _path = "";
    public static void Use(string folder) => _path = Path.Combine(folder, "mail.secret");

    public static string Read()
    {
        try
        {
            if (_path.Length == 0 || !File.Exists(_path)) return "";
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), null, DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or FormatException) { return ""; }
    }

    public static void Write(string secret)
    {
        try
        {
            if (_path.Length == 0) return;
            if (secret.Length == 0) { if (File.Exists(_path)) File.Delete(_path); return; }
            var plain = Encoding.UTF8.GetBytes(secret);
            try { File.WriteAllBytes(_path, ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser)); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException) { }
    }

    public static bool Has => _path.Length > 0 && File.Exists(_path);
}
