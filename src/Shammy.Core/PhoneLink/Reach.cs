using System.Text.Json.Nodes;

namespace Shammy.Core.PhoneLink;

/// <summary>
/// Reach: the phone controls this laptop (keys, typing, pointer, scroll). The pure part: which messages are accepted, which
/// keys exist (a fixed list, nothing else reaches Windows), how fast each kind may arrive, and which mouse buttons are
/// still held so they're let go when Reach ends. Messages (LINK.md §5): <c>reach</c> {on}, <c>key</c> {k, mods},
/// <c>type</c> {text}, <c>mv</c> {dx, dy}, <c>btn</c> {b, act}, <c>wh</c> {dy, dx}.
/// </summary>
public static class Reach
{
    public const int MaxText = 4096, MaxMove = 2000, MaxWheel = 2400;

    /// <summary>Modifier names the phone may send with a key, and their virtual-key codes.</summary>
    public static readonly IReadOnlyDictionary<string, ushort> Modifiers = new Dictionary<string, ushort>(StringComparer.Ordinal)
    {
        ["Ctrl"] = 0x11, ["Alt"] = 0x12, ["Shift"] = 0x10, ["Win"] = 0x5B,
    };

    /// <summary>Every key the phone may press, by name. A name not here is refused.</summary>
    public static readonly IReadOnlyDictionary<string, ushort> Keys = BuildKeys();

    /// <summary>Keys Windows treats as "extended" (they need the flag, or arrows arrive as the number pad's).</summary>
    public static readonly IReadOnlySet<ushort> Extended = new HashSet<ushort>
    {
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0x5B, // PageUp..Down, End, Home, arrows, Insert, Delete, Win
        0xAD, 0xAE, 0xAF, 0xB0, 0xB1, 0xB3,                               // volume and media keys
    };

    static Dictionary<string, ushort> BuildKeys()
    {
        var k = new Dictionary<string, ushort>(StringComparer.Ordinal)
        {
            ["Esc"] = 0x1B, ["Tab"] = 0x09, ["Enter"] = 0x0D, ["Backspace"] = 0x08, ["Delete"] = 0x2E, ["Insert"] = 0x2D, ["Space"] = 0x20,
            ["Up"] = 0x26, ["Down"] = 0x28, ["Left"] = 0x25, ["Right"] = 0x27, ["Home"] = 0x24, ["End"] = 0x23, ["PageUp"] = 0x21, ["PageDown"] = 0x22,
            ["`"] = 0xC0, ["-"] = 0xBD, ["="] = 0xBB, ["["] = 0xDB, ["]"] = 0xDD, ["\\"] = 0xDC, [";"] = 0xBA, ["'"] = 0xDE,
            [","] = 0xBC, ["."] = 0xBE, ["/"] = 0xBF,
            ["VolumeUp"] = 0xAF, ["VolumeDown"] = 0xAE, ["VolumeMute"] = 0xAD, ["PlayPause"] = 0xB3, ["NextTrack"] = 0xB0, ["PrevTrack"] = 0xB1,
        };
        for (int i = 1; i <= 12; i++) k[$"F{i}"] = (ushort)(0x6F + i);
        for (char c = 'A'; c <= 'Z'; c++) k[c.ToString()] = c;
        for (char c = '0'; c <= '9'; c++) k[c.ToString()] = c;
        return k;
    }

    /// <summary>The input a message asks for, or null when it isn't one, names an unknown key, or is malformed.</summary>
    public static ReachInput? Parse(JsonObject m)
    {
        switch (Str(m, "t"))
        {
            case "key":
                if (!Keys.TryGetValue(Str(m, "k"), out var vk)) return null;
                var mods = new List<ushort>();
                if (m["mods"] is { } node && node is not JsonArray) return null;
                if (m["mods"] is JsonArray arr)
                    foreach (var x in arr)
                    {
                        if (x is not JsonValue v || !v.TryGetValue<string>(out var name) || !Modifiers.TryGetValue(name, out var mvk)) return null;
                        if (!mods.Contains(mvk)) mods.Add(mvk);
                    }
                return new ReachKey(vk, mods);
            case "type":
                var text = Clean(Str(m, "text"));
                return text.Length == 0 ? null : new ReachText(text);
            case "mv":
                int dx = Clamp(Num(m, "dx"), MaxMove), dy = Clamp(Num(m, "dy"), MaxMove);
                return dx == 0 && dy == 0 ? null : new ReachMove(dx, dy);
            case "wh":
                // lines (fractions allowed, for smooth scrolling) → Windows wheel units, 120 to a notch
                int wv = Clamp(Num(m, "dy") * 120, MaxWheel), wh = Clamp(Num(m, "dx") * 120, MaxWheel);
                return wv == 0 && wh == 0 ? null : new ReachWheel(wv, wh);
            case "at":
                // a spot on the screen picture the phone shows, as fractions of it (0..1): the pointer goes there
                double ax = Num(m, "x"), ay = Num(m, "y");
                return ax is < 0 or > 1 || ay is < 0 or > 1 ? null : new ReachAt(ax, ay);
            case "btn":
                ReachMouse? b = Str(m, "b") switch { "left" => ReachMouse.Left, "right" => ReachMouse.Right, "middle" => ReachMouse.Middle, _ => null };
                ReachPress? p = Str(m, "act") switch { "click" => ReachPress.Click, "double" => ReachPress.Double, "down" => ReachPress.Down, "up" => ReachPress.Up, _ => null };
                return b is null || p is null ? null : new ReachButton(b.Value, p.Value);
            default:
                return null;
        }
    }

    /// <summary>Typed text: Windows line ends become \n, other control characters go (they'd be keystrokes in disguise), capped.</summary>
    public static string Clean(string text)
    {
        var t = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var sb = new System.Text.StringBuilder(Math.Min(t.Length, MaxText));
        foreach (var c in t)
        {
            if (sb.Length >= MaxText) break;
            if (c is '\n' or '\t' || !char.IsControl(c)) sb.Append(c);
        }
        // never end on half an emoji
        if (sb.Length > 0 && char.IsHighSurrogate(sb[^1])) sb.Length--;
        return sb.ToString();
    }

    /// <summary>
    /// Where a pointer position lands on Windows' 0..65535 absolute scale across all screens ([origin] is the leftmost
    /// or topmost pixel of the whole desktop, which is negative when a screen sits left of or above the main one).
    /// </summary>
    public static int Absolute(int pos, int origin, int size)
    {
        size = Math.Max(1, size);
        int p = Math.Clamp(pos, origin, origin + size - 1) - origin;
        return (int)Math.Round(p * 65535.0 / Math.Max(1, size - 1));
    }

    static string Str(JsonObject m, string key) => m[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    static double Num(JsonObject m, string key)
    {
        if (m[key] is not JsonValue v) return 0;
        if (v.TryGetValue<double>(out var d)) return double.IsFinite(d) ? d : 0;
        if (v.TryGetValue<int>(out var i)) return i;
        return 0;
    }

    static int Clamp(double v, int max) => (int)Math.Round(Math.Clamp(v, -max, max));
}

public abstract record ReachInput;
public sealed record ReachKey(ushort Vk, IReadOnlyList<ushort> Mods) : ReachInput;
public sealed record ReachText(string Text) : ReachInput;
public sealed record ReachMove(int Dx, int Dy) : ReachInput;
public sealed record ReachWheel(int Vertical, int Horizontal) : ReachInput;
public sealed record ReachButton(ReachMouse Button, ReachPress Press) : ReachInput;
/// <summary>The pointer to a spot on the screen the phone is watching, as fractions of that screen.</summary>
public sealed record ReachAt(double X, double Y) : ReachInput;
public enum ReachMouse { Left, Right, Middle }
public enum ReachPress { Click, Double, Down, Up }

/// <summary>
/// The door between the phone and Windows, one per laptop: whether Reach is on and for which connection, how fast each
/// kind of input may arrive (a token bucket per kind; typed text costs by length), and which mouse buttons are held.
/// Everything happens under one lock, including handing input to the injector, so Stop and a late message can't
/// interleave into a button left down.
/// </summary>
public sealed class ReachGate
{
    static readonly Dictionary<Type, (double PerSecond, double Burst)> Rates = new()
    {
        [typeof(ReachMove)] = (300, 60), [typeof(ReachWheel)] = (120, 30), [typeof(ReachKey)] = (60, 20),
        [typeof(ReachText)] = (30, 10), [typeof(ReachButton)] = (20, 8), [typeof(ReachAt)] = (20, 8),
    };

    readonly object _lock = new();
    readonly Dictionary<Type, (double Tokens, DateTime At)> _buckets = new();
    readonly HashSet<ReachMouse> _down = new();
    object? _owner;

    public bool On { get { lock (_lock) return _owner is not null; } }

    /// <summary>Turns Reach on for the connection [owner]; false if it was already on (for it or another).</summary>
    public bool Start(object owner)
    {
        lock (_lock)
        {
            if (_owner is not null) { _owner = owner; return false; }
            _owner = owner;
            return true;
        }
    }

    /// <summary>
    /// Ends Reach if it's on (and, given [owner], only if that connection started it). [letGo] runs under the lock with the
    /// buttons still held, so the caller can drop queued input and release them before anything else gets in.
    /// </summary>
    public bool End(object? owner, Action<IReadOnlyList<ReachMouse>> letGo)
    {
        lock (_lock)
        {
            if (_owner is null || (owner is not null && !ReferenceEquals(owner, _owner))) return false;
            _owner = null;
            var held = _down.ToList();
            _down.Clear();
            letGo(held);
            return true;
        }
    }

    /// <summary>A message from the phone: passed to [send] (under the lock) only while on, known, within its rate, and
    /// (for a button let go) only for a button that is held. Returns whether it went.</summary>
    public bool Admit(JsonObject m, DateTime utcNow, Action<ReachInput> send)
    {
        lock (_lock)
        {
            if (_owner is null || Reach.Parse(m) is not { } input) return false;
            double cost = input is ReachText t ? t.Text.Length / 64 + 1 : 1;
            if (!Take(input.GetType(), cost, utcNow)) return false;
            if (input is ReachButton b)
            {
                if (b.Press == ReachPress.Down) _down.Add(b.Button);
                else if (b.Press == ReachPress.Up && !_down.Remove(b.Button)) return false;
            }
            send(input);
            return true;
        }
    }

    bool Take(Type kind, double cost, DateTime utcNow)
    {
        var rate = Rates[kind];
        var (tokens, at) = _buckets.TryGetValue(kind, out var b) ? b : (rate.Burst, utcNow);
        tokens = Math.Min(rate.Burst, tokens + Math.Max(0, (utcNow - at).TotalSeconds) * rate.PerSecond);
        bool ok = tokens >= cost;
        _buckets[kind] = (ok ? tokens - cost : tokens, utcNow);
        return ok;
    }
}
