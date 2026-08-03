using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MultiSSH.Models;
using MultiSSH.Services;
using MultiSSH.Terminal;
using Renci.SshNet.Common;

namespace MultiSSH.Views;

/// <summary>Live connection state, surfaced as a coloured dot in pane/tab headers.</summary>
public enum ConnectionState { Idle, Connecting, Connected, Disconnected, Failed }

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
    private ITerminalBackend? _conn;
    private bool _connected;
    private bool _pendingConnect = true;
    private bool _connecting;   // guards against overlapping connect/reconnect attempts
    private ConnectionState _state = ConnectionState.Idle;

    public SessionConfig Config => _cfg;
    public string TabTitle { get; private set; }
    public ConnectionState State => _state;
    public string StatusText { get; private set; } = "Idle";

    public event Action<SessionView>? TitleChanged;
    public event Action<SessionView>? ConnectionClosed;
    /// <summary>Raised when the remote shell exits — the host destroys the pane.</summary>
    public event Action<SessionView>? ShellExited;
    /// <summary>Raised when connection state or status text changes (UI thread).</summary>
    public event Action<SessionView>? StateChanged;
    /// <summary>Raised on a terminal double-click — used to enlarge/restore this session.</summary>
    public event Action<SessionView>? DoubleClicked;

    /// <summary>Header status-dot colour for a given state.</summary>
    public static Color DotColor(ConnectionState s) => s switch
    {
        ConnectionState.Connected => Color.FromRgb(0x16, 0xC6, 0x0C),
        ConnectionState.Connecting => Color.FromRgb(0xE0, 0xA0, 0x30),
        ConnectionState.Failed => Color.FromRgb(0xE7, 0x48, 0x56),
        ConnectionState.Disconnected => Color.FromRgb(0x99, 0x99, 0x99),
        _ => Color.FromRgb(0x77, 0x77, 0x77),
    };

    /// <summary>Connection-type colour (used to tint the ball glyph).</summary>
    public static Color KindColor(SessionKind k) => k switch
    {
        SessionKind.PowerShell => Color.FromRgb(0x24, 0x72, 0xC8), // blue
        SessionKind.Cmd => Color.FromRgb(0x9A, 0x9A, 0x9A),        // gray
        SessionKind.Bash => Color.FromRgb(0x4E, 0xAA, 0x25),       // green
        SessionKind.Wsl => Color.FromRgb(0xE9, 0x54, 0x20),        // ubuntu orange
        _ => Color.FromRgb(0x3A, 0x96, 0xDD),                       // SSH: cyan
    };

    /// <summary>Connection-type icon glyph. PowerShell uses the 🌐 globe; the
    /// others are a filled ball tinted by <see cref="KindColor"/>.</summary>
    public static string KindGlyph(SessionKind k) => k switch
    {
        SessionKind.PowerShell => "🌐",
        _ => "●",
    };

    private static Color BarColor(ConnectionState s) => s switch
    {
        ConnectionState.Connected => Color.FromRgb(0x1e, 0x3a, 0x1e),
        ConnectionState.Connecting => Color.FromRgb(0x3a, 0x33, 0x1e),
        ConnectionState.Failed => Color.FromRgb(0x3a, 0x1e, 0x1e),
        _ => Color.FromRgb(0x25, 0x25, 0x2b),
    };

    public SessionView(SessionConfig cfg)
    {
        _cfg = cfg;
        TabTitle = cfg.Display;

        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _term = new TerminalControl(cfg);
        _term.Input += bytes => _conn?.Send(bytes);
        _term.GridResized += (cols, rows) => _conn?.Resize(cols, rows);
        _term.DoubleClicked += () => DoubleClicked?.Invoke(this);
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
        if (_connecting) return;   // a connect attempt is already in flight
        _connecting = true;
        try { await ConnectCoreAsync(); }
        finally { _connecting = false; }
    }

    private async Task ConnectCoreAsync()
    {
        int cols = _term.Buffer.Cols;
        int rows = _term.Buffer.Rows;

        if (_cfg.IsLocal)
        {
            await StartLocalShellAsync(cols, rows);
            return;
        }

        SetState(ConnectionState.Connecting, "Connecting…");

        // If this method needs a password and none was saved, ask for one now
        // (securely, masked) rather than failing the login. A key file means
        // public-key auth, so never prompt for a password in that case.
        if (UsesPassword(_cfg.Auth) && string.IsNullOrEmpty(_cfg.Password)
            && string.IsNullOrEmpty(_cfg.PrivateKeyPath))
        {
            var pw = PromptSecret($"Password for {_cfg.Username}@{_cfg.Host}",
                $"Host {_cfg.Host} : {_cfg.Port}", null);
            if (pw == null) { SetState(ConnectionState.Disconnected, "Login cancelled — no password entered"); return; }
            _cfg.Password = pw;
        }

        for (int attempt = 1; ; attempt++)
        {
            var ssh = new SshConnection(_cfg);
            HookBackend(ssh);
            _conn = ssh;

            try
            {
                await _conn.ConnectAsync(cols, rows);
                SetState(ConnectionState.Connected, $"Connected — {_cfg.Username}@{_cfg.Host}");
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
                if (pw == null) { SetState(ConnectionState.Disconnected, "Login cancelled"); return; }
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
                if (pass == null) { SetState(ConnectionState.Disconnected, "Login cancelled"); return; }
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

    /// <summary>
    /// Start a local PowerShell / cmd session. No host, no credentials — the
    /// shell either launches or it doesn't.
    /// </summary>
    private async Task StartLocalShellAsync(int cols, int rows)
    {
        SetState(ConnectionState.Connecting, $"Starting {_cfg.LocalShellName} …");

        var local = new LocalShellConnection(_cfg);
        HookBackend(local);
        _conn = local;

        try
        {
            await local.ConnectAsync(cols, rows);
            SetState(ConnectionState.Connected, $"{_cfg.LocalShellName} — local");
        }
        catch (Exception ex)
        {
            _conn = null;
            local.Dispose();
            Fail(ex.Message);
        }
    }

    /// <summary>Wire a backend's events to this view (shared by SSH and local shells).</summary>
    private void HookBackend(ITerminalBackend backend)
    {
        backend.StatusChanged += SetStatusText;
        backend.DataReceived += bytes => _term.Feed(bytes);
        backend.Closed += msg =>
        {
            if (_state != ConnectionState.Connecting)
                SetState(ConnectionState.Disconnected, msg);
            Dispatcher.BeginInvoke(() => ConnectionClosed?.Invoke(this));
        };
        backend.ShellExited += () =>
        {
            SetState(ConnectionState.Disconnected, "Shell closed");
            Dispatcher.BeginInvoke(() => ShellExited?.Invoke(this));
        };
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
        SetState(ConnectionState.Failed, "Connection failed: " + msg);
        _term.Feed(Enc($"\r\n\x1b[31m*** Connection failed: {msg} ***\x1b[0m\r\n"));
    }

    private static byte[] Enc(string s) => Encoding.UTF8.GetBytes(s);

    public async Task ReconnectAsync()
    {
        if (_connecting) return;   // don't interrupt/overwrite an in-flight connect
        _conn?.Dispose();
        _conn = null;
        SetState(ConnectionState.Connecting, "Reconnecting …");
        await ConnectAsync();
        _term.Focus();
    }

    /// <summary>Change connection state (dot colour + status bar) and status text.</summary>
    private void SetState(ConnectionState state, string text)
    {
        _state = state;
        _connected = state == ConnectionState.Connected;
        StatusText = text;
        Dispatcher.BeginInvoke(() =>
        {
            _status.Text = text;
            _statusBar.Background = new SolidColorBrush(BarColor(state));
            StateChanged?.Invoke(this);
        });
    }

    /// <summary>Update the status text only, keeping the current state/colour.</summary>
    private void SetStatusText(string s)
    {
        StatusText = s;
        Dispatcher.BeginInvoke(() =>
        {
            _status.Text = s;
            StateChanged?.Invoke(this);
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

    /// <summary>
    /// Like <see cref="Close"/>, but tears the SSH connection down on a background
    /// thread so a wedged socket can't block the caller (used on app shutdown).
    /// The UI timers are still stopped synchronously on the calling (UI) thread.
    /// </summary>
    public void CloseAsync()
    {
        _term.Shutdown();
        var conn = _conn;
        _conn = null;
        if (conn != null)
            Task.Run(() => { try { conn.Dispose(); } catch { /* best effort */ } });
    }
}
