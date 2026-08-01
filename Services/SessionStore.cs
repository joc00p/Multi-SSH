using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MultiSSH.Models;

namespace MultiSSH.Services;

/// <summary>
/// Persists saved sessions to %AppData%\Multi-SSH\sessions.json.
/// Passwords/passphrases are encrypted per-user with Windows DPAPI so the
/// on-disk file is not readable by other accounts.
/// </summary>
public static class SessionStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Multi-SSH");
    private static readonly string FilePath = Path.Combine(Dir, "sessions.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };

    public static List<SessionConfig> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var json = File.ReadAllText(FilePath);
            var list = JsonSerializer.Deserialize<List<SessionConfig>>(json, JsonOpts) ?? new();
            foreach (var s in list)
            {
                s.Password = Unprotect(s.Password);
                s.KeyPassphrase = Unprotect(s.KeyPassphrase);
            }
            return list;
        }
        catch
        {
            return new();
        }
    }

    public static void Save(IEnumerable<SessionConfig> sessions)
    {
        Directory.CreateDirectory(Dir);
        // Encrypt secrets on a copy so in-memory configs keep plaintext.
        var toWrite = new List<SessionConfig>();
        foreach (var s in sessions)
        {
            var c = s.Clone();
            c.Password = Protect(c.Password);
            c.KeyPassphrase = Protect(c.KeyPassphrase);
            toWrite.Add(c);
        }
        var json = JsonSerializer.Serialize(toWrite, JsonOpts);
        File.WriteAllText(FilePath, json);
    }

    private const string Marker = "dpapi:";

    private static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return plain;
        try
        {
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
            return Marker + Convert.ToBase64String(bytes);
        }
        catch
        {
            return plain;
        }
    }

    private static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith(Marker)) return stored;
        try
        {
            var bytes = Convert.FromBase64String(stored.Substring(Marker.Length));
            var plain = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }
}
