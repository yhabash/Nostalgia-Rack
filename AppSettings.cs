using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Input;

namespace NostalgiaRack;

/// <summary>Things a keyboard shortcut can do.</summary>
public enum ShortcutAction
{
    PlayPause, PlayPauseAlt, Stop, Next, Previous, SkipBack, SkipForward,
    VolumeUp, VolumeDown, ToggleEq, FoldEq, FoldDynamics, FoldPlaylist, SlimMode, Search, Settings
}

/// <summary>A key with optional Ctrl / Shift / Alt, e.g. "Ctrl+Right".</summary>
public readonly record struct Shortcut(ModifierKeys Modifiers, Key Key)
{
    public static readonly Shortcut None = new(ModifierKeys.None, Key.None);

    public bool IsNone => Key == Key.None;

    public override string ToString()
    {
        if (IsNone) return "";
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        parts.Add(Key switch
        {
            Key.OemComma => ",", Key.OemPeriod => ".", Key.OemPlus => "+", Key.OemMinus => "-",
            Key.Space => "Space",
            >= Key.D0 and <= Key.D9 => ((int)(Key - Key.D0)).ToString(),
            _ => Key.ToString()
        });
        return string.Join("+", parts);
    }

    /// <summary>Reads text like "Ctrl+Shift+Right" back into a shortcut.</summary>
    public static Shortcut Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return None;
        var modifiers = ModifierKeys.None;
        Key key = Key.None;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            string part = raw.Trim();
            switch (part.ToLowerInvariant())
            {
                case "ctrl": modifiers |= ModifierKeys.Control; break;
                case "shift": modifiers |= ModifierKeys.Shift; break;
                case "alt": modifiers |= ModifierKeys.Alt; break;
                case ",": key = Key.OemComma; break;
                case ".": key = Key.OemPeriod; break;
                case "-": key = Key.OemMinus; break;
                default:
                    if (part.Length == 1 && char.IsDigit(part[0])) key = Key.D0 + (part[0] - '0'); // "1" → the 1 key
                    else if (!int.TryParse(part, out _) && Enum.TryParse(part, true, out Key parsed)) key = parsed;
                    break;
            }
        }
        return text.EndsWith("++") ? new(modifiers, Key.OemPlus) : new(modifiers, key);
    }
}

/// <summary>
/// Every setting you can change, plus a few things the player remembers (volume, EQ,
/// which decks are folded). Saved as one block of text (JSON) in the library database.
/// The Podcast Index key and secret are stored separately and encrypted with your Windows
/// account (DPAPI), so they are never in a readable settings file.
/// </summary>
public sealed class AppSettings
{
    // ---------------- Advanced settings ----------------
    public string OutputDeviceId { get; set; } = "";    // "" = Windows default
    public bool ExclusiveMode { get; set; }
    public int SampleRate { get; set; }                 // 0 = automatic
    public int BufferMs { get; set; }                   // 0 = automatic
    public double CrossfadeSeconds { get; set; }        // 0 = gapless
    public int ResamplerQuality { get; set; } = 4;      // 0 (fastest) … 4 (best)
    public string ReplayGainMode { get; set; } = "Off"; // Off / Track / Album (applied from Milestone 7)
    public int NetBufferMs { get; set; } = 5000;
    public int NetPreBufferPercent { get; set; } = 75;
    public string RadioFilter { get; set; } = "HideAdult"; // Off / HideAdult / FamilyOnly
    public int BluetoothLatencyMs { get; set; }         // delays the VU needles to match wireless headphones
    public double SeekStepSeconds { get; set; } = 15;
    public string Theme { get; set; } = "Champagne";
    public Dictionary<string, string> Shortcuts { get; set; } = DefaultShortcuts();

    // ---------------- Remembered between runs ----------------
    public double Volume { get; set; } = 0.8;
    public double Balance { get; set; }
    // EQ banks: each holds the 10 band gains, then the preamp (11 numbers, in dB).
    // EqBank says which is lit: 0 = none (flat), 1 = Bank 1, 2 = Bank 2.
    public int EqBank { get; set; }
    public float[] EqBank1 { get; set; } = new float[11];
    public float[] EqBank2 { get; set; } = new float[11];
    public bool EqOn { get; set; } = true;
    public bool SoftClip { get; set; } = true;
    // Compressor: on/off and the three knob positions (0 … 1). Defaults = gentle levelling.
    // Attack and release are fixed at gentle values (see Compressor.cs).
    public bool CompOn { get; set; }
    public double CompThreshold { get; set; } = 0.55;  // −18 dB
    public double CompRatio { get; set; } = 0.4;       // 2.5 : 1
    public double CompGain { get; set; } = 0.22;       // about +4 dB
    public bool EqFolded { get; set; }
    public bool DynFolded { get; set; }
    public bool PlaylistFolded { get; set; }
    public bool SlimMode { get; set; }
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;

    // ---------------- Secrets (not in the JSON) ----------------
    [System.Text.Json.Serialization.JsonIgnore] public string PodcastIndexKey { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore] public string PodcastIndexSecret { get; set; } = "";

    public static Dictionary<string, string> DefaultShortcuts() => new()
    {
        // The transport keys follow Winamp: Z X C V B, and the arrow keys to skip.
        // Single keys are preferred over combinations (they're quicker to hit).
        [nameof(ShortcutAction.PlayPause)] = "X",
        [nameof(ShortcutAction.PlayPauseAlt)] = "C",
        [nameof(ShortcutAction.Stop)] = "V",
        [nameof(ShortcutAction.Next)] = "B",
        [nameof(ShortcutAction.Previous)] = "Z",
        [nameof(ShortcutAction.SkipBack)] = "Left",
        [nameof(ShortcutAction.SkipForward)] = "Right",
        [nameof(ShortcutAction.VolumeUp)] = "Ctrl+Up",
        [nameof(ShortcutAction.VolumeDown)] = "Ctrl+Down",
        [nameof(ShortcutAction.ToggleEq)] = "Ctrl+E",
        [nameof(ShortcutAction.FoldEq)] = "Ctrl+1",
        [nameof(ShortcutAction.FoldDynamics)] = "Ctrl+3",
        [nameof(ShortcutAction.FoldPlaylist)] = "Ctrl+2",
        [nameof(ShortcutAction.SlimMode)] = "Ctrl+M",
        [nameof(ShortcutAction.Search)] = "Ctrl+F",
        [nameof(ShortcutAction.Settings)] = "Ctrl+,",
    };

    /// <summary>The shortcut for an action (or None).</summary>
    public Shortcut GetShortcut(ShortcutAction action) =>
        Shortcuts.TryGetValue(action.ToString(), out var text) ? Shortcut.Parse(text) : Shortcut.None;

    /// <summary>Which action (if any) a key press means.</summary>
    public ShortcutAction? FindAction(ModifierKeys modifiers, Key key)
    {
        foreach (ShortcutAction action in Enum.GetValues<ShortcutAction>())
        {
            var shortcut = GetShortcut(action);
            if (!shortcut.IsNone && shortcut.Key == key && shortcut.Modifiers == modifiers) return action;
        }
        return null;
    }

    /// <summary>A friendly name for each action, for the settings screen.</summary>
    public static string Describe(ShortcutAction action) => action switch
    {
        ShortcutAction.PlayPause => "Play / pause",
        ShortcutAction.PlayPauseAlt => "Play / pause (second key)",
        ShortcutAction.Stop => "Stop",
        ShortcutAction.Next => "Next song",
        ShortcutAction.Previous => "Previous song",
        ShortcutAction.SkipBack => "Skip back",
        ShortcutAction.SkipForward => "Skip forward",
        ShortcutAction.VolumeUp => "Volume up",
        ShortcutAction.VolumeDown => "Volume down",
        ShortcutAction.ToggleEq => "EQ on / off",
        ShortcutAction.FoldEq => "Fold / unfold the EQ deck",
        ShortcutAction.FoldDynamics => "Fold / unfold the dynamics deck",
        ShortcutAction.FoldPlaylist => "Fold / unfold the playlist deck",
        ShortcutAction.SlimMode => "Slim strip on / off",
        ShortcutAction.Search => "Search",
        ShortcutAction.Settings => "Advanced settings",
        _ => action.ToString()
    };

    // ---------------- Saving and loading ----------------

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        // "NaN" means "no window position saved yet"; JSON needs permission to store it.
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public static AppSettings Load(LibraryDatabase database)
    {
        AppSettings settings;
        try
        {
            string? json = database.GetSetting("app_settings");
            settings = string.IsNullOrEmpty(json) ? new AppSettings() : JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (Exception)
        {
            settings = new AppSettings(); // damaged settings: start fresh rather than crash
        }

        // Version 0.8 moved the transport keys to Winamp's. Keys still on the old defaults are
        // moved across; keys you changed yourself are left alone.
        var oldDefaults = new Dictionary<string, string>
        {
            [nameof(ShortcutAction.PlayPause)] = "Space",
            [nameof(ShortcutAction.Stop)] = "Ctrl+Space",
            [nameof(ShortcutAction.Next)] = "Ctrl+Right",
            [nameof(ShortcutAction.Previous)] = "Ctrl+Left",
            [nameof(ShortcutAction.SkipBack)] = "Shift+Left",
            [nameof(ShortcutAction.SkipForward)] = "Shift+Right",
        };
        var newDefaults = DefaultShortcuts();
        foreach (var (action, old) in oldDefaults)
            if (settings.Shortcuts.TryGetValue(action, out var saved) && saved == old)
                settings.Shortcuts[action] = newDefaults[action];

        // Make sure every action has an entry (new actions in later versions get their default).
        foreach (var (action, shortcut) in DefaultShortcuts())
            settings.Shortcuts.TryAdd(action, shortcut);
        // Sample rate and buffer are always automatic now (the choices were removed from Settings).
        settings.SampleRate = 0;
        settings.BufferMs = 0;
        settings.CrossfadeSeconds = Math.Clamp(settings.CrossfadeSeconds, 0, 20);
        if (settings.EqBank1.Length != 11) settings.EqBank1 = new float[11];
        if (settings.EqBank2.Length != 11) settings.EqBank2 = new float[11];
        if (settings.EqBank is < 0 or > 2) settings.EqBank = 0;

        settings.PodcastIndexKey = Unprotect(database.GetSetting("podcastindex_key"));
        settings.PodcastIndexSecret = Unprotect(database.GetSetting("podcastindex_secret"));
        return settings;
    }

    public void Save(LibraryDatabase database)
    {
        database.SetSetting("app_settings", JsonSerializer.Serialize(this, JsonOptions));
        database.SetSetting("podcastindex_key", Protect(PodcastIndexKey));
        database.SetSetting("podcastindex_secret", Protect(PodcastIndexSecret));
    }

    public AppSettings Copy()
    {
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!;
        copy.PodcastIndexKey = PodcastIndexKey;
        copy.PodcastIndexSecret = PodcastIndexSecret;
        return copy;
    }

    // DPAPI: encrypted so that only your Windows account on this PC can read it back.
    private static string? Protect(string value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        byte[] encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    private static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        try
        {
            byte[] plain = ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception) { return ""; }
    }
}
