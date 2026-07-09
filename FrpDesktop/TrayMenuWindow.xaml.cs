using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfButton = System.Windows.Controls.Button;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfCursors = System.Windows.Input.Cursors;
using WpfImage = System.Windows.Controls.Image;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace FrpDesktop;

public partial class TrayMenuWindow : Window
{
    private readonly AppState _state;
    private readonly FrpProfile? _selectedProfile;
    private readonly Func<FrpProfile, FrpProxy, bool, Task> _toggleProxyAsync;
    private readonly Action _restoreMainWindow;
    private readonly Func<Task> _exitApplicationAsync;
    private bool _isClosingByCommand;

    public TrayMenuWindow(
        AppState state,
        FrpProfile? selectedProfile,
        bool isRunning,
        Func<FrpProfile, FrpProxy, bool, Task> toggleProxyAsync,
        Action restoreMainWindow,
        Func<Task> exitApplicationAsync)
    {
        InitializeComponent();
        _state = state;
        _selectedProfile = selectedProfile;
        _toggleProxyAsync = toggleProxyAsync;
        _restoreMainWindow = restoreMainWindow;
        _exitApplicationAsync = exitApplicationAsync;

        StatusText.Text = isRunning ? "连接运行中" : "后台待命";
        BuildProfileList();
    }

    private void BuildProfileList()
    {
        ProfilesPanel.Children.Clear();

        if (_state.Profiles.Count == 0)
        {
            ProfilesPanel.Children.Add(CreateEmptyText("暂无节点"));
            return;
        }

        foreach (var profile in _state.Profiles)
        {
            ProfilesPanel.Children.Add(CreateProfileGroup(profile));
        }
    }

    private UIElement CreateProfileGroup(FrpProfile profile)
    {
        var group = new Border
        {
            Background = BrushFromHex(profile.Id == _selectedProfile?.Id ? "#122B25" : "#0F1B29"),
            BorderBrush = BrushFromHex(profile.Id == _selectedProfile?.Id ? "#1A6B58" : "#203349"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 8)
        };

        var content = new StackPanel();
        group.Child = content;

        var header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new StackPanel();
        var titleRow = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal
        };
        if (profile.HasFlagIcon)
        {
            titleRow.Children.Add(new WpfImage
            {
                Source = new BitmapImage(new Uri(profile.FlagIconPath, UriKind.Relative)),
                Width = 18,
                Height = 18,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 1, 5, 0)
            });
        }
        titleRow.Children.Add(new TextBlock
        {
            Text = profile.NameWithoutFlag,
            Foreground = WpfBrushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14
        });
        title.Children.Add(titleRow);
        title.Children.Add(new TextBlock
        {
            Text = $"{profile.ServerAddr}:{profile.ServerPort}",
            Foreground = BrushFromHex("#91A4B8"),
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0)
        });
        header.Children.Add(title);

        var countBadge = new Border
        {
            Background = BrushFromHex("#153D34"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 3, 8, 4),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = $"{profile.Proxies.Count(proxy => proxy.Enabled)}/{profile.Proxies.Count}",
                Foreground = BrushFromHex("#5BE2A6"),
                FontWeight = FontWeights.SemiBold,
                FontSize = 11
            }
        };
        Grid.SetColumn(countBadge, 1);
        header.Children.Add(countBadge);
        content.Children.Add(header);

        if (profile.Proxies.Count == 0)
        {
            content.Children.Add(CreateEmptyText("暂无通道"));
            return group;
        }

        foreach (var proxy in profile.Proxies)
        {
            content.Children.Add(CreateProxyRow(profile, proxy));
        }

        return group;
    }

    private UIElement CreateProxyRow(FrpProfile profile, FrpProxy proxy)
    {
        var row = new Border
        {
            Background = BrushFromHex("#0B1421"),
            BorderBrush = BrushFromHex("#1E3044"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(9, 8, 9, 8),
            Margin = new Thickness(0, 0, 0, 6),
            Cursor = WpfCursors.Hand
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Child = grid;

        var text = new StackPanel();
        text.Children.Add(new TextBlock
        {
            Text = proxy.Name,
            Foreground = WpfBrushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13
        });
        text.Children.Add(new TextBlock
        {
            Text = $"{proxy.Type}  {proxy.LocalIP}:{proxy.LocalPort} -> {proxy.RemotePort}",
            Foreground = BrushFromHex("#91A4B8"),
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0)
        });
        grid.Children.Add(text);

        var toggle = new WpfCheckBox
        {
            Style = (Style)FindResource("SwitchCheckBox"),
            IsChecked = proxy.Enabled,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };
        Grid.SetColumn(toggle, 1);
        toggle.Click += async (_, _) =>
        {
            await _toggleProxyAsync(profile, proxy, toggle.IsChecked == true);
            BuildProfileList();
        };
        grid.Children.Add(toggle);

        row.MouseEnter += (_, _) => row.Opacity = 0.86;
        row.MouseLeave += (_, _) => row.Opacity = 1;
        row.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is WpfCheckBox)
            {
                return;
            }

            toggle.IsChecked = toggle.IsChecked != true;
            toggle.RaiseEvent(new RoutedEventArgs(WpfButton.ClickEvent));
        };

        return row;
    }

    private static TextBlock CreateEmptyText(string text)
    {
        return new TextBlock
        {
            Text = text,
            Foreground = BrushFromHex("#91A4B8"),
            FontSize = 12,
            Margin = new Thickness(2, 2, 0, 4)
        };
    }

    public void PlaceNear(double screenX, double screenY)
    {
        Left = screenX - Width - 8;
        Top = screenY - ActualHeight - 8;

        var area = SystemParameters.WorkArea;
        if (Left < area.Left + 8)
        {
            Left = area.Left + 8;
        }

        if (Top < area.Top + 8)
        {
            Top = area.Top + 8;
        }

        if (Left + Width > area.Right - 8)
        {
            Left = area.Right - Width - 8;
        }

        if (Top + ActualHeight > area.Bottom - 8)
        {
            Top = area.Bottom - ActualHeight - 8;
        }
    }

    private void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        _isClosingByCommand = true;
        Close();
        _restoreMainWindow();
    }

    private async void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        _isClosingByCommand = true;
        Close();
        await _exitApplicationAsync();
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (!_isClosingByCommand)
        {
            Close();
        }
    }

    private static SolidColorBrush BrushFromHex(string hex)
    {
        return (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
    }
}
