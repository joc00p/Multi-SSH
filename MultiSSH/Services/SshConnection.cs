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
public class SshConnection : IDisposable
{
    private readonly SessionConfig _cfg;
    private SshClient? _client;
    private ShellStream? _shell;
    private bool _disposed;

    public event Action<byte[]>? DataReceived;
    public event Action<string>? StatusChanged;
    public event Action<string>? Closed;

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

        _shell.DataReceived += OnShellData;
        _shell.ErrorOccurred += (_, e) => StatusChanged?.Invoke("Shell error: " + e.Exception.Message);
    }

    private void OnShellData(object? sender, ShellDataEventArgs e)
    {
        if (e.Data is { Length: > 0 })
            DataReceived?.Invoke(e.Data);
    }

    private ConnectionInfo BuildConnectionInfo()
    {
        var methods = new List<AuthenticationMethod>();

        switch (_cfg.Auth)
        {
            case AuthMethod.PublicKey:
                if (string.IsNullOrWhiteSpace(_cfg.PrivateKeyPath) || !File.Exists(_cfg.PrivateKeyPath))
                    throw new FileNotFoundException("Private key file not found", _cfg.PrivateKeyPath);
                var keyFile = string.IsNullOrEmpty(_cfg.KeyPassphrase)
                    ? new PrivateKeyFile(_cfg.PrivateKeyPath)
                    : new PrivateKeyFile(_cfg.PrivateKeyPath, _cfg.KeyPassphrase);
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
            if (_shell != null)
            {
                _shell.DataReceived -= OnShellData;
                _shell.Dispose();
            }
            _client?.Disconnect();
            _client?.Dispose();
        }
        catch { /* ignore teardown errors */ }
        Closed?.Invoke("Disconnected");
    }
}
