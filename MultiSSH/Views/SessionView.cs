using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MultiSSH.Models;
using MultiSSH.Services;
using MultiSSH.Terminal;

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

    public async Task ConnectAsync()
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

        try
        {
            int cols = _term.Buffer.Cols;
            int rows = _term.Buffer.Rows;
            await _conn.ConnectAsync(cols, rows);
            _connected = true;
            _term.Feed(System.Text.Encoding.UTF8.GetBytes($"\x1b[32m*** Connected to {_cfg.Host} ***\x1b[0m\r\n"));
        }
        catch (Exception ex)
        {
            SetStatus("Connection failed: " + ex.Message);
            _term.Feed(System.Text.Encoding.UTF8.GetBytes(
                $"\r\n\x1b[31m*** Connection failed: {ex.Message} ***\x1b[0m\r\n"));
        }
    }

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

    public void Close()
    {
        _term.Shutdown();
        _conn?.Dispose();
        _conn = null;
    }
}
