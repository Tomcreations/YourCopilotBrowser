using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace YCB;

public partial class ProfileChooserWindow : Window
{
    private readonly List<ProfileItem> _profiles;
    private readonly string _activeProfileName;
    private readonly bool _darkMode;
    private readonly Action<ProfileItem> _saveProfile;
    private readonly Func<string, string?> _deleteProfile;

    public ProfileItem? SelectedProfile { get; private set; }

    public ProfileChooserWindow(
        IEnumerable<ProfileItem> profiles,
        string activeProfileName,
        bool darkMode,
        Action<ProfileItem> saveProfile,
        Func<string, string?> deleteProfile)
    {
        InitializeComponent();
        _profiles = profiles?.ToList() ?? new List<ProfileItem>();
        _activeProfileName = activeProfileName ?? "";
        _darkMode = darkMode;
        _saveProfile = saveProfile;
        _deleteProfile = deleteProfile;
        Loaded += ProfileChooserWindow_Loaded;
        KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                DialogResult = false;
                Close();
            }
        };
    }

    private void ProfileChooserWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyTheme();
        RenderProfiles();
    }

    private void ApplyTheme()
    {
        Root.Background = new SolidColorBrush(Colors.White);
        TitleText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#202124")!);
        SubtitleText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#5f6368")!);
    }

    private void RenderProfiles()
    {
        ProfilesPanel.Children.Clear();

        foreach (var profile in _profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            ProfilesPanel.Children.Add(BuildProfileCard(profile));
        }
    }

    private UIElement BuildProfileCard(ProfileItem profile)
    {
        var name = string.IsNullOrWhiteSpace(profile.Name) ? "Profile" : profile.Name.Trim();
        var isActive = string.Equals(name, _activeProfileName, StringComparison.OrdinalIgnoreCase);
        var borderBrush = isActive ? "#c5c7ca" : "#dadce0";
        var background = "#ffffff";
        var hover = "#f1f3f4";

        var button = new Button
        {
            Width = 240,
            Height = 258,
            Margin = new Thickness(12, 10, 12, 10),
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Tag = profile,
            ToolTip = isActive ? "Current profile" : "Open this profile",
            FocusVisualStyle = null,
            OverridesDefaultStyle = true,
            IsTabStop = false
        };

        button.Template = BuildFlatButtonTemplate();

        var card = new Border
        {
            CornerRadius = new CornerRadius(18),
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(background)!),
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(borderBrush)!),
            BorderThickness = new Thickness(isActive ? 1.5 : 1),
            ClipToBounds = true,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 14,
                ShadowDepth = 1,
                Opacity = 0.10,
                Direction = 270
            }
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var menuBtn = new Button
        {
            Content = "⋮",
            Width = 30,
            Height = 30,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(8, 8, 8, 0),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#5f6368")!),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "Profile options"
        };

        var menu = new ContextMenu
        {
            Background = new SolidColorBrush(Colors.White),
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#dadce0")!),
            BorderThickness = new Thickness(1)
        };
        var edit = new MenuItem { Header = "Edit profile" };
        var delete = new MenuItem { Header = "Delete profile" };
        edit.Click += (_, _) => EditProfile(profile);
        delete.Click += (_, _) => DeleteProfile(profile);
        menu.Items.Add(edit);
        menu.Items.Add(delete);
        menuBtn.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new System.Windows.Input.MouseButtonEventHandler((_, e) =>
        {
            e.Handled = true;
            menu.PlacementTarget = menuBtn;
            menu.IsOpen = true;
        }), true);

        root.Children.Add(menuBtn);

        var stack = new StackPanel { Margin = new Thickness(16, 28, 16, 16), VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock
        {
            Text = isActive ? "Your YCB" : name,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#202124")!),
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 14)
        };

        var avatar = CreateAvatar(profile, 94);
        var footer = new TextBlock
        {
            Text = name,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#202124")!),
            Opacity = 0.98,
            FontSize = 13,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 14, 0, 0)
        };
        stack.Children.Add(title);
        stack.Children.Add(avatar);
        stack.Children.Add(footer);
        Grid.SetRow(stack, 1);
        root.Children.Add(stack);
        card.Child = root;
        button.Content = card;

        button.Click += (_, _) =>
        {
            if (_profiles.Count == 0) return;
            SelectedProfile = new ProfileItem
            {
                Name = name,
                Initial = string.IsNullOrWhiteSpace(profile.Initial) ? GetInitial(name) : profile.Initial,
                Icon = profile.Icon ?? "",
                Color = string.IsNullOrWhiteSpace(profile.Color) ? "#5b9bf9" : profile.Color
            };
            DialogResult = true;
            Close();
        };

        button.MouseEnter += (_, _) => card.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hover)!);
        button.MouseLeave += (_, _) => card.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(background)!);
        return button;
    }

    private static ControlTemplate BuildFlatButtonTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Stretch);
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Stretch);
        presenter.SetValue(ContentPresenter.MarginProperty, new Thickness(0));
        template.VisualTree = presenter;
        return template;
    }

    private void EditProfile(ProfileItem profile)
    {
        var editor = new ProfileEditWindow(profile, _darkMode)
        {
            Owner = this
        };

        if (editor.ShowDialog() == true && editor.ResultProfile != null)
        {
            var updated = editor.ResultProfile;
            var current = _profiles.FirstOrDefault(p => string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase));
            if (current != null)
            {
                current.Name = updated.Name;
                current.Initial = updated.Initial;
                current.Icon = updated.Icon;
                current.Color = updated.Color;
            }
            _saveProfile(updated);
            if (string.Equals(_activeProfileName, profile.Name, StringComparison.OrdinalIgnoreCase))
            {
                SelectedProfile = updated;
            }
            RenderProfiles();
        }
    }

    private void DeleteProfile(ProfileItem profile)
    {
        var name = string.IsNullOrWhiteSpace(profile.Name) ? "" : profile.Name.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        var result = MessageBox.Show(
            this,
            $"Delete profile '{name}'? This only removes the profile entry, not your browser data folder.",
            "Delete profile",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        var nextActive = _deleteProfile(name);
        _profiles.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(nextActive))
        {
            RenderProfiles();
        }
        else
        {
            RenderProfiles();
        }
    }

    private UIElement CreateAvatar(ProfileItem profile, double size)
    {
        var icon = (profile.Icon ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(icon))
        {
            if (LooksLikeImage(icon))
            {
                try
                {
                    var image = new Image
                    {
                        Width = size,
                        Height = size,
                        Stretch = Stretch.UniformToFill,
                        Clip = new EllipseGeometry(new Point(size / 2, size / 2), size / 2, size / 2)
                    };
                    image.Source = new BitmapImage(new Uri(icon, UriKind.RelativeOrAbsolute));
                    return image;
                }
                catch { }
            }

        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(string.IsNullOrWhiteSpace(profile.Color) ? "#5b9bf9" : profile.Color)!),
                Child = new TextBlock
                {
                    Text = icon.Length <= 2 ? icon : GetInitial(profile.Name),
                    Foreground = Brushes.White,
                    FontSize = size * 0.40,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center
                }
            };
        }

        return CreateGuestAvatar(size);
    }

    private UIElement CreateGuestAvatar(double size)
    {
        var circle = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#dadce0")!)
        };

        circle.Child = new Viewbox
        {
            Margin = new Thickness(size * 0.15),
            Child = new TextBlock
            {
                Text = "👤",
                FontSize = size * 0.72,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#5f6368")!)
            }
        };
        return circle;
    }

    private bool LooksLikeImage(string value)
    {
        var v = (value ?? "").Trim();
        return v.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               v.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
               v.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) ||
               v.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
               v.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
               v.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
               v.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               v.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ||
               v.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ||
               v.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetInitial(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return "Y";
        return char.ToUpperInvariant(trimmed[0]).ToString();
    }
}
