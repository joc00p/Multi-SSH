using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
            // The file exists but couldn't be parsed. Preserve it (never discard
            // silently) so it can be recovered, then start empty.
            try { if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".corrupt", overwrite: true); }
            catch { /* best-effort */ }
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

        // Safety net: if this save would shrink the list, snapshot the current file to a
        // dated backup first. The single sessions.json.bak only holds one generation, so
        // two saves after an unexpected drop erase it — a dated copy is always recoverable.
        BackupIfShrinking(toWrite.Count);

        // Atomic write: serialize to a temp file, then replace. File.Replace swaps
        // it in atomically and keeps the previous version as sessions.json.bak, so
        // a crash mid-write can never truncate or lose the real file.
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(FilePath))
            File.Replace(tmp, FilePath, FilePath + ".bak");
        else
            File.Move(tmp, FilePath);
    }

    private static readonly Regex DatedBackup = new(@"^sessions\.\d{8}-\d{6}\.bak$", RegexOptions.Compiled);

    /// <summary>
    /// If the new list has fewer entries than what is currently on disk, copy the current
    /// file to sessions.{yyyyMMdd-HHmmss}.bak before overwriting it. Best-effort — a backup
    /// failure must never block the save. Keeps the newest few dated backups.
    /// </summary>
    private static void BackupIfShrinking(int newCount)
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var existing = JsonSerializer.Deserialize<List<SessionConfig>>(File.ReadAllText(FilePath));
            int oldCount = existing?.Count ?? 0;
            if (newCount >= oldCount) return;   // same size or growing — nothing to protect

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Copy(FilePath, Path.Combine(Dir, $"sessions.{stamp}.bak"), overwrite: true);

            // Prune to the 10 most recent dated backups so they can't accumulate forever.
            var stale = Directory.GetFiles(Dir, "sessions.*.bak")
                .Where(f => DatedBackup.IsMatch(Path.GetFileName(f)))
                .OrderByDescending(f => f)
                .Skip(10)
                .ToList();
            foreach (var f in stale) File.Delete(f);
        }
        catch { /* backups are best-effort; never let one break a save */ }
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
            // DPAPI is unavailable (e.g. FIPS policy). Never fall back to writing the
            // secret in cleartext — drop it instead so it can't leak to disk. The
            // session is still saved; the user simply re-enters the password next time.
            return null;
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
