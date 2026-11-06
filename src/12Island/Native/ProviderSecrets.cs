using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TwelveIsland.Core;

namespace TwelveIsland.Native;

public sealed class ProviderSecrets(string directory) : IProviderSecrets
{
    private readonly object _gate = new();
    private string FilePath => Path.Combine(directory, "secrets.dat");
    private Dictionary<Guid, string> Load() => File.Exists(FilePath)
        ? JsonSerializer.Deserialize<Dictionary<Guid, string>>(File.ReadAllBytes(FilePath)) ?? [] : [];
    public string Read(Guid instanceId)
    {
        lock (_gate)
        {
            if (!Load().TryGetValue(instanceId, out var encrypted)) return "";
            var plain = ProtectedData.Unprotect(Convert.FromBase64String(encrypted), null, DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
    }
    public void Write(Guid instanceId, string secret)
    {
        lock (_gate)
        {
            var values = Load();
            if (secret.Length == 0) values.Remove(instanceId);
            else
            {
                var plain = Encoding.UTF8.GetBytes(secret);
                try { values[instanceId] = Convert.ToBase64String(ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser)); }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
            Directory.CreateDirectory(directory);
            var temp = FilePath + ".tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(values)); File.Move(temp, FilePath, true);
        }
    }
    public void Delete(Guid instanceId) => Write(instanceId, "");
}
