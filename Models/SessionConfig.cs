using System.Text.Json.Serialization;

namespace MultiSSH.Models;

public enum AuthMethod
{
    Password,
    PublicKey,
    Agent,
    KeyboardInteractive
}

/// <summary>
/// A saved / active session profile. Mirrors the common set of options
/// exposed by PuTTY's configuration dialog.
/// </summary>
public class SessionConfig
{
    // --- Session ---
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "";

    /// <summary>Folder this connection lives in, e.g. "Client A/Prod". "" = top level.
    /// Uses '/' as the separator. Old sessions load with "" (root), so nothing is lost.</summary>
    public string FolderPath { get; set; } = "";

    // --- Connection / Authentication ---
    public AuthMethod Auth { get; set; } = AuthMethod.Password;

    /// <summary>Optional password. Stored obfuscated on disk (not secure — see note in SessionStore).</summary>
    public string? Password { get; set; }

    /// <summary>Path to a private key file (OpenSSH or PuTTY .ppk converted).</summary>
    public string? PrivateKeyPath { get; set; }

    /// <summary>Optional passphrase for the private key.</summary>
    public string? KeyPassphrase { get; set; }

    // --- Connection tuning ---
    public int KeepAliveSeconds { get; set; } = 30;
    public int ConnectTimeoutSeconds { get; set; } = 15;
    public bool TcpNoDelay { get; set; } = true;

    // --- Terminal ---
    public string TerminalType { get; set; } = "xterm-256color";
    public int Columns { get; set; } = 80;
    public int Rows { get; set; } = 24;

    // --- Appearance ---
    public string FontFamily { get; set; } = "Consolas";
    public double FontSize { get; set; } = 14;
    public string ColorScheme { get; set; } = "Campbell";
    public int ScrollbackLines { get; set; } = 2000;

    // --- Behaviour ---
    public bool BellEnabled { get; set; } = true;
    /// <summary>PuTTY-style: selecting text copies, right-click pastes.</summary>
    public bool CopyOnSelect { get; set; } = true;
    public bool PasteOnRightClick { get; set; } = true;

    [JsonIgnore]
    public string Display => string.IsNullOrWhiteSpace(Name)
        ? (string.IsNullOrWhiteSpace(Host) ? "(new session)" : $"{Username}@{Host}")
        : Name;

    public SessionConfig Clone()
    {
        return (SessionConfig)MemberwiseClone();
    }
}
