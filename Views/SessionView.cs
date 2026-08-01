using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MultiSSH.Models;
using MultiSSH.Services;
using MultiSSH.Terminal;
using Renci.SshNet.Common;

namespace MultiSSH.Views;

/// <summary>
/// One live SSH session: a terminal plus a status strip, backed by an
/// <see cref="SshConnection"/>. Used interchangeably inside tabs or tiles.
/// </summary>
public class SessionView : Grid
{
    private readonly SessionConfig _cfg;
    private readonly TerminalControl _term;
    private readonly TextBlock _status;
    private readonly Border _statusBar;
    private SshConnection? _conn;
    private bool _connected;
    private bool _pendingConnect = true;

    public SessionConfig Config => _cfg;
    public string TabTitle { get; private set; }

    public event Action<SessionView>? TitleChanged;
    public event Action<SessionView>? ConnectionClosed;
    /// <summary>Raised when the remote shell exits — the host destroys the pane.</summary>
    public event Action<SessionView>? ShellExited;

    public SessionView(SessionConfig cfg)
    {
        _cfg = cfg;
        TabTitle = cfg.Display;

        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _term = new TerminalControl(cfg);
        _term.Input += bytes => _conn?.Send(bytes);
        _term.GridResized += (cols, rows) => _conn?.Resize(cols, rows);
        _term.TitleChanged += t =>
        {
            TabTitle = string.IsNullOrWhiteSpace(t) ? cfg.Display : t;
            TitleChanged?.Invoke(this);
        };
        SetRow(_term, 0);
        Children.Add(_term);

        _status = new TextBlock
        {
            Text = "Idle",
            Foreground = Brushes.White,
            Margin = new Thickness(6, 2, 6, 2),
            FontSize = 11,
        };
        _statusBar = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x2b)),
            Child = _status,
        };
        SetRow(_statusBar, 1);
        Children.Add(_statusBar);

        Loaded += OnLoaded;
        Unloaded += (_, _) => { /* keep connection alive across tab/tile moves */ };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_pendingConnect)
        {
            _pendingConnect = false;
            _ = ConnectAsync();
        }
        _term.Focus();
    }

    private const int MaxAuthAttempts = 4;

    public async Task ConnectAsync()
    {
        int cols = _term.Buffer.Cols;
        int rows = _term.Buffer.Rows;

        // If this method needs a password and none was saved, ask for one now
        // (securely, masked) rather than failing the login.
        if (UsesPassword(_cfg.Auth) && string.IsNullOrEmpty(_cfg.Password))
        {
            var pw = PromptSecret($"Password for {_cfg.Username}@{_cfg.Host}",
                $"Host {_cfg.Host} : {_cfg.Port}", null);
            if (pw == null) { SetStatus("Login cancelled — no password entered"); return; }
            _cfg.Password = pw;
        }

        for (int attempt = 1; ; attempt++)
        {
            _conn = new SshConnection(_cfg);
            _conn.StatusChanged += SetStatus;
            _conn.DataReceived += bytes => _term.Feed(bytes);
            _conn.Closed += msg =>
            {
                _connected = false;
                SetStatus(msg);
                Dispatcher.BeginInvoke(() => ConnectionClosed?.Invoke(this));
            };
            _conn.ShellExited += () =>
            {
                _connected = false;
                Dispatcher.BeginInvoke(() => ShellExited?.Invoke(this));
            };

            try
            {
                await _conn.ConnectAsync(cols, rows);
                _connected = true;
                _term.Feed(Enc($"\x1b[32m*** Connected to {_cfg.Host} ***\x1b[0m\r\n"));
                return;
            }
            catch (SshAuthenticationException ex)
            {
                _conn.Dispose();
                _conn = null;

                // Re-prompt on rejection (wrong or empty password), up to a limit.
                if (!UsesPassword(_cfg.Auth) || attempt >= MaxAuthAttempts)
                {
                    Fail(ex.Message);
                    return;
                }

                var pw = PromptSecret($"Password for {_cfg.Username}@{_cfg.Host}",
                    $"Host {_cfg.Host} : {_cfg.Port}", $"Authentication failed: {ex.Message}");
                if (pw == null) { SetStatus("Login cancelled"); return; }
                _cfg.Password = pw;
            }
            catch (KeyPassphraseRequiredException ex)
            {
                _conn.Dispose();
                _conn = null;

                if (attempt >= MaxAuthAttempts) { Fail(ex.Message); return; }

                // Show the error only after a wrong attempt (a passphrase was set).
                string? error = string.IsNullOrEmpty(_cfg.KeyPassphrase) ? null : ex.Message;
                var pass = PromptSecret("Passphrase for the private key",
                    _cfg.PrivateKeyPath ?? "", error);
                if (pass == null) { SetStatus("Login cancelled"); return; }
                _cfg.KeyPassphrase = pass;
            }
            catch (Exception ex)
            {
                _conn?.Dispose();
                _conn = null;
                Fail(ex.Message);
                return;
            }
        }
    }

    private static bool UsesPassword(AuthMethod m)
        => m is AuthMethod.Password or AuthMethod.KeyboardInteractive or AuthMethod.Agent;

    /// <summary>Show a masked secret prompt (password or passphrase). Null if cancelled.</summary>
    private string? PromptSecret(string prompt, string target, string? error)
    {
        var dlg = new PasswordPromptDialog(prompt, target, error)
        {
            Owner = Window.GetWindow(this),
        };
        return dlg.ShowDialog() == true ? dlg.Password : null;
    }

    private void Fail(string msg)
    {
        SetStatus("Connection failed: " + msg);
        _term.Feed(Enc($"\r\n\x1b[31m*** Connection failed: {msg} ***\x1b[0m\r\n"));
    }

    private static byte[] Enc(string s) => Encoding.UTF8.GetBytes(s);

    public async Task ReconnectAsync()
    {
        _conn?.Dispose();
        _conn = null;
        SetStatus("Reconnecting …");
        await ConnectAsync();
        _term.Focus();
    }

    private void SetStatus(string s)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _status.Text = s;
            _statusBar.Background = new SolidColorBrush(
                _connected ? Color.FromRgb(0x1e, 0x3a, 0x1e) : Color.FromRgb(0x25, 0x25, 0x2b));
        });
    }

    public bool IsConnected => _connected;

    public void FocusTerminal() => _term.Focus();

    /// <summary>Send raw text to the remote shell (used by the broadcast bar).</summary>
    public void SendText(string text) => _conn?.Send(text);

    public void Close()
    {
        _term.Shutdown();
        _conn?.Dispose();
        _conn = null;
    }
}
