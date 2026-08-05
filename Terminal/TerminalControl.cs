using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MultiSSH.Models;
using MultiSSH.Services;

namespace MultiSSH.Terminal;

/// <summary>
/// A self-contained VT100/xterm terminal widget: renders a
/// <see cref="TerminalBuffer"/> and emits keystrokes via <see cref="Input"/>.
/// Handles scrollback (mouse wheel), selection→copy and right-click paste.
/// </summary>
public class TerminalControl : Control
{
    private readonly TerminalBuffer _buffer;
    private readonly AnsiParser _parser;
    private ColorScheme _scheme;
    private readonly SessionConfig _cfg;

    private Typeface _typeface = null!;
    private Typeface _boldTypeface = null!;
    private double _cellW, _cellH, _baseline;
    private double _fontSize;   // effective size (may differ from _cfg.FontSize in font-scaling resize mode)
    private double _pixelsPerDip = 1.0;

    private int _scrollOffset;            // lines scrolled up into history
    private bool _dirty = true;
    // Incoming output is queued from any thread and drained on the render tick,
    // so a flood of small chunks can't saturate the UI dispatcher.
    private readonly ConcurrentQueue<byte[]> _incoming = new();
    private readonly DispatcherTimer _renderTimer;
    private readonly DispatcherTimer _blinkTimer;
    private bool _cursorOn = true;

    // selection (in "combined" line coordinates: history rows then screen rows)
    private bool _selecting;
    private (int row, int col)? _selStart;
    private (int row, int col)? _selEnd;

    /// <summary>Raised with the bytes to send to the remote host.</summary>
    public event Action<byte[]>? Input;
    /// <summary>Raised when the window title (OSC) changes.</summary>
    public event Action<string>? TitleChanged;
    /// <summary>Raised when the visible grid size changes (cols, rows).</summary>
    public event Action<int, int>? GridResized;
    /// <summary>Raised on a left double-click (used to maximize/restore the pane).</summary>
    public event Action? DoubleClicked;

    private string _lastTitle = "";

    public TerminalControl(SessionConfig cfg)
    {
        _cfg = cfg;
        _fontSize = cfg.FontSize;
        _scheme = ColorScheme.Get(cfg.ColorScheme);
        _buffer = new TerminalBuffer(cfg.Rows, cfg.Columns) { MaxScrollback = cfg.ScrollbackLines };
        _parser = new AnsiParser(_buffer);
        _buffer.Bell += OnBell;

        Focusable = true;
        FocusVisualStyle = null;
        SnapsToDevicePixels = true;
        Cursor = Cursors.IBeam;
        Background = new SolidColorBrush(_scheme.Background);
        Padding = new Thickness(2);

        BuildTypeface();

        _renderTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _renderTimer.Tick += (_, _) =>
        {
            DrainIncoming();
            if (_dirty) { _dirty = false; InvalidateVisual(); }
            PublishTitle();
        };
        _renderTimer.Start();

        _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        _blinkTimer.Tick += (_, _) => { _cursorOn = !_cursorOn; _dirty = true; };
        _blinkTimer.Start();

        ContextMenu = BuildContextMenu();

        Loaded += (_, _) => Focus();
    }

    private ContextMenu BuildContextMenu()
    {
        var copy = new MenuItem { Header = "Copy", InputGestureText = "Ctrl+Shift+C" };
        copy.Click += (_, _) => CopySelection();
        var paste = new MenuItem { Header = "Paste", InputGestureText = "Ctrl+Shift+V" };
        paste.Click += (_, _) => Paste();
        var selectAll = new MenuItem { Header = "Select All" };
        selectAll.Click += (_, _) => SelectAll();
        var clear = new MenuItem { Header = "Clear Selection" };
        clear.Click += (_, _) => { _selStart = _selEnd = null; _dirty = true; };

        var menu = new ContextMenu();
        menu.Items.Add(copy);
        menu.Items.Add(paste);
        menu.Items.Add(new Separator());
        menu.Items.Add(selectAll);
        menu.Items.Add(clear);
        menu.Opened += (_, _) =>
        {
            copy.IsEnabled = HasSelection();
            try { paste.IsEnabled = Clipboard.ContainsText(); } catch { paste.IsEnabled = true; }
        };
        return menu;
    }

    private bool HasSelection()
        => _selStart != null && _selEnd != null && !_selStart.Value.Equals(_selEnd.Value);

    private void SelectAll()
    {
        int history = _buffer.Scrollback.Count;
        _selStart = (0, 0);
        _selEnd = (history + _buffer.Rows - 1, _buffer.Cols - 1);
        _dirty = true;
    }

    public TerminalBuffer Buffer => _buffer;
    public AnsiParser Parser => _parser;

    public void ApplyScheme(string name)
    {
        _scheme = ColorScheme.Get(name);
        Background = new SolidColorBrush(_scheme.Background);
        _dirty = true;
    }

    private void BuildTypeface()
    {
        var family = new FontFamily(_cfg.FontFamily);
        _typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _boldTypeface = new Typeface(family, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

        var ft = new FormattedText("M", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            _typeface, _fontSize, Brushes.White, _pixelsPerDip);
        _cellW = Math.Ceiling(ft.WidthIncludingTrailingWhitespace);
        _cellH = Math.Ceiling(ft.Height);
        _baseline = ft.Baseline;
    }

    // -------------------- incoming data --------------------

    /// <summary>Queue raw bytes from the backend (any thread). Drained on the render tick.</summary>
    public void Feed(byte[] data)
    {
        if (data.Length == 0) return;
        _incoming.Enqueue(data);
    }

    /// <summary>Parse all queued output on the UI thread (called from the render tick).</summary>
    private void DrainIncoming()
    {
        _buffer.PushErasedToScrollback = AppSettings.Current.PushErasedToScrollback;

        bool any = false;
        while (_incoming.TryDequeue(out var chunk))
        {
            // Contain any parser/buffer fault to this one terminal: a malformed escape
            // sequence must never escape the render tick and take down the whole app.
            try { _parser.Feed(chunk, chunk.Length); }
            catch { /* drop the offending chunk, keep the session alive */ }
            any = true;
        }
        if (any)
        {
            // "Reset scrollback on display activity": snap to the bottom on new output.
            if (AppSettings.Current.ResetScrollbackOnActivity) _scrollOffset = 0;
            _dirty = true;
        }
    }

    private void OnBell()
    {
        if (_cfg.BellEnabled) System.Media.SystemSounds.Beep.Play();
    }

    private void PublishTitle()
    {
        if (_buffer.Title != _lastTitle)
        {
            _lastTitle = _buffer.Title;
            TitleChanged?.Invoke(_lastTitle);
        }
    }

    // -------------------- layout / sizing --------------------

    protected override Size ArrangeOverride(Size finalSize)
    {
        var s = base.ArrangeOverride(finalSize);
        RecomputeGrid(finalSize);
        return s;
    }

    private void RecomputeGrid(Size size)
    {
        if (_cellW <= 0 || _cellH <= 0) return;
        double availW = size.Width - Padding.Left - Padding.Right;
        double availH = size.Height - Padding.Top - Padding.Bottom;
        if (availW <= 0 || availH <= 0) return;

        bool maximized = (Window.GetWindow(this) as Window)?.WindowState == WindowState.Maximized;
        switch (AppSettings.Current.ResizeBehavior)
        {
            case "Forbid":
                SetGrid(Math.Max(1, _cfg.Columns), Math.Max(1, _cfg.Rows));
                break;
            case "FontSize":
                ScaleFontToFit(availW, availH);
                break;
            case "FontSizeMax":
                if (maximized) ScaleFontToFit(availW, availH);
                else ReflowGrid(availW, availH);
                break;
            default: // RowsCols
                ReflowGrid(availW, availH);
                break;
        }
    }

    private void ReflowGrid(double availW, double availH)
        => SetGrid(Math.Max(1, (int)(availW / _cellW)), Math.Max(1, (int)(availH / _cellH)));

    private void SetGrid(int cols, int rows)
    {
        if (cols != _buffer.Cols || rows != _buffer.Rows)
        {
            _buffer.Resize(rows, cols);
            _dirty = true;
            GridResized?.Invoke(cols, rows);
        }
    }

    /// <summary>Keep the configured rows/cols and scale the font so they fill the pane.</summary>
    private void ScaleFontToFit(double availW, double availH)
    {
        int cols = Math.Max(1, _cfg.Columns);
        int rows = Math.Max(1, _cfg.Rows);

        double perColW = _cellW / _fontSize;   // cell width per point (≈ constant)
        double perRowH = _cellH / _fontSize;
        double newSize = Math.Max(6, Math.Min(availW / (cols * perColW), availH / (rows * perRowH)));

        if (Math.Abs(newSize - _fontSize) > 0.25)
        {
            _fontSize = newSize;
            BuildTypeface();
            _dirty = true;
        }
        SetGrid(cols, rows);
    }

    // -------------------- rendering --------------------

    protected override void OnRender(DrawingContext dc)
    {
        var newDpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (Math.Abs(newDpi - _pixelsPerDip) > 0.001)
        {
            _pixelsPerDip = newDpi;
            BuildTypeface();
        }

        dc.DrawRectangle(new SolidColorBrush(_scheme.Background), null, new Rect(RenderSize));

        int rows = _buffer.Rows;
        int cols = _buffer.Cols;
        int history = _buffer.Scrollback.Count;
        int topIndex = history - _scrollOffset; // combined index of first visible row

        double ox = Padding.Left, oy = Padding.Top;

        var selBrush = new SolidColorBrush(Color.FromArgb(120, 80, 130, 200));

        for (int r = 0; r < rows; r++)
        {
            int combined = topIndex + r;
            Cell[]? line = LineAtCombined(combined, history);
            if (line == null) continue;

            double y = oy + r * _cellH;
            int c = 0;
            while (c < cols && c < line.Length)
            {
                var cell = line[c];
                int runStart = c;
                var first = cell;
                // group identical style
                while (c < cols && c < line.Length && line[c].SameStyle(first))
                    c++;

                DrawRun(dc, line, runStart, c, combined, ox, y, selBrush);
            }
        }

        DrawCursor(dc, ox, oy, history);
        DrawScrollbarHint(dc, history);
    }

    private void DrawRun(DrawingContext dc, Cell[] line, int start, int end, int combinedRow,
                         double ox, double y, Brush selBrush)
    {
        var style = line[start];
        bool inverse = (style.Flags & CellFlags.Inverse) != 0;

        // Resolve each colour in its natural role first (so a Default code maps to the
        // scheme's foreground/background correctly), THEN swap for inverse. Swapping the
        // raw codes before resolving cancels out when both are Default, so reverse-video
        // on default colours (vim/htop/tmux status bars) would render as normal text.
        var fg = ColorResolver.Resolve(style.Fg, _scheme, isForeground: true);
        var bg = ColorResolver.Resolve(style.Bg, _scheme, isForeground: false);
        if (inverse) (fg, bg) = (bg, fg);

        double x = ox + start * _cellW;
        double w = (end - start) * _cellW;

        // Background fill (only when it differs from the terminal background — i.e. an
        // explicit background was set, or inverse video makes the fill the fg colour).
        if (style.Bg != Cell.Default || inverse)
            dc.DrawRectangle(new SolidColorBrush(bg), null, new Rect(x, y, w, _cellH));

        // Selection highlight overlay.
        for (int c = start; c < end; c++)
        {
            if (IsSelected(combinedRow, c))
                dc.DrawRectangle(selBrush, null, new Rect(ox + c * _cellW, y, _cellW, _cellH));
        }

        if ((style.Flags & CellFlags.Hidden) != 0) return;

        var sb = new StringBuilder(end - start);
        for (int c = start; c < end; c++)
        {
            char ch = line[c].Char;
            sb.Append(ch == '\0' ? ' ' : ch);
        }
        string text = sb.ToString();
        if (string.IsNullOrWhiteSpace(text)) return;

        bool bold = (style.Flags & CellFlags.Bold) != 0;
        var tf = bold ? _boldTypeface : _typeface;

        if ((style.Flags & CellFlags.Dim) != 0)
            fg = Color.FromArgb(160, fg.R, fg.G, fg.B);

        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            tf, _fontSize, new SolidColorBrush(fg), _pixelsPerDip);

        dc.DrawText(ft, new Point(x, y));

        if ((style.Flags & CellFlags.Underline) != 0)
        {
            var pen = new Pen(new SolidColorBrush(fg), 1);
            double uy = y + _baseline + 1.5;
            dc.DrawLine(pen, new Point(x, uy), new Point(x + w, uy));
        }
    }

    private void DrawCursor(DrawingContext dc, double ox, double oy, int history)
    {
        if (!_buffer.CursorVisible || _scrollOffset != 0) return;
        if (!IsFocused && !IsKeyboardFocusWithin)
        {
            // hollow cursor when unfocused
            var pen = new Pen(new SolidColorBrush(_scheme.Cursor), 1);
            double cx = ox + _buffer.CursorX * _cellW;
            double cy = oy + _buffer.CursorY * _cellH;
            dc.DrawRectangle(null, pen, new Rect(cx + 0.5, cy + 0.5, _cellW - 1, _cellH - 1));
            return;
        }
        if (!_cursorOn) return;

        double x = ox + _buffer.CursorX * _cellW;
        double y = oy + _buffer.CursorY * _cellH;
        var cursorBrush = new SolidColorBrush(_scheme.Cursor) { Opacity = 0.75 };
        dc.DrawRectangle(cursorBrush, null, new Rect(x, y, _cellW, _cellH));
    }

    private void DrawScrollbarHint(DrawingContext dc, int history)
    {
        if (history == 0) return;
        // "Display scrollbar" (and its full-screen variant when the window is maximized).
        bool maximized = (Window.GetWindow(this) as Window)?.WindowState == WindowState.Maximized;
        bool show = maximized ? AppSettings.Current.ScrollbarInFullScreen : AppSettings.Current.DisplayScrollbar;
        if (!show) return;

        double trackH = RenderSize.Height;
        int total = history + _buffer.Rows;
        double thumbH = Math.Max(20, trackH * _buffer.Rows / total);
        double topFrac = (double)(history - _scrollOffset) / total;
        double y = topFrac * trackH;
        var brush = new SolidColorBrush(Color.FromArgb(90, 200, 200, 200));
        dc.DrawRectangle(brush, null, new Rect(RenderSize.Width - 4, y, 4, thumbH));
    }

    private Cell[]? LineAtCombined(int combined, int history)
    {
        if (combined < 0) return null;
        if (combined < history) return _buffer.Scrollback[combined];
        int screenRow = combined - history;
        if (screenRow < _buffer.Rows) return _buffer.Line(screenRow);
        return null;
    }

    // -------------------- selection --------------------

    private bool IsSelected(int row, int col)
    {
        if (_selStart == null || _selEnd == null) return false;
        var (sr, sc) = _selStart.Value;
        var (er, ec) = _selEnd.Value;
        if (sr > er || (sr == er && sc > ec)) { (sr, sc, er, ec) = (er, ec, sr, sc); }
        if (row < sr || row > er) return false;
        if (row == sr && col < sc) return false;
        if (row == er && col > ec) return false;
        return true;
    }

    private (int row, int col) PointToCell(Point p)
    {
        int history = _buffer.Scrollback.Count;
        int topIndex = history - _scrollOffset;
        int col = Math.Clamp((int)((p.X - Padding.Left) / _cellW), 0, _buffer.Cols - 1);
        int r = Math.Clamp((int)((p.Y - Padding.Top) / _cellH), 0, _buffer.Rows - 1);
        return (topIndex + r, col);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();

        // Double-click anywhere in the terminal enlarges/restores this session.
        if (e.ClickCount == 2)
        {
            _selecting = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
            _selStart = _selEnd = null;
            _dirty = true;
            DoubleClicked?.Invoke();
            e.Handled = true;
            return;
        }

        _selecting = true;
        var cell = PointToCell(e.GetPosition(this));
        _selStart = cell; _selEnd = cell;
        CaptureMouse();
        _dirty = true;
        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_selecting)
        {
            _selEnd = PointToCell(e.GetPosition(this));
            _dirty = true;
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_selecting)
        {
            _selecting = false;
            ReleaseMouseCapture();
            if (_cfg.CopyOnSelect) CopySelection();
        }
        base.OnMouseLeftButtonUp(e);
    }

    // Right-click now opens the Copy/Paste context menu (WPF shows it automatically).

    private void CopySelection()
    {
        if (_selStart == null || _selEnd == null) return;
        var (sr, sc) = _selStart.Value;
        var (er, ec) = _selEnd.Value;
        if (sr > er || (sr == er && sc > ec)) { (sr, sc, er, ec) = (er, ec, sr, sc); }
        if (sr == er && sc == ec) return;

        string text = _buffer.GetText(sr, sc, er, ec, includeScrollback: true);
        if (!string.IsNullOrEmpty(text))
        {
            try { Clipboard.SetText(text); } catch { /* clipboard busy */ }
        }
    }

    private void Paste()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                string t = Clipboard.GetText().Replace("\r\n", "\r").Replace("\n", "\r");
                Input?.Invoke(Encoding.UTF8.GetBytes(t));
            }
        }
        catch { /* clipboard busy */ }
    }

    // -------------------- scrolling --------------------

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        int lines = e.Delta / 120 * 3;
        int history = _buffer.Scrollback.Count;
        _scrollOffset = Math.Clamp(_scrollOffset + lines, 0, history);
        _dirty = true;
        e.Handled = true;
    }

    // -------------------- keyboard --------------------

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    { _dirty = true; base.OnGotKeyboardFocus(e); }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    { _dirty = true; base.OnLostKeyboardFocus(e); }

    /// <summary>Snap to the bottom on keypress when "Reset scrollback on keypress" is enabled.</summary>
    private void ResetScrollOnKeypress()
    {
        if (AppSettings.Current.ResetScrollbackOnKeypress) _scrollOffset = 0;
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Text))
        {
            // Filter out control chars already handled in OnKeyDown.
            if (e.Text.Length == 1 && e.Text[0] < 0x20) { base.OnTextInput(e); return; }
            Input?.Invoke(Encoding.UTF8.GetBytes(e.Text));
            ResetScrollOnKeypress();
            e.Handled = true;
        }
        base.OnTextInput(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        bool ctrl = (mods & ModifierKeys.Control) != 0;
        bool shift = (mods & ModifierKeys.Shift) != 0;

        // Copy / paste shortcuts (PuTTY-friendly).
        if (ctrl && shift && e.Key == Key.C) { CopySelection(); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.V) { Paste(); e.Handled = true; return; }

        // User-configured hot keys: send the mapped command to the shell.
        var pressed = e.Key == Key.System ? e.SystemKey : e.Key; // Alt combos arrive as System
        foreach (var hk in Services.AppSettings.Current.HotKeys)
        {
            if (hk.Matches(pressed, mods) && !string.IsNullOrEmpty(hk.Command))
            {
                Input?.Invoke(Encoding.UTF8.GetBytes(hk.Command + (hk.SendEnter ? "\r" : "")));
                ResetScrollOnKeypress();
                e.Handled = true;
                return;
            }
        }

        byte[]? seq = MapKey(e.Key, ctrl, shift);
        if (seq != null)
        {
            Input?.Invoke(seq);
            ResetScrollOnKeypress();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    private byte[]? MapKey(Key key, bool ctrl, bool shift)
    {
        string ss = _parser.ApplicationCursorKeys ? "\x1bO" : "\x1b[";

        switch (key)
        {
            case Key.Enter: return new byte[] { 0x0D };
            case Key.Back: return new byte[] { 0x7F };
            case Key.Tab: return shift ? Enc("\x1b[Z") : new byte[] { 0x09 };
            case Key.Escape: return new byte[] { 0x1B };
            case Key.Up: return Enc(ss + "A");
            case Key.Down: return Enc(ss + "B");
            case Key.Right: return Enc(ss + "C");
            case Key.Left: return Enc(ss + "D");
            case Key.Home: return Enc(ss + "H");
            case Key.End: return Enc(ss + "F");
            case Key.Insert: return Enc("\x1b[2~");
            case Key.Delete: return Enc("\x1b[3~");
            case Key.PageUp: return Enc("\x1b[5~");
            case Key.PageDown: return Enc("\x1b[6~");
            case Key.F1: return Enc("\x1bOP");
            case Key.F2: return Enc("\x1bOQ");
            case Key.F3: return Enc("\x1bOR");
            case Key.F4: return Enc("\x1bOS");
            case Key.F5: return Enc("\x1b[15~");
            case Key.F6: return Enc("\x1b[17~");
            case Key.F7: return Enc("\x1b[18~");
            case Key.F8: return Enc("\x1b[19~");
            case Key.F9: return Enc("\x1b[20~");
            case Key.F10: return Enc("\x1b[21~");
            case Key.F11: return Enc("\x1b[23~");
            case Key.F12: return Enc("\x1b[24~");
        }

        // Ctrl+letter -> control code 1..26, plus common ctrl symbols.
        if (ctrl)
        {
            if (key >= Key.A && key <= Key.Z)
                return new[] { (byte)(key - Key.A + 1) };
            switch (key)
            {
                case Key.Space: return new byte[] { 0x00 };
                case Key.OemOpenBrackets: return new byte[] { 0x1B }; // Ctrl+[
                case Key.OemCloseBrackets: return new byte[] { 0x1D };
                case Key.OemBackslash:
                case Key.Oem5: return new byte[] { 0x1C };
                case Key.OemMinus: return new byte[] { 0x1F };
            }
        }
        return null;
    }

    private static byte[] Enc(string s) => Encoding.UTF8.GetBytes(s);

    public void Shutdown()
    {
        _renderTimer.Stop();
        _blinkTimer.Stop();
    }
}
