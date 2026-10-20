using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace NostalgiaRack;

/// <summary>
/// Two small pop-up windows, built in code so they don't need their own XAML files:
/// - AskForName: "Save playlist as…" with a text box
/// - EditLibraryFolders: the list of music folders the library scans
/// They get the champagne skin together with everything else in Milestone 6.
/// </summary>
public static class Dialogs
{
    private static readonly Brush PanelBrush = new SolidColorBrush(Color.FromRgb(0xE2, 0xD5, 0xB6));

    /// <summary>Asks for a name. Returns the name, or null if the user cancelled.</summary>
    public static string? AskForName(Window owner, string title, string prompt, string initial = "")
    {
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 6, 0, 12), Padding = new Thickness(4, 2, 4, 2) };
        var ok = new Button { Content = "OK", Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };

        var window = new Window
        {
            Title = title,
            Owner = owner,
            Width = 360,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = PanelBrush,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Children =
                {
                    new TextBlock { Text = prompt },
                    box,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { ok, cancel }
                    }
                }
            }
        };

        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };

        if (window.ShowDialog() != true) return null;
        string name = box.Text.Trim();
        return name.Length == 0 ? null : name;
    }

    /// <summary>
    /// Shows the library folders with Add… and Remove buttons.
    /// Returns true if something changed (so the library should be rescanned).
    /// </summary>
    public static bool EditLibraryFolders(Window owner, LibraryDatabase database)
    {
        bool changed = false;
        var list = new ListBox { Height = 180, Margin = new Thickness(0, 6, 0, 10) };
        void Refresh() => list.ItemsSource = database.GetLibraryFolders();
        Refresh();

        var add = new Button { Content = "Add folder…", Width = 100, Margin = new Thickness(0, 0, 8, 0) };
        var remove = new Button { Content = "Remove", Width = 80, Margin = new Thickness(0, 0, 8, 0) };
        var close = new Button { Content = "Close", Width = 80, IsCancel = true, IsDefault = true };

        var window = new Window
        {
            Title = "Library folders",
            Owner = owner,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = PanelBrush,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Children =
                {
                    new TextBlock
                    {
                        Text = "Music in these folders (and their sub-folders) is added to your library.",
                        TextWrapping = TextWrapping.Wrap
                    },
                    list,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { add, remove, close }
                    }
                }
            }
        };

        add.Click += (_, _) =>
        {
            var dialog = new OpenFolderDialog { Title = "Add a music folder to the library", Multiselect = true };
            if (dialog.ShowDialog(window) != true) return;
            foreach (var folder in dialog.FolderNames) database.AddLibraryFolder(folder);
            changed = true;
            Refresh();
        };

        remove.Click += (_, _) =>
        {
            if (list.SelectedItem is not string folder) return;
            database.RemoveLibraryFolder(folder);
            changed = true;
            Refresh();
        };

        close.Click += (_, _) => window.Close();
        window.ShowDialog();
        return changed;
    }
}
