using System.Globalization;

namespace Shammy.Core.Stage;

/// <summary>Stage's small rules: the text sizes, the ring light's strength, the caption line and the test pipe's words.</summary>
public static class StageText
{
    public const double DefaultSize = 22, SmallestSize = 16, LargestSize = 32, SizeStep = 2;

    /// <summary>The ring light's strength (0.2–1); 0.6 to start with.</summary>
    public const double DefaultRing = 0.6, FaintestRing = 0.2, RingStep = 0.2;

    /// <summary>The text size one step bigger (+1) or smaller (−1), within 16–32 px.</summary>
    public static double StepSize(double size, int direction) =>
        Math.Clamp(Math.Round(ClampSize(size) / SizeStep) * SizeStep + Math.Sign(direction) * SizeStep, SmallestSize, LargestSize);

    public static double ClampSize(double size) => double.IsFinite(size) && size > 0 ? Math.Clamp(size, SmallestSize, LargestSize) : DefaultSize;

    public static double StepRing(double strength, int direction) =>
        Math.Round(Math.Clamp(ClampRing(strength) + Math.Sign(direction) * RingStep, FaintestRing, 1), 2);

    public static double ClampRing(double strength) => double.IsFinite(strength) && strength > 0 ? Math.Clamp(strength, FaintestRing, 1) : DefaultRing;

    /// <summary>
    /// The newest words of a caption, for one short line: the last line of it, and when that is longer than
    /// <paramref name="maxChars"/>, its end from a word boundary with an ellipsis in front (the newest words matter).
    /// </summary>
    public static string CaptionTail(string? caption, int maxChars = 64)
    {
        var text = (caption ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0) return "";
        var last = string.Join(' ', lines[^1].Split(' ', StringSplitOptions.RemoveEmptyEntries));
        maxChars = Math.Max(8, maxChars);
        if (last.Length <= maxChars) return last;
        var tail = last[^(maxChars - 1)..];
        int space = tail.IndexOf(' ');
        if (space >= 0 && space < tail.Length - 1) tail = tail[(space + 1)..];
        return "…" + tail;
    }

    /// <summary>"stage-voice 0.2" (an RMS level 0–1), "stage-voice off" (back to the microphone) → null for off; false when unreadable.</summary>
    public static bool TryVoiceLevel(string? arg, out double? level)
    {
        level = null;
        var a = (arg ?? "").Trim().ToLowerInvariant();
        if (a is "off" or "mic" or "") return a.Length > 0;
        if (!double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v) || v < 0) return false;
        level = Math.Min(1, v);
        return true;
    }

    /// <summary>"on", "off", "toggle" or a strength 0–100 for the ring light.</summary>
    public static bool TryRing(string? arg, bool on, double strength, out bool newOn, out double newStrength)
    {
        newOn = on;
        newStrength = strength;
        var a = (arg ?? "").Trim().ToLowerInvariant().TrimEnd('%');
        switch (a)
        {
            case "on": newOn = true; return true;
            case "off": newOn = false; return true;
            case "toggle": newOn = !on; return true;
            case "brighter" or "up": newOn = true; newStrength = StepRing(strength, 1); return true;
            case "dimmer" or "down": newStrength = StepRing(strength, -1); return true;
        }
        if (!double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v)) return false;
        if (v <= 0)
        {
            newOn = false;
            return true;
        }
        newOn = true;
        newStrength = ClampRing(v / 100);
        return true;
    }
}
