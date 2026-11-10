using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Shammy.Core.OnlineVoice;

/// <summary>What to say and how, for one text-to-speech request.</summary>
/// <param name="Language">"hi-en" (Hinglish), "hi-IN", "en-IN" or "auto".</param>
public sealed record TtsOptions(string Text, string Voice, string Language, string Model = GnaniApi.TtsModel,
    int SampleRate = GnaniApi.TtsSampleRate, double Speed = 1.0);

/// <summary>How a recording should be written down, for one speech-to-text request.</summary>
/// <param name="Format">"transliterate" writes everything in Latin letters ("to theek hai").</param>
/// <param name="LanguageCode">Optional with "transliterate" (the service then picks the language itself).</param>
public sealed record SttOptions(string Model = GnaniApi.SttModel, string Format = GnaniApi.SttFormat, string? LanguageCode = null,
    string FileName = "speech.wav");

/// <summary>The uploaded recording's part of a multipart request.</summary>
public sealed record GnaniFile(string FieldName, string FileName, string ContentType, int Bytes);

/// <summary>
/// One request to Gnani, as plain data (the app turns it into an HTTP call). It carries the user's key, so it is
/// never logged or printed as it is: <see cref="Describe"/> (and ToString) show "&lt;key&gt;" in its place.
/// </summary>
public sealed class GnaniRequest
{
    public required string Url { get; init; }

    public string Method => "POST";

    public required IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; }

    /// <summary>The JSON body (text-to-speech); null for a multipart request.</summary>
    public string? JsonBody { get; init; }

    /// <summary>The multipart text fields, in order (speech-to-text); empty for a JSON request.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> FormFields { get; init; } = Array.Empty<KeyValuePair<string, string>>();

    /// <summary>The multipart file part (speech-to-text); null for a JSON request.</summary>
    public GnaniFile? File { get; init; }

    /// <summary>What would be sent, for the test pipe and the log: every header name, the key replaced by "&lt;key&gt;".</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        sb.Append(Method).Append(' ').Append(Url);
        sb.Append(" headers=[");
        sb.Append(string.Join(", ", Headers.Select(h =>
            $"{h.Key}: {(string.Equals(h.Key, GnaniApi.KeyHeader, StringComparison.OrdinalIgnoreCase) ? GnaniApi.KeyPlaceholder : h.Value)}")));
        sb.Append(']');
        if (JsonBody is not null) sb.Append(" body=").Append(JsonBody);
        if (File is not null || FormFields.Count > 0)
        {
            sb.Append(" form=[");
            var parts = FormFields.Select(f => $"{f.Key}={f.Value}").ToList();
            if (File is not null) parts.Insert(0, $"{File.FieldName}=@{File.FileName} ({File.ContentType}, {File.Bytes} bytes)");
            sb.Append(string.Join(", ", parts)).Append(']');
        }
        return sb.ToString();
    }

    public override string ToString() => Describe();
}

/// <summary>
/// Gnani's "Vachana" speech API, as documented at docs.gnani.ai (read on 10 Nov 2026):
/// text-to-speech is POST /api/v1/tts/inference with a JSON body and answers with the audio file itself;
/// speech-to-text is POST /stt/v3 as multipart/form-data and answers with JSON holding "transcript".
/// Both take the key in the X-API-Key-ID header and need nothing else.
/// </summary>
public static class GnaniApi
{
    public const string BaseUrl = "https://api.vachana.ai";
    public const string TtsUrl = BaseUrl + "/api/v1/tts/inference";
    public const string SttUrl = BaseUrl + "/stt/v3";

    public const string KeyHeader = "X-API-Key-ID";
    public const string KeyPlaceholder = "<key>";

    /// <summary>The model with the Hinglish voice (Poorvi) and the "hi-en" language.</summary>
    public const string TtsModel = "timbre-v2.5";

    /// <summary>The multilingual model: the only one that writes Hindi in Latin letters.</summary>
    public const string SttModel = "prisma-v3.0";

    public const string SttFormat = "transliterate";

    /// <summary>What the service treats a missing language as; sent only if it ever insists on one.</summary>
    public const string SttAutoLanguage = "indic-auto";

    /// <summary>24 kHz mono 16-bit: clear speech at half the size of the service's 48 kHz default.</summary>
    public const int TtsSampleRate = 24000;

    /// <summary>The documented sample rates; anything else is answered with a 400.</summary>
    public static readonly int[] TtsSampleRates = { 8000, 16000, 22050, 24000, 44100, 48000 };

    /// <summary>The REST limit is 30 s a clip; stay a little under it so a full-length hold is never refused.</summary>
    public const double SttMaxSeconds = 29;

    public static readonly TimeSpan TtsTimeout = TimeSpan.FromSeconds(6);
    public static readonly TimeSpan SttTimeout = TimeSpan.FromSeconds(4);

    static readonly JsonWriterOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static GnaniRequest Tts(TtsOptions o, string apiKey)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, Json))
        {
            w.WriteStartObject();
            w.WriteString("text", o.Text);
            w.WriteString("voice", o.Voice);
            w.WriteString("model", o.Model);
            w.WriteString("language", o.Language);
            w.WriteNumber("speed", Math.Round(Math.Clamp(o.Speed, 0.85, 1.15), 2));
            w.WriteStartObject("audio_config");
            w.WriteNumber("sample_rate", TtsSampleRates.Contains(o.SampleRate) ? o.SampleRate : TtsSampleRate);
            w.WriteNumber("num_channels", 1);
            w.WriteNumber("sample_width", 2);
            w.WriteString("encoding", "linear_pcm");
            w.WriteString("container", "wav");
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return new GnaniRequest
        {
            Url = TtsUrl,
            Headers = new[] { Pair("Content-Type", "application/json"), Pair(KeyHeader, apiKey) },
            JsonBody = Encoding.UTF8.GetString(stream.ToArray()),
        };
    }

    /// <param name="wavBytes">The size of the recording being uploaded (a 16 kHz mono WAV).</param>
    public static GnaniRequest Stt(SttOptions o, string apiKey, int wavBytes)
    {
        var fields = new List<KeyValuePair<string, string>> { Pair("model", o.Model), Pair("format", o.Format) };
        if (!string.IsNullOrWhiteSpace(o.LanguageCode)) fields.Add(Pair("language_code", o.LanguageCode.Trim()));
        return new GnaniRequest
        {
            Url = SttUrl,
            // The multipart content type (with its boundary) is set by whoever sends it.
            Headers = new[] { Pair(KeyHeader, apiKey) },
            FormFields = fields,
            // The service tells the audio format from the file name's extension.
            File = new GnaniFile("audio_file", o.FileName, "audio/wav", wavBytes),
        };
    }

    static KeyValuePair<string, string> Pair(string name, string value) => new(name, value);
}
