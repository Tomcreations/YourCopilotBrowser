using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace YCB;

public partial class ProfileEditWindow : Window
{
    private readonly bool _darkMode;
    private readonly ProfileItem _profile;
    public ProfileItem? ResultProfile { get; private set; }

    public ProfileEditWindow(ProfileItem profile, bool darkMode)
    {
        InitializeComponent();
        _profile = profile ?? new ProfileItem();
        _darkMode = darkMode;
        Loaded += ProfileEditWindow_Loaded;
        KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                DialogResult = false;
                Close();
            }
        };
    }

    private void ProfileEditWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyTheme();
        NameBox.Text = string.IsNullOrWhiteSpace(_profile.Name) ? Environment.UserName : _profile.Name.Trim();
        IconBox.Text = _profile.Icon ?? "";
        NameBox.SelectAll();

        CancelBtn.Click += (_, _) =>
        {
            DialogResult = false;
            Close();
        };

        BrowseBtn.Click += (_, _) => BrowseForIcon();
        SaveBtn.Click += (_, _) => SaveProfile();
    }

    private void ApplyTheme()
    {
        if (_darkMode)
        {
            Root.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#202124")!);
            TitleText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#f8f9fa")!);
            SubtitleText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#f8f9fa")!);
        }
        else
        {
            Root.Background = Brushes.White;
            TitleText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#111111")!);
            SubtitleText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#111111")!);
        }

        foreach (var control in new Control[] { NameBox, IconBox, CancelBtn, SaveBtn, BrowseBtn })
        {
            control.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_darkMode ? "#f8f9fa" : "#111111")!);
        }

        NameBox.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_darkMode ? "#303134" : "#ffffff")!);
        NameBox.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_darkMode ? "#5f6368" : "#dfe1e5")!);
        NameBox.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_darkMode ? "#f8f9fa" : "#111111")!);
        IconBox.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_darkMode ? "#303134" : "#ffffff")!);
        IconBox.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_darkMode ? "#5f6368" : "#dfe1e5")!);
        IconBox.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_darkMode ? "#f8f9fa" : "#111111")!);
        CancelBtn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_darkMode ? "#303134" : "#f8f9fa")!);
        CancelBtn.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_darkMode ? "#5f6368" : "#dfe1e5")!);
        SaveBtn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1a73e8")!);
        SaveBtn.BorderBrush = Brushes.Transparent;
        SaveBtn.Foreground = Brushes.White;
        BrowseBtn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_darkMode ? "#303134" : "#f8f9fa")!);
        BrowseBtn.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_darkMode ? "#5f6368" : "#dfe1e5")!);
    }

    private void BrowseForIcon()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a profile icon",
            Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All files|*.*"
        };
        if (dlg.ShowDialog() == true)
        {
            IconBox.Text = dlg.FileName;
        }
    }

    private void SaveProfile()
    {
        var name = (NameBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            NameBox.Focus();
            return;
        }

        ResultProfile = new ProfileItem
        {
            Name = name,
            Initial = string.IsNullOrWhiteSpace(_profile.Initial) ? GetInitial(name) : _profile.Initial,
            Icon = (IconBox.Text ?? "").Trim(),
            Color = string.IsNullOrWhiteSpace(_profile.Color) ? "#5b9bf9" : _profile.Color
        };
        DialogResult = true;
        Close();
    }

    private static string GetInitial(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return "Y";
        return char.ToUpperInvariant(trimmed[0]).ToString();
    }
}
