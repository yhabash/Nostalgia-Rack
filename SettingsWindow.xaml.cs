using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;

namespace NostalgiaRack;

/// <summary>
/// Advanced settings. Works on a copy of the settings, so Cancel really changes nothing.
/// APPLY hands the copy to the player without closing; OK applies and closes.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly LibraryDatabase _database;
    private readonly Dictionary<ShortcutAction, TextBox> _shortcutBoxes = new();
    private List<(string Key, string Text)> _filterChoices = new();

    /// <summary>The player listens to this, and applies the settings when it fires.</summary>
    public event Action<AppSettings>? Applied;

    /// <summary>True if you changed the music folders (the player then re-scans the library).</summary>
    public bool LibraryFoldersChanged { get; private set; }

    // A choice in a drop-down: what you see, and the number behind it.
    private sealed record Choice(string Text, double Value)
    {
        public override string ToString() => Text;
    }

    public SettingsWindow(AppSettings settings, LibraryDatabase database)
    {
        InitializeComponent();
        _settings = settings;
        _database = database;
        FillControls();
    }

    // ================= Putting the current settings on screen =================

    private void FillControls()
    {
        // Output devices: "Windows default" first, then everything Windows lists.
        var devices = new List<OutputDevice> { new("", "Windows default (follows Windows)", false) };
        devices.AddRange(AudioEngine.GetOutputDevices());
        DeviceCombo.ItemsSource = devices;
        DeviceCombo.SelectedItem = devices.FirstOrDefault(d => d.Id == _settings.OutputDeviceId) ?? devices[0];

        SharedMode.IsChecked = !_settings.ExclusiveMode;
        ExclusiveMode.IsChecked = _settings.ExclusiveMode;

        Fill(ResamplerCombo, _settings.ResamplerQuality,
             ("0 · Fastest", 0), ("1 · Good", 1), ("2 · Better", 2), ("3 · High", 3), ("4 · Best (recommended)", 4));

        CrossfadeSlider.Value = Math.Clamp(Math.Round(_settings.CrossfadeSeconds), 0, 20);
        ShowCrossfade();

        Fill(ReplayGainCombo, 0, ("Off", 0), ("Track", 1), ("Album", 2));
        Fill(SeekStepCombo, _settings.SeekStepSeconds,
             ("5 seconds", 5), ("10 seconds", 10), ("15 seconds", 15), ("30 seconds", 30), ("60 seconds", 60));
        Fill(BluetoothCombo, _settings.BluetoothLatencyMs,
             ("None (wired)", 0), ("100 ms", 100), ("150 ms", 150), ("200 ms (typical Bluetooth)", 200),
             ("250 ms", 250), ("300 ms", 300), ("400 ms", 400));

        Fill(NetBufferCombo, _settings.NetBufferMs,
             ("2 seconds", 2000), ("5 seconds (recommended)", 5000), ("10 seconds", 10000), ("20 seconds", 20000));
        Fill(PreBufferCombo, _settings.NetPreBufferPercent,
             ("25% of the buffer (starts sooner)", 25), ("50% of the buffer", 50),
             ("75% of the buffer (recommended)", 75), ("100% of the buffer (steadiest)", 100));

        _filterChoices = new List<(string Key, string Text)>
        {
            ("Off", "Off: show every station"),
            ("HideAdult", "Hide adult and explicit stations (recommended)"),
            ("FamilyOnly", "Only kids' and family stations")
        };
        RadioFilterCombo.ItemsSource = _filterChoices.Select(c => c.Text).ToList();
        RadioFilterCombo.SelectedIndex = Math.Max(0, _filterChoices.FindIndex(c => c.Key == _settings.RadioFilter));

        ThemeCombo.ItemsSource = new[] { "Champagne & Black Glass" };
        ThemeCombo.SelectedIndex = 0;

        PodcastKeyBox.Text = _settings.PodcastIndexKey;
        PodcastSecretBox.Password = _settings.PodcastIndexSecret;

        ShowFolderCount();
        BuildShortcutRows();
    }

    // Fills a drop-down and picks the entry closest to the current value
    // (so a value from an older version still shows something sensible).
    private static void Fill(ComboBox combo, double current, params (string text, double value)[] choices)
    {
        var list = choices.Select(c => new Choice(c.text, c.value)).ToList();
        combo.ItemsSource = list;
        combo.SelectedItem = list.OrderBy(c => Math.Abs(c.Value - current)).First();
    }

    private static double Picked(ComboBox combo, double fallback) =>
        combo.SelectedItem is Choice choice ? choice.Value : fallback;

    private void ShowFolderCount()
    {
        int count = _database.GetLibraryFolders().Count;
        FolderCountText.Text = count == 0 ? "No folders chosen yet" : $"{count} folder{(count == 1 ? "" : "s")}";
    }

    // ================= Keyboard shortcuts =================

    private void BuildShortcutRows()
    {
        ShortcutPanel.Children.Clear();
        _shortcutBoxes.Clear();

        foreach (ShortcutAction action in Enum.GetValues<ShortcutAction>())
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            row.ColumnDefinitions.Add(new ColumnDefinition());

            var label = new TextBlock { Text = AppSettings.Describe(action), Style = (Style)FindResource("SettingLabel") };

            var box = new TextBox
            {
                Style = (Style)FindResource("FieldText"),
                Width = 180,
                IsReadOnly = true,
                IsReadOnlyCaretVisible = false,
                Tag = "NONE",
                Text = ShortcutText(_settings.GetShortcut(action)),
                ToolTip = "Click, then press the keys you want",
                Cursor = Cursors.Hand
            };
            box.PreviewKeyDown += (_, e) => CaptureShortcut(action, box, e);
            box.GotKeyboardFocus += (_, _) => { box.Text = "PRESS KEYS…"; ShortcutMessage.Text = ""; };
            box.LostKeyboardFocus += (_, _) => box.Text = ShortcutText(_settings.GetShortcut(action));
            Grid.SetColumn(box, 1);

            var clear = new Button
            {
                Style = (Style)FindResource("TextKey"),
                Content = "CLEAR",
                Width = 56,
                HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = "No shortcut for this"
            };
            clear.Click += (_, _) => SetShortcut(action, Shortcut.None);
            Grid.SetColumn(clear, 2);

            row.Children.Add(label);
            row.Children.Add(box);
            row.Children.Add(clear);
            ShortcutPanel.Children.Add(row);
            _shortcutBoxes[action] = box;
        }
    }

    private static string ShortcutText(Shortcut shortcut) => shortcut.IsNone ? "" : shortcut.ToString().ToUpperInvariant();

    private void CaptureShortcut(ShortcutAction action, TextBox box, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Tab moves on as usual; Escape leaves the box unchanged.
        if (key == Key.Tab) { e.Handled = false; return; }
        if (key == Key.Escape) { Keyboard.ClearFocus(); ShortcutPanel.Focus(); return; }

        // Wait for a real key: pressing Ctrl or Shift alone isn't a shortcut yet.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or
                   Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;

        var shortcut = new Shortcut(Keyboard.Modifiers, key);

        SetShortcut(action, shortcut);
        box.Text = ShortcutText(shortcut);
    }

    // Gives the shortcut to this action, taking it away from any other action that had it.
    private void SetShortcut(ShortcutAction action, Shortcut shortcut)
    {
        ShortcutMessage.Text = "";
        if (!shortcut.IsNone)
        {
            foreach (ShortcutAction other in Enum.GetValues<ShortcutAction>())
            {
                if (other == action || _settings.GetShortcut(other) != shortcut) continue;
                _settings.Shortcuts[other.ToString()] = "";
                _shortcutBoxes[other].Text = "";
                ShortcutMessage.Text = $"{shortcut} was used for \"{AppSettings.Describe(other)}\". That one has no shortcut now.";
            }
        }
        _settings.Shortcuts[action.ToString()] = shortcut.IsNone ? "" : shortcut.ToString();
        _shortcutBoxes[action].Text = ShortcutText(shortcut);
    }

    private void ResetShortcuts_Click(object sender, RoutedEventArgs e)
    {
        _settings.Shortcuts = AppSettings.DefaultShortcuts();
        foreach (var (action, box) in _shortcutBoxes) box.Text = ShortcutText(_settings.GetShortcut(action));
        ShortcutMessage.Text = "All shortcuts are back to the originals.";
    }

    // ================= Buttons =================

    private void LibraryFolders_Click(object sender, RoutedEventArgs e)
    {
        if (Dialogs.EditLibraryFolders(this, _database)) LibraryFoldersChanged = true;
        ShowFolderCount();
    }

    // Copies what's on screen into the settings.
    private AppSettings Collect()
    {
        _settings.OutputDeviceId = (DeviceCombo.SelectedItem as OutputDevice)?.Id ?? "";
        _settings.ExclusiveMode = ExclusiveMode.IsChecked == true;
        _settings.SampleRate = 0;   // always automatic
        _settings.BufferMs = 0;     // always automatic
        _settings.ResamplerQuality = (int)Picked(ResamplerCombo, 4);
        _settings.CrossfadeSeconds = CrossfadeSlider.Value;
        _settings.SeekStepSeconds = Picked(SeekStepCombo, 15);
        _settings.BluetoothLatencyMs = (int)Picked(BluetoothCombo, 0);
        _settings.NetBufferMs = (int)Picked(NetBufferCombo, 5000);
        _settings.NetPreBufferPercent = (int)Picked(PreBufferCombo, 75);
        _settings.Theme = "Champagne";
        if (RadioFilterCombo.SelectedIndex >= 0) _settings.RadioFilter = _filterChoices[RadioFilterCombo.SelectedIndex].Key;
        _settings.PodcastIndexKey = PodcastKeyBox.Text.Trim();
        _settings.PodcastIndexSecret = PodcastSecretBox.Password.Trim();
        return _settings.Copy(); // the player gets its own copy; this window keeps working on ours
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => Applied?.Invoke(Collect());

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Applied?.Invoke(Collect());
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Title_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch (InvalidOperationException) { }
    }

    private void CrossfadeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => ShowCrossfade();

    private void ShowCrossfade()
    {
        if (CrossfadeText == null) return;
        int seconds = (int)Math.Round(CrossfadeSlider.Value);
        CrossfadeText.Text = seconds == 0 ? "GAPLESS" : $"{seconds} S";
    }

    // The mouse wheel scrolls this page at half the usual speed (24 px per notch instead of 48).
    private void SettingsScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        SettingsScroll.ScrollToVerticalOffset(SettingsScroll.VerticalOffset - e.Delta / 120.0 * 24);
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception) { /* no browser */ }
        e.Handled = true;
    }
}
