namespace Shammy.Core.Wheel;

/// <summary>Something the wheel can do. <see cref="Words"/> are what a typed or spoken command for it starts with.</summary>
public sealed record WheelAction(string Id, string Name, string Words);

/// <summary>A slot's saved state: the action it holds, and whether the user pinned it there.</summary>
public sealed record WheelSlot(string ActionId, bool Pinned);

/// <summary>
/// The six beads: the droplets the wheel shows when it isn't following the moment. They start as the six in
/// <see cref="Defaults"/>; unpinned beads then follow what is used most
/// (wheel picks and commands, fading with a two-week half-life), while pinned beads never move. An action already
/// on the wheel keeps its bead, so beads don't shuffle; a newcomer takes the weakest unpinned bead only once it is
/// clearly used more.
/// </summary>
public static class WheelSlots
{
    public static readonly IReadOnlyList<WheelAction> All = new WheelAction[]
    {
        new("screenshot", "Screenshot", "screenshot|screen shot|take a screenshot|snip"),
        new("record", "Record", "record|start recording|screen recording"),
        new("timer", "Timer", "timer|set a timer|start a timer|stopwatch"),
        new("music", "Music", "play|pause|next song|previous song|music|song|resume"),
        new("notes", "Notes", "note|notes|show my notes|remind me"),
        new("ask", "Ask Shammy", ""),
        new("phone", "To phone", "send|send to phone|send this to my phone"),
        new("awake", "Keep awake", "keep my pc awake|keep awake|stay awake|stop keeping awake"),
        new("mirror", "Mirror", "mirror|open camera"),
        new("captions", "Captions", "captions|turn on captions|live captions|subtitles"),
        new("clipboard", "Clipboard", "clipboard|clipboard history"),
        new("calendar", "Calendar", "calendar|what's on my calendar|next meeting"),
        new("weather", "Weather", "weather|will it rain"),
        new("mixer", "Volume mixer", "mixer|volume mixer"),
        new("prompter", "Stage", "stage|teleprompter|prompter"),
        new("settings", "Settings", "shammy settings"),
        new("guide", "Guide", "what can you do|help|features|guide"),
        new("focus", "Focus", "focus|start focus|focus mode|focus session"),
    };

    /// <summary>
    /// Actions only the moment's droplets offer (see <see cref="WheelMoments"/>): they can't be pinned and don't
    /// learn. Stage opens the teleprompter, Share link sends to the phone, Music card opens the music card.
    /// </summary>
    public static readonly IReadOnlyList<WheelAction> MomentOnly = new WheelAction[]
    {
        new("stage", "Stage", ""),
        new("mute", "Mute mic", ""),
        new("share", "Share link", ""),
        new("playpause", "Play/pause", ""),
        new("next", "Next", ""),
        new("previous", "Previous", ""),
        new("lyrics", "Lyrics", ""),
        new("musiccard", "Music card", ""),
        new("snippets", "Snippets", ""),
        new("translate", "Translate", ""),
    };

    public static readonly IReadOnlyList<string> Defaults = new[] { "screenshot", "record", "timer", "music", "notes", "ask" };

    public static readonly TimeSpan HalfLife = TimeSpan.FromDays(14);

    /// <summary>A newcomer needs this much more use than the weakest unpinned bead to take its place.</summary>
    public const double SwapMargin = 1.5;

    /// <summary>...and at least this many recent uses more, so one try never reshuffles the wheel.</summary>
    public const double MinUses = 2;

    /// <summary>A bead action (pinnable, learns from use), or null.</summary>
    public static WheelAction? ById(string? id) => All.FirstOrDefault(a => a.Id == id);

    /// <summary>Any action a droplet can hold: a bead action or a moment-only one.</summary>
    public static WheelAction? Find(string? id) => ById(id) ?? MomentOnly.FirstOrDefault(a => a.Id == id);

    /// <summary>The action a typed or spoken command belongs to ("timer 10 minutes" → timer), or null.</summary>
    public static string? ActionFor(string? text)
    {
        var t = (text ?? "").Trim().ToLowerInvariant();
        if (t.Length == 0) return null;
        WheelAction? best = null;
        int bestLength = 0;
        foreach (var a in All)
            foreach (var w in a.Words.Split('|', StringSplitOptions.RemoveEmptyEntries))
                if ((t == w || t.StartsWith(w + " ", StringComparison.Ordinal)) && w.Length > bestLength)
                {
                    best = a;
                    bestLength = w.Length;
                }
        return best?.Id;
    }

    /// <summary>A use's weight now: 1 for a use today, 0.5 two weeks ago.</summary>
    public static double Weight(DateTime used, DateTime now) => Math.Pow(0.5, Math.Max(0, (now - used).TotalDays) / HalfLife.TotalDays);

    /// <summary>
    /// The beads now. <paramref name="saved"/> is what the beads held last time (any length; unknown ids
    /// are ignored), <paramref name="uses"/> each action's uses.
    /// </summary>
    public static IReadOnlyList<WheelSlot> Arrange(IReadOnlyList<WheelSlot>? saved, IReadOnlyDictionary<string, IReadOnlyList<DateTime>> uses, DateTime now)
    {
        var slots = new WheelSlot[WheelGeometry.Beads];
        for (int i = 0; i < slots.Length; i++)
        {
            var s = saved is not null && i < saved.Count && ById(saved[i].ActionId) is not null ? saved[i] : new WheelSlot(Defaults[i], false);
            slots[i] = s;
        }
        // A duplicate (two beads, one action) keeps the first; the other goes back to a default not on the wheel.
        var seen = new HashSet<string>();
        for (int i = 0; i < slots.Length; i++)
        {
            if (seen.Add(slots[i].ActionId)) continue;
            var free = Defaults.Concat(All.Select(a => a.Id)).First(id => !slots.Any(s => s.ActionId == id));
            slots[i] = new WheelSlot(free, false);
            seen.Add(free);
        }

        double Score(string id) => uses.TryGetValue(id, out var list) ? list.Sum(u => Weight(u, now)) : 0;

        // Newcomers, most used first, each taking the weakest unpinned bead if clearly ahead of it.
        var onWheel = slots.Select(s => s.ActionId).ToHashSet();
        foreach (var id in All.Select(a => a.Id).Where(id => !onWheel.Contains(id)).OrderByDescending(Score))
        {
            double score = Score(id);
            if (score <= 0) break;
            int weakest = -1;
            double weakestScore = double.MaxValue;
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i].Pinned) continue;
                double s = Score(slots[i].ActionId);
                // On a tie the later bead goes: the first beads hold the defaults people reach for most.
                if (s <= weakestScore)
                {
                    weakest = i;
                    weakestScore = s;
                }
            }
            if (weakest < 0 || score < weakestScore * SwapMargin + MinUses) continue;
            onWheel.Remove(slots[weakest].ActionId);
            slots[weakest] = new WheelSlot(id, false);
            onWheel.Add(id);
        }
        return slots;
    }
}
