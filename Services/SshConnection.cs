using System.IO;
using System.Reflection;
using MultiSSH.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace MultiSSH.Services;

/// <summary>
/// Wraps an SSH.NET <see cref="SshClient"/> and an interactive shell channel.
/// Raises <see cref="DataReceived"/> as bytes arrive; write user keystrokes
/// with <see cref="Send"/>.
/// </summary>
public class SshConnection : ITerminalBackend
{
    private readonly SessionConfig _cfg;
    private SshClient? _client;
    private ShellStream? _shell;
    private Thread? _readThread;
    private volatile bool _disposed;

    public event Action<byte[]>? DataReceived;
    public event Action<string>? StatusChanged;
    public event Action<string>? Closed;
    /// <summary>Raised once when the remote shell ends (EOF on the channel).</summary>
    public event Action? ShellExited;

    public bool IsConnected => _client?.IsConnected ?? false;

    public SshConnection(SessionConfig cfg) => _cfg = cfg;

    public async Task ConnectAsync(int cols, int rows)
    {
        StatusChanged?.Invoke($"Connecting to {_cfg.Host}:{_cfg.Port} …");

        var info = BuildConnectionInfo();
        _client = new SshClient(info);
        _client.KeepAliveInterval = _cfg.KeepAliveSeconds > 0
            ? TimeSpan.FromSeconds(_cfg.KeepAliveSeconds)
            : Timeout.InfiniteTimeSpan;
        _client.ErrorOccurred += (_, e) => StatusChanged?.Invoke("Error: " + e.Exception.Message);

        await Task.Run(() => _client.Connect());

        StatusChanged?.Invoke($"Connected — {_cfg.Username}@{_cfg.Host}");

        var modes = new Dictionary<Renci.SshNet.Common.TerminalModes, uint>();
        _shell = _client.CreateShellStream(
            _cfg.TerminalType,
            (uint)cols, (uint)rows,
            (uint)(cols * 8), (uint)(rows * 16),
            8192, modes);

        _shell.ErrorOccurred += (_, e) => StatusChanged?.Invoke("Shell error: " + e.Exception.Message);

        // Read on a background thread: a blocking Read returns 0 at EOF when the
        // remote shell exits, which lets us tear the window down automatically.
        _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "ssh-read" };
        _readThread.Start();
    }

    private void ReadLoop()
    {
        var buf = new byte[8192];
        var shell = _shell;
        try
        {
            while (!_disposed && shell != null)
            {
                int n = shell.Read(buf, 0, buf.Length);
                if (n <= 0) break; // EOF — the shell has exited
                var slice = new byte[n];
                Array.Copy(buf, slice, n);
                DataReceived?.Invoke(slice);
            }
        }
        catch
        {
            // Stream disposed or connection dropped — treated the same as EOF.
        }
        finally
        {
            if (!_disposed)
            {
                StatusChanged?.Invoke("Shell closed");
                ShellExited?.Invoke();
            }
        }
    }

    private ConnectionInfo BuildConnectionInfo()
    {
        var methods = new List<AuthenticationMethod>();

        switch (_cfg.Auth)
        {
            case AuthMethod.PublicKey:
                if (string.IsNullOrWhiteSpace(_cfg.PrivateKeyPath) || !File.Exists(_cfg.PrivateKeyPath))
                    throw new FileNotFoundException("Private key file not found", _cfg.PrivateKeyPath);
                PrivateKeyFile keyFile;
                try
                {
                    keyFile = string.IsNullOrEmpty(_cfg.KeyPassphrase)
                        ? new PrivateKeyFile(_cfg.PrivateKeyPath)
                        : new PrivateKeyFile(_cfg.PrivateKeyPath, _cfg.KeyPassphrase);
                }
                catch (Exception ex) when (IsPassphraseProblem(ex))
                {
                    throw new KeyPassphraseRequiredException(
                        string.IsNullOrEmpty(_cfg.KeyPassphrase)
                            ? "The private key is encrypted and needs a passphrase."
                            : "Incorrect passphrase for the private key.", ex);
                }
                methods.Add(new PrivateKeyAuthenticationMethod(_cfg.Username, keyFile));
                break;

            case AuthMethod.KeyboardInteractive:
            {
                var ki = new KeyboardInteractiveAuthenticationMethod(_cfg.Username);
                ki.AuthenticationPrompt += (_, e) =>
                {
                    foreach (var prompt in e.Prompts)
                        prompt.Response = _cfg.Password ?? "";
                };
                methods.Add(ki);
                break;
            }

            case AuthMethod.Agent:
                // SSH.NET has no built-in agent transport; fall back to password if supplied.
                methods.Add(new PasswordAuthenticationMethod(_cfg.Username, _cfg.Password ?? ""));
                break;

            default: // Password
                methods.Add(new PasswordAuthenticationMethod(_cfg.Username, _cfg.Password ?? ""));
                // Also allow keyboard-interactive with the same password (common on Linux).
                var kip = new KeyboardInteractiveAuthenticationMethod(_cfg.Username);
                kip.AuthenticationPrompt += (_, e) =>
                {
                    foreach (var prompt in e.Prompts)
                        prompt.Response = _cfg.Password ?? "";
                };
                methods.Add(kip);
                break;
        }

        var info = new ConnectionInfo(_cfg.Host, _cfg.Port, _cfg.Username, methods.ToArray())
        {
            Timeout = TimeSpan.FromSeconds(_cfg.ConnectTimeoutSeconds),
        };
        return info;
    }

    private static bool IsPassphraseProblem(Exception ex)
    {
        if (ex is SshPassPhraseNullOrEmptyException) return true;
        if (ex is System.Security.Cryptography.CryptographicException) return true;
        var m = ex.Message?.ToLowerInvariant() ?? "";
        return m.Contains("passphrase") || m.Contains("pass phrase")
            || m.Contains("invalid pad") || m.Contains("decrypt")
            || m.Contains("bad data");
    }

    public void Send(byte[] data)
    {
        if (_shell == null) return;
        try
        {
            _shell.Write(data, 0, data.Length);
            _shell.Flush();
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke("Send failed: " + ex.Message);
        }
    }

    public void Send(string text) => Send(System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// Best-effort PTY resize. SSH.NET doesn't expose window-change on ShellStream
    /// publicly, so we reach the underlying channel via reflection.
    /// </summary>
    public void Resize(int cols, int rows)
    {
        if (_shell == null) return;
        try
        {
            var field = typeof(ShellStream).GetField("_channel",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var channel = field?.GetValue(_shell);
            var method = channel?.GetType().GetMethod("SendWindowChangeRequest",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            method?.Invoke(channel, new object[]
            {
                (uint)cols, (uint)rows, (uint)(cols * 8), (uint)(rows * 16)
            });
        }
        catch
        {
            // Non-fatal: the remote keeps the original size.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _shell?.Dispose();   // unblocks the read loop
            _client?.Disconnect();
            _client?.Dispose();
        }
        catch { /* ignore teardown errors */ }
        finally
        {
            // Null the handles so any late Send/Resize/IsConnected call no-ops
            // cleanly instead of touching a disposed stream. ReadLoop keeps its own
            // local reference to the shell, so this doesn't disturb it.
            _shell = null;
            _client = null;
        }
        Closed?.Invoke("Disconnected");
    }
}
