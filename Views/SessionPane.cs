using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MultiSSH.Views;

/// <summary>
/// A movable container that wraps a <see cref="SessionView"/> with an optional
/// header (title + reconnect + close). The same pane instance is moved between
/// the tab host and the tile host, so it must be detached from its old parent
/// before being re-added.
/// </summary>
public class SessionPane : Border
{
    public SessionView Session { get; }

    private readonly Border _header;
    private readonly TextBlock _titleText;
    private bool _active;

    public event Action<SessionPane>? CloseRequested;
    public event Action<SessionPane>? ReconnectRequested;
    public event Action<SessionPane>? Activated;

    private static readonly Color ActiveColor = Color.FromRgb(0x3B, 0x78, 0xFF);
    private static readonly Color InactiveColor = Color.FromRgb(0x3A, 0x3A, 0x42);

    public SessionPane(SessionView session)
    {
        Session = session;
        BorderThickness = new Thickness(2);
        BorderBrush = new SolidColorBrush(InactiveColor);

        var dock = new DockPanel { LastChildFill = true };

        _titleText = new TextBlock
        {
            Text = session.TabTitle,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var reconnectBtn = MakeHeaderButton("⟳", "Reconnect");
        reconnectBtn.Click += (_, _) => ReconnectRequested?.Invoke(this);
        var closeBtn = MakeHeaderButton("✕", "Close");
        closeBtn.Click += (_, _) => CloseRequested?.Invoke(this);

        var btns = new StackPanel { Orientation = Orientation.Horizontal };
        btns.Children.Add(reconnectBtn);
        btns.Children.Add(closeBtn);
        DockPanel.SetDock(btns, Dock.Right);

        var headerGrid = new DockPanel();
        headerGrid.Children.Add(btns);
        headerGrid.Children.Add(_titleText);

        _header = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x30)),
            Height = 26,
            Child = headerGrid,
        };
        DockPanel.SetDock(_header, Dock.Top);
        dock.Children.Add(_header);
        dock.Children.Add(session);

        Child = dock;

        session.TitleChanged += _ => _titleText.Text = session.TabTitle;
        _header.MouseLeftButtonDown += (_, _) => Activated?.Invoke(this);
        PreviewMouseDown += (_, _) => Activated?.Invoke(this);
    }

    public bool HeaderVisible
    {
        get => _header.Visibility == Visibility.Visible;
        set
        {
            _header.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            BorderThickness = value ? new Thickness(2) : new Thickness(0);
        }
    }

    public string Title => Session.TabTitle;

    public bool Active
    {
        get => _active;
        set
        {
            _active = value;
            BorderBrush = new SolidColorBrush(value ? ActiveColor : InactiveColor);
        }
    }

    private static Button MakeHeaderButton(string glyph, string tip)
    {
        return new Button
        {
            Content = glyph,
            ToolTip = tip,
            Width = 26,
            Height = 26,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            FontSize = 12,
        };
    }

    /// <summary>Remove this pane from whatever parent currently holds it.</summary>
    public void DetachFromParent()
    {
        switch (Parent)
        {
            case Panel panel: panel.Children.Remove(this); break;
            case ContentControl cc when ReferenceEquals(cc.Content, this): cc.Content = null; break;
            case Decorator dec when ReferenceEquals(dec.Child, this): dec.Child = null; break;
        }
    }
}
