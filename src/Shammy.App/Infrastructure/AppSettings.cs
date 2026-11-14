using System.Text.Json;
using System.Text.Json.Serialization;
using Shammy.Core.Notch;
using Shammy.Core.Search;
using Shammy.Core.Settings;
using Microsoft.Win32;

namespace Shammy.Infrastructure;

internal sealed class AppSettings
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Swallow the volume keys and show Shammy's HUD instead of Windows' flyout.</summary>
    public bool ReplaceVolumeFlyout { get; set; } = true;

    public bool ShowVisualizer { get; set; } = true;

    /// <summary>The animated multi-colour shammy around the notch.</summary>
    public bool ShammyGlow { get; set; } = true;

    public SearchMode DefaultSearchMode { get; set; } = SearchMode.Ai;

    /// <summary>Render Google with a phone user agent so its compact layout fits the card.</summary>
    public bool CompactResults { get; set; } = true;

    /// <summary>
    /// false (default): sit at the top of the usable area, below a top taskbar.
    /// true: always on the screen's very top edge, over a top taskbar.
    /// </summary>
    public bool PinToScreenEdge { get; set; }

    /// <summary>Whisper language: "en" is ~4x faster than "auto" (which detects the language per query).</summary>
    public string VoiceLanguage { get; set; } = "en";

    public const string EnglishVoice = "english", HinglishVoice = "hinglish";

    /// <summary>
    /// "english" (default) or "hinglish": the opt-in Hinglish model (Hindi + English, written in Latin
    /// letters), only on PCs whose voice runs on a graphics card.
    /// </summary>
    public string VoiceMode { get; set; } = EnglishVoice;

    /// <summary>The voice model this PC was matched to, e.g. "large-v3-turbo@1" (GPU 1) or "small" (CPU).</summary>
    public string? VoiceEngine { get; set; }

    /// <summary>Set while the one-time hardware probe runs; left set means it crashed, so skip the GPUs.</summary>
    public bool VoiceProbeStarted { get; set; }

    public List<string> Shelf { get; set; } = new();

    /// <summary>
    /// Show other apps' notifications in the notch. Windows' own pop-ups can only be turned off by the
    /// user (Do Not Disturb or per-app banners in Settings); Shammy keeps showing notifications either way.
    /// </summary>
    public bool ShowNotifications { get; set; } = true;

    /// <summary>The one-time "start with Windows?" question has been answered (or startup was already on).</summary>
    public bool StartupAsked { get; set; }

    /// <summary>Shammy's own soft ping for notifications and a chime for timers/reminders.</summary>
    public bool NotchSounds { get; set; } = true;

    /// <summary>Shammy the pet (the eyes on the resting notch); off leaves just the rotating glance ("pet off").</summary>
    public bool PetEnabled { get; set; } = true;

    /// <summary>App IDs the user muted from the notch.</summary>
    public List<string> MutedNotificationApps { get; set; } = new();

    // ---- Settings screen (alarms-settings) ------------------------------------------------------------
    // Everything the Settings card and the settings-get/settings-set pipe can change. Defaults keep today's
    // behaviour; Normalize() puts hand-edited values back in range. SettingsApplier pushes the ones features
    // read from static knobs; the rest are read live where they're used.

    /// <summary>12 or 24-hour times; null follows the Windows region format.</summary>
    public bool? Use24Hour { get; set; }

    public TemperatureUnit TemperatureUnit { get; set; } = TemperatureUnit.Celsius;

    /// <summary>Circled prices convert to this (three-letter code).</summary>
    public string HomeCurrency { get; set; } = "INR";

    /// <summary>English text is translated into this language (Google Translate code; Hindi by default).</summary>
    public string TranslateTarget { get; set; } = "hi";

    public int PomodoroWorkMinutes { get; set; } = 25;
    public int PomodoroShortBreakMinutes { get; set; } = 5;
    public int PomodoroLongBreakMinutes { get; set; } = 15;

    /// <summary>Snooze length for reminders and alarms without their own.</summary>
    public int SnoozeMinutes { get; set; } = 10;

    /// <summary>How long a due reminder's card stays before it closes by itself.</summary>
    public int ReminderCardSeconds { get; set; } = 30;

    /// <summary>How long the pointer has to rest before the peek and the open notch.</summary>
    public HoverSpeed HoverSpeed { get; set; } = HoverSpeed.Normal;

    /// <summary>The glanceable peek on a resting pointer; off opens the notch after the whole wait instead.</summary>
    public bool PeekEnabled { get; set; } = true;

    /// <summary>A small tab under the notch slides it away for a few seconds, to reach what is under it.</summary>
    public bool TuckAwayEnabled { get; set; } = true;

    /// <summary>Wheel and swipe over the notch (volume, next/previous track).</summary>
    public bool GesturesEnabled { get; set; } = true;

    /// <summary>The microphone's name (as Windows lists it); null uses the Windows default.</summary>
    public string? MicrophoneDeviceId { get; set; }

    public bool ClipboardHistoryEnabled { get; set; } = true;

    /// <summary>Unpinned clipboard history is forgotten after this many hours.</summary>
    public int ClipboardRetentionHours { get; set; } = 24;

    /// <summary>The short "copied" preview in the notch.</summary>
    public bool CopyPreviewEnabled { get; set; } = true;

    /// <summary>How long another app's notification shows in the notch.</summary>
    public int NotificationPopupSeconds { get; set; } = 6;

    /// <summary>Show who a notification is from but never what it says, even when not presenting.</summary>
    public bool AlwaysHideNotificationText { get; set; }

    /// <summary>Battery (charging, low) and Bluetooth connect pills.</summary>
    public bool SystemAlertsEnabled { get; set; } = true;

    /// <summary>0–100: the ping, the timer/reminder chime and the alarm.</summary>
    // 60% for new installs; saved settings keep their volume.
    public int NotchSoundVolume { get; set; } = 60;

    /// <summary>Modules (by name, e.g. "music", "clipboard") that don't start. Takes effect on the next start.</summary>
    public List<string> DisabledModules { get; set; } = new();

    // ---- Pet, welcome tour, alarm extras (alarm-settings-tour) ---------------------------------------

    /// <summary>The pet's eye shape (Settings → Notch &amp; pet); every mood keeps it.</summary>
    public PetEyeStyle PetEyeStyle { get; set; } = PetEyeStyle.Round;

    public PetEyeColor PetEyeColor { get; set; } = PetEyeColor.White;

    /// <summary>A cat (ears, slit pupils, whiskers and a nose around the eyes) or the plain eyes.</summary>
    // The plain eyes are the default; the cat is a choice (settings saved with Cat keep it).
    public PetKind PetKind { get; set; } = PetKind.Eyes;

    /// <summary>The cat purrs softly when the pointer rests on it (also needs notch sounds on).</summary>
    public bool PetPurrs { get; set; } = true;

    /// <summary>Cat touches beyond the pet: the cat listens while you talk, paws lead the "Did you mean" chips.</summary>
    public bool CatTheme { get; set; }

    /// <summary>Hold the middle mouse button for the wheel: eight actions around the pointer.</summary>
    public bool WheelEnabled { get; set; } = true;

    /// <summary>Hold Left Alt and point: a bubble translates, converts or explains the words under the pointer.</summary>
    public bool HoverToUnderstand { get; set; } = true;

    // "DockEnabled" (the dock, since removed) may still be in older settings.json files: unknown names are ignored on load.

    /// <summary>One line at the top of the open notch with what matters right now (a meeting soon, Claude waiting…).</summary>
    public bool NowLineEnabled { get; set; } = true;

    /// <summary>A one-time code in a message shows big in the notch with Copy, for two minutes.</summary>
    public bool OtpEnabled { get; set; } = true;

    /// <summary>Starting a focus session also plays focus music (when nothing is playing).</summary>
    public bool FocusMusic { get; set; }

    /// <summary>The first unlock of a morning: the weather, meetings, reminders, said aloud with a Gnani key.</summary>
    public bool MorningBrief { get; set; } = true;

    /// <summary>What the pet calls itself in the tour and on Settings; picked in the tour's last step.</summary>
    public string PetName { get; set; } = PetLooks.DefaultName;

    /// <summary>The first-run welcome tour has been shown (finished, skipped or left); the tray can replay it.</summary>
    public bool TourSeen { get; set; }

    // ---- Look and place (Settings → Notch & pet) ----------------------------------------------------

    /// <summary>Lively plays the bounce, the spinning record and the pet's extras; Calm holds still.</summary>
    public Shammy.Core.Settings.MotionStyle Motion { get; set; }

    /// <summary>Scales everything in the notch.</summary>
    public Shammy.Core.Settings.NotchSize NotchSize { get; set; } = Shammy.Core.Settings.NotchSize.Normal;

    /// <summary>The accent picked before palettes existed; only read once, to move the user to the closest palette.</summary>
    public Shammy.Core.Settings.AccentColor Accent { get; set; }

    /// <summary>
    /// The island's palette (see IslandPalettes): "album" (the default: the song's colours), "monsoon", "neon-dusk",
    /// "northern", "holo" or "frost". Its first colour is the app's accent.
    /// </summary>
    public string? Palette { get; set; }

    /// <summary>"main", "pointer" (the screen the pointer is on), or a screen's device name.</summary>
    public string NotchDisplay { get; set; } = Shammy.Core.Settings.NotchDisplay.Main;

    /// <summary>Apps (by plain name, "photoshop") the notch stays away from while they are in front.</summary>
    public List<string> HiddenApps { get; set; } = new();

    /// <summary>The lyric being sung shows under the record while music plays (songs with synced lyrics only).</summary>
    public bool LyricLineEnabled { get; set; } = true;

    /// <summary>The version that last told the user what's new; a newer one shows its note once.</summary>
    public string? LastRunVersion { get; set; }

    /// <summary>A ringing alarm swells from quiet to full over 30 s; off rings at full volume straight away.</summary>
    public bool AlarmGentleWake { get; set; } = true;

    /// <summary>A ringing alarm shows over fullscreen apps (games, videos, slides); off waits until fullscreen ends.</summary>
    public bool AlarmBreaksFullscreen { get; set; } = true;

    // ---- Nudges (eye breaks, water) -------------------------------------------------------------------

    /// <summary>After every 20 minutes of active screen time the pet closes its eyes with you for 20 seconds.</summary>
    // Off for new installs (a choice in Settings › Routines); saved settings keep theirs.
    public bool EyeBreaksEnabled { get; set; }

    /// <summary>The pet asks about water every 90 minutes of active time.</summary>
    public bool WaterRemindersEnabled { get; set; }

    // ---- Live cricket -------------------------------------------------------------------------------

    /// <summary>
    /// While India plays, the pet shows the score and the Cricket card joins the rail. Off, nothing is fetched
    /// for it. "ticker cricket" turns it on with the ticker.
    /// </summary>
    public bool LiveCricketEnabled { get; set; }

    // ---- Online voice (Gnani) -------------------------------------------------------------------------
    // The key itself is not here: it lives in the module's encrypted store, never in settings.json.

    /// <summary>Timers, alarms and reminders are said aloud through Gnani (needs a saved key).</summary>
    public bool OnlineVoiceSpeaks { get; set; }

    /// <summary>The talk key's recording is transcribed by Gnani instead of the local model (needs a saved key).</summary>
    public bool OnlineVoiceListens { get; set; }

    /// <summary>The Gnani voice that speaks (see GnaniVoices); the Hinglish one by default.</summary>
    public string OnlineVoiceName { get; set; } = Shammy.Core.OnlineVoice.GnaniVoices.Default;

    // ---- Claude Code (coding agents) ----------------------------------------------------------------

    /// <summary>
    /// When a Claude Code session needs an OK, a card with Deny and Allow drops from the notch. Off, the notch
    /// only shows that the session is waiting and Claude Code asks in its own window.
    /// </summary>
    public bool AgentsAskInNotch { get; set; } = true;

    /// <summary>Raised (with the property name) after the settings card or pipe changed a setting.</summary>
    public event Action<string>? Changed;

    public void NotifyChanged(string name) => Changed?.Invoke(name);

    /// <summary>Lets one app's notifications show again; true if it was muted.</summary>
    public bool UnmuteApp(string appId)
    {
        int removed = MutedNotificationApps.RemoveAll(a => string.Equals(a, appId, StringComparison.OrdinalIgnoreCase));
        if (removed > 0) Save();
        return removed > 0;
    }

    /// <summary>Puts every setting back in range (a hand-edited file, or a value from the pipe).</summary>
    public void Normalize()
    {
        Shelf ??= new();
        MutedNotificationApps ??= new();
        DisabledModules ??= new();
        DisabledModules = DisabledModules.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim().ToLowerInvariant()).Distinct().ToList();
        if (string.IsNullOrWhiteSpace(VoiceLanguage)) VoiceLanguage = "en";
        if (VoiceMode != HinglishVoice) VoiceMode = EnglishVoice;
        // Lens needs a picture; as the default for typed and spoken searches it means the AI answer.
        if (DefaultSearchMode == SearchMode.Lens || !Enum.IsDefined(DefaultSearchMode)) DefaultSearchMode = SearchMode.Ai;
        if (!Enum.IsDefined(TemperatureUnit)) TemperatureUnit = TemperatureUnit.Celsius;
        if (!Enum.IsDefined(HoverSpeed)) HoverSpeed = HoverSpeed.Normal;
        HomeCurrency = SettingLimits.CurrencyCode(HomeCurrency);
        TranslateTarget = SettingLimits.LanguageCode(TranslateTarget);
        PomodoroWorkMinutes = SettingLimits.PomodoroMinutes(PomodoroWorkMinutes);
        PomodoroShortBreakMinutes = SettingLimits.PomodoroMinutes(PomodoroShortBreakMinutes);
        PomodoroLongBreakMinutes = SettingLimits.PomodoroMinutes(PomodoroLongBreakMinutes);
        SnoozeMinutes = SettingLimits.SnoozeMinutes(SnoozeMinutes);
        ReminderCardSeconds = SettingLimits.ReminderCardSeconds(ReminderCardSeconds);
        ClipboardRetentionHours = SettingLimits.ClipboardRetentionHours(ClipboardRetentionHours);
        NotificationPopupSeconds = SettingLimits.NotificationPopupSeconds(NotificationPopupSeconds);
        NotchSoundVolume = SettingLimits.Volume(NotchSoundVolume);
        if (string.IsNullOrWhiteSpace(MicrophoneDeviceId)) MicrophoneDeviceId = null;
        if (!Enum.IsDefined(PetEyeStyle)) PetEyeStyle = PetEyeStyle.Round;
        if (!Enum.IsDefined(PetEyeColor)) PetEyeColor = PetEyeColor.White;
        if (!Enum.IsDefined(PetKind)) PetKind = PetKind.Eyes;
        PetName = PetLooks.Name(PetName);
        if (!Enum.IsDefined(Motion)) Motion = Shammy.Core.Settings.MotionStyle.Lively;
        if (!Enum.IsDefined(NotchSize)) NotchSize = Shammy.Core.Settings.NotchSize.Normal;
        if (!Enum.IsDefined(Accent)) Accent = Shammy.Core.Settings.AccentColor.Orange;
        Palette = IslandPalettes.Migrate(Palette, Accent);
        NotchDisplay = Shammy.Core.Settings.NotchDisplay.Normalize(NotchDisplay);
        HiddenApps = Shammy.Core.Settings.HideRules.Clean(HiddenApps);
    }

    [JsonIgnore]
    public bool StartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("Shammy") is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue("Shammy", $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue("Shammy", false);
        }
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// The startup entry used to be called "Halo" and point at Halo.exe. Replace it with "Shammy" pointing at
    /// this exe, and repair a "Shammy" entry whose exe no longer exists (it moved or was renamed).
    /// </summary>
    public static void MigrateStartupEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null || Environment.ProcessPath is not { } exe) return;
            // Only the installed copy may claim the startup entry (a dev build would otherwise take it over).
            if (!exe.Contains(@"\HaloNotch\current\", StringComparison.OrdinalIgnoreCase)) return;
            bool wanted = key.GetValue(OldRunName) is string || key.GetValue("Shammy") is string old && !File.Exists(old.Trim('"'));
            key.DeleteValue(OldRunName, false);
            if (wanted) key.SetValue("Shammy", $"\"{exe}\"");
        }
        catch (Exception ex)
        {
            Log.Warn("Updating the startup entry failed", ex);
        }
    }

    const string OldRunName = "Halo";

    /// <summary>Called by the uninstaller, before Shammy's files are removed.</summary>
    public static void RemoveStartupEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue("Shammy", false);
            key?.DeleteValue(OldRunName, false);
        }
        catch (Exception)
        {
            // Uninstall must not fail over this.
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Paths.SettingsFile))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(ReadSettingsText(), Json) ?? new AppSettings();
                // A hand-edited or half-written file can say null (every list must exist for the rest of Shammy)
                // or a value out of range.
                s.Normalize();
                return s;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn("Settings unreadable, using defaults", ex);
            KeepUnreadable();
        }
        return new AppSettings();
    }

    /// <summary>A file another program holds open for a moment (a sync client, an antivirus scan) is read again.</summary>
    static string ReadSettingsText()
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return File.ReadAllText(Paths.SettingsFile); }
            catch (IOException) when (attempt < 4) { Thread.Sleep(100); }
        }
    }

    /// <summary>
    /// The first save after starting on defaults would overwrite the unreadable file, and with it the user's pins,
    /// voice choice and muted apps. Keep a copy beside it so a hand-edit gone wrong can be put right.
    /// </summary>
    static void KeepUnreadable()
    {
        try
        {
            var kept = Paths.SettingsFile + ".unreadable";
            File.Copy(Paths.SettingsFile, kept, overwrite: true);
            Log.Warn($"Kept the unreadable settings as {Path.GetFileName(kept)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Couldn't keep a copy of the unreadable settings", ex);
        }
    }

    public void Save()
    {
        try
        {
            var tmp = Paths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, Paths.SettingsFile, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // e.g. settings.json marked read-only: keep running with the in-memory settings.
            Log.Warn("Settings save failed", ex);
        }
    }
}
