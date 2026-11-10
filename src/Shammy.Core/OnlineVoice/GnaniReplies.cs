using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Shammy.Core.OnlineVoice;

/// <summary>Why a request to Gnani didn't work, in the few kinds the app tells apart.</summary>
public enum GnaniProblem
{
    /// <summary>401 MISSING_API_KEY: the key header was empty.</summary>
    MissingKey,
    /// <summary>401 INVALID_API_KEY: the key isn't valid.</summary>
    BadKey,
    /// <summary>402: no credit left.</summary>
    NoCredits,
    /// <summary>403: the key may not use this service (SCOPE_NOT_ALLOWED), or the account is refused.</summary>
    NotAllowed,
    /// <summary>429 RATE_LIMITED: too many requests at once.</summary>
    Busy,
    /// <summary>400 or 422: the service didn't like the request.</summary>
    BadRequest,
    /// <summary>400 MAX_AUDIO_DURATION_EXCEEDED: the recording was longer than 30 s.</summary>
    AudioTooLong,
    /// <summary>500 or 503: the service failed.</summary>
    Server,
    /// <summary>504 UPSTREAM_TIMEOUT, or no answer in the time the app allows.</summary>
    Timeout,
    /// <summary>The PC has no way out to the internet.</summary>
    Offline,
    /// <summary>The PC is online but the service couldn't be reached.</summary>
    Unreachable,
    /// <summary>An answer that isn't what the documentation describes.</summary>
    Unexpected,
}

/// <param name="Code">The service's own code ("INVALID_API_KEY", "RATE_LIMITED"…), or "" when it gave none.</param>
/// <param name="Message">The service's own words, shortened, for the log. Never shown as they are.</param>
public sealed record GnaniError(GnaniProblem Problem, int Status, string Code, string Message)
{
    /// <summary>For the log and the pipe: "BadKey 401 INVALID_API_KEY".</summary>
    public string Short => Code.Length > 0 ? $"{Problem} {Status} {Code}" : $"{Problem} {Status}";
}

/// <summary>Speech, or why there is none.</summary>
public sealed record TtsReply(byte[]? Audio, WavInfo? Info, GnaniError? Error)
{
    public bool Ok => Audio is not null && Info is not null;
}

/// <summary>What was heard, or why nothing came back.</summary>
/// <param name="Truncated">The service cut the transcript short (very long speech).</param>
public sealed record SttReply(string? Transcript, bool Truncated, GnaniError? Error)
{
    public bool Ok => Transcript is not null;
}

/// <summary>
/// Reads Gnani's answers. The documentation shows four error shapes and they are all accepted here, whatever
/// the endpoint: {"detail":{"error_code","message"}} for the key and rate limits, {"success":false,"error":
/// {"type","message"}} for speech-to-text, a flat {"success":false,"message","status_code"} for text-to-speech,
/// {"detail":[…]} for a missing field, and {"error","code","message"} for the 504. Fields nobody documented are
/// ignored, and a body that isn't JSON at all is judged by its status code alone.
/// </summary>
public static class GnaniReplies
{
    const int MessageLimit = 200;

    static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);
    static readonly Regex NoCredit = new(@"insufficient|out of credit|no credit|credits? (?:exhausted|expired)|balance", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static readonly GnaniError Offline = new(GnaniProblem.Offline, 0, "", "No network");
    public static readonly GnaniError Unreachable = new(GnaniProblem.Unreachable, 0, "", "The request didn't reach Gnani");
    public static readonly GnaniError TimedOut = new(GnaniProblem.Timeout, 0, "", "No answer in time");

    // ---- Errors ------------------------------------------------------------------------------------

    public static GnaniError Error(int status, string? body)
    {
        var (code, message) = CodeAndMessage(body);
        return new GnaniError(Classify(status, code, message), status, code, message);
    }

    static GnaniProblem Classify(int status, string code, string message)
    {
        switch (code.ToUpperInvariant())
        {
            case "MISSING_API_KEY": return GnaniProblem.MissingKey;
            case "INVALID_API_KEY": return GnaniProblem.BadKey;
            case "SCOPE_NOT_ALLOWED": return GnaniProblem.NotAllowed;
            case "RATE_LIMITED": return GnaniProblem.Busy;
            case "UPSTREAM_TIMEOUT": return GnaniProblem.Timeout;
            case "MAX_AUDIO_DURATION_EXCEEDED": return GnaniProblem.AudioTooLong;
        }
        return status switch
        {
            401 => GnaniProblem.BadKey,
            402 => GnaniProblem.NoCredits,
            // The documented 403 lists "available credits" among its causes; only call it that when it says so.
            403 => NoCredit.IsMatch(message) ? GnaniProblem.NoCredits : GnaniProblem.NotAllowed,
            429 => GnaniProblem.Busy,
            408 or 504 => GnaniProblem.Timeout,
            >= 400 and < 500 => GnaniProblem.BadRequest,
            >= 500 => GnaniProblem.Server,
            _ => GnaniProblem.Unexpected,
        };
    }

    static (string Code, string Message) CodeAndMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return ("", "");
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ("", Short(body));

            if (root.TryGetProperty("detail", out var detail))
            {
                if (detail.ValueKind == JsonValueKind.Object)
                    return (Text(detail, "error_code", "code", "type"), Short(Text(detail, "message", "msg")));
                if (detail.ValueKind == JsonValueKind.Array) return ("", Short(Validation(detail)));
                if (detail.ValueKind == JsonValueKind.String) return ("", Short(detail.GetString() ?? ""));
            }
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object)
                    return (Text(error, "type", "code", "error_code"), Short(Text(error, "message", "msg")));
                if (error.ValueKind == JsonValueKind.String)
                {
                    // {"error":"Gateway Timeout","code":"UPSTREAM_TIMEOUT",…} and {"error":"JOB_NOT_FOUND",…}.
                    var code = Text(root, "code", "error_code");
                    return (code.Length > 0 ? code : error.GetString() ?? "", Short(Text(root, "message", "msg")));
                }
            }
            return (Text(root, "error_code", "code", "type"), Short(Text(root, "message", "msg")));
        }
        catch (JsonException)
        {
            return ("", Short(body));
        }
    }

    /// <summary>{"detail":[{"loc":["body","language_code"],"msg":"Field required"}]} as "language_code: Field required".</summary>
    static string Validation(JsonElement items)
    {
        var parts = new List<string>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            string field = "";
            if (item.TryGetProperty("loc", out var loc) && loc.ValueKind == JsonValueKind.Array)
                foreach (var part in loc.EnumerateArray())
                    if (part.ValueKind == JsonValueKind.String) field = part.GetString() ?? "";
            var msg = Text(item, "msg", "message");
            parts.Add(field.Length > 0 ? $"{field}: {msg}" : msg);
        }
        return string.Join("; ", parts);
    }

    /// <summary>The service asked for a language although the documentation calls it optional here.</summary>
    public static bool WantsLanguage(GnaniError e) =>
        e.Status is 400 or 422 && e.Message.Contains("language_code", StringComparison.OrdinalIgnoreCase);

    /// <summary>The error in the user's words (the Test button shows it).</summary>
    public static string Friendly(GnaniError e) => e.Problem switch
    {
        GnaniProblem.MissingKey or GnaniProblem.BadKey => "Gnani didn’t accept the key",
        GnaniProblem.NoCredits => "Out of credits",
        GnaniProblem.NotAllowed => e.Code.Equals("SCOPE_NOT_ALLOWED", StringComparison.OrdinalIgnoreCase)
            ? "This key isn’t allowed to do that" : "Gnani refused this key",
        GnaniProblem.Busy => "Gnani is busy, try again in a moment",
        GnaniProblem.Offline => "No internet",
        GnaniProblem.Unreachable => "Couldn’t reach Gnani",
        GnaniProblem.Timeout => "Gnani took too long",
        GnaniProblem.AudioTooLong => "That was too long for Gnani",
        GnaniProblem.Server => "Gnani has a problem right now",
        GnaniProblem.BadRequest => "Gnani didn’t accept the request",
        _ => "Gnani sent something unexpected",
    };

    // ---- Text to speech ----------------------------------------------------------------------------

    /// <summary>
    /// A 2xx answer is the WAV file itself. Should the service ever wrap it in JSON instead, the audio is taken
    /// from a base64 field with a likely name; anything else that isn't playable PCM is an error, never cached.
    /// </summary>
    public static TtsReply Tts(int status, byte[]? body)
    {
        body ??= Array.Empty<byte>();
        if (status is < 200 or >= 300) return new TtsReply(null, null, Error(status, Utf8(body)));
        if (Wav.TryRead(body, out var info)) return new TtsReply(body, info, null);
        if (LooksLikeJson(body))
        {
            var text = Utf8(body);
            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (Failed(root)) return new TtsReply(null, null, Error(status, text));
                    if (Base64Audio(root) is { } audio && Wav.TryRead(audio, out var wrapped)) return new TtsReply(audio, wrapped, null);
                }
            }
            catch (JsonException)
            {
            }
            return new TtsReply(null, null, new GnaniError(GnaniProblem.Unexpected, status, "", "JSON without audio"));
        }
        return new TtsReply(null, null, new GnaniError(GnaniProblem.Unexpected, status, "",
            body.Length == 0 ? "An empty answer" : "Audio that isn't a PCM WAV file"));
    }

    static readonly string[] AudioFields = { "audio", "audio_content", "audioContent", "audio_base64", "audio_data", "data" };

    static byte[]? Base64Audio(JsonElement obj, int depth = 0)
    {
        foreach (var name in AudioFields)
        {
            if (!obj.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 64 } text)
            {
                // "data:audio/wav;base64,…" or the bare base64.
                int comma = text.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? text.IndexOf(',') : -1;
                try { return Convert.FromBase64String(comma >= 0 ? text[(comma + 1)..] : text); }
                catch (FormatException) { }
            }
            else if (value.ValueKind == JsonValueKind.Object && depth < 2 && Base64Audio(value, depth + 1) is { } inner) return inner;
        }
        foreach (var name in new[] { "result", "output", "response" })
            if (depth < 2 && obj.TryGetProperty(name, out var nested) && nested.ValueKind == JsonValueKind.Object && Base64Audio(nested, depth + 1) is { } found)
                return found;
        return null;
    }

    // ---- Speech to text ----------------------------------------------------------------------------

    /// <summary>
    /// A 2xx answer is {"success":true,"transcript":"…"}; an empty transcript means no speech and is not an
    /// error. "text" and an object holding the text are accepted too, in case an older shape comes back.
    /// </summary>
    public static SttReply Stt(int status, string? body)
    {
        if (status is < 200 or >= 300) return new SttReply(null, false, Error(status, body));
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "null" : body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Odd(status, "JSON that isn't an object");
            if (Failed(root)) return new SttReply(null, false, Error(status, body));
            bool truncated = root.TryGetProperty("truncated", out var t) && t.ValueKind == JsonValueKind.True;
            foreach (var name in new[] { "transcript", "text" })
            {
                if (!root.TryGetProperty(name, out var value)) continue;
                if (value.ValueKind == JsonValueKind.String) return new SttReply(Tidy(value.GetString()), truncated, null);
                if (value.ValueKind == JsonValueKind.Null) return new SttReply("", truncated, null);
                if (value.ValueKind == JsonValueKind.Object && Text(value, "text", "transcript", "literal", "verbatim") is { Length: > 0 } inner)
                    return new SttReply(Tidy(inner), truncated, null);
            }
            if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Object &&
                output.TryGetProperty("literal", out var literal) && literal.ValueKind == JsonValueKind.String)
                return new SttReply(Tidy(literal.GetString()), truncated, null);
            return Odd(status, "No transcript in the answer");
        }
        catch (JsonException)
        {
            return Odd(status, "An answer that isn't JSON");
        }
    }

    static SttReply Odd(int status, string why) => new(null, false, new GnaniError(GnaniProblem.Unexpected, status, "", why));

    // ---- Helpers -----------------------------------------------------------------------------------

    /// <summary>"success": false on a 2xx answer.</summary>
    static bool Failed(JsonElement root) => root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.False;

    static string Text(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
            if (obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text)
                return text;
        return "";
    }

    static string Tidy(string? text) => Spaces.Replace(text ?? "", " ").Trim();

    static string Short(string text)
    {
        text = Tidy(text);
        return text.Length <= MessageLimit ? text : text[..MessageLimit] + "…";
    }

    static bool LooksLikeJson(byte[] body)
    {
        foreach (byte b in body)
        {
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or 0xEF or 0xBB or 0xBF) continue;
            return b is (byte)'{' or (byte)'[';
        }
        return false;
    }

    static string Utf8(byte[] body)
    {
        // An error body is small; never turn megabytes of audio into text.
        int n = Math.Min(body.Length, 8192);
        return Encoding.UTF8.GetString(body, 0, n);
    }
}
