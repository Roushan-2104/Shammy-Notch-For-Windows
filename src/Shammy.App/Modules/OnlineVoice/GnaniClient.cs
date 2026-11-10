using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Text;
using Shammy.Core.OnlineVoice;

namespace Shammy.Modules.OnlineVoice;

/// <summary>
/// Sends a <see cref="GnaniRequest"/> and reads the answer. The key travels only in this one request's header
/// (never as a default header on the shared client, which other modules use for other sites), and nothing here
/// logs a request, a key or what was said. Network trouble comes back as an error, not an exception; only a
/// cancellation (the caller's, or its time limit) is thrown.
/// </summary>
internal sealed class GnaniClient(HttpClient http)
{
    public async Task<TtsReply> SpeakAsync(GnaniRequest request, CancellationToken ct)
    {
        try
        {
            using var message = Build(request, null);
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseContentRead, ct);
            var body = await response.Content.ReadAsByteArrayAsync(ct);
            return GnaniReplies.Tts((int)response.StatusCode, body);
        }
        catch (HttpRequestException ex)
        {
            return new TtsReply(null, null, NotReached(ex));
        }
    }

    public async Task<SttReply> TranscribeAsync(GnaniRequest request, byte[] wav, CancellationToken ct)
    {
        try
        {
            using var message = Build(request, wav);
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseContentRead, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            return GnaniReplies.Stt((int)response.StatusCode, body);
        }
        catch (HttpRequestException ex)
        {
            return new SttReply(null, false, NotReached(ex));
        }
    }

    /// <summary>Whether Windows has any network at all (a cheap check; it can't tell if the internet is beyond it).</summary>
    public static bool NetworkUp()
    {
        try { return NetworkInterface.GetIsNetworkAvailable(); }
        catch (Exception) { return true; }
    }

    static GnaniError NotReached(HttpRequestException ex) =>
        !NetworkUp() || ex.HttpRequestError == HttpRequestError.NameResolutionError ? GnaniReplies.Offline : GnaniReplies.Unreachable;

    static HttpRequestMessage Build(GnaniRequest request, byte[]? file)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, request.Url);
        if (request.JsonBody is not null)
        {
            message.Content = new StringContent(request.JsonBody, Encoding.UTF8, "application/json");
        }
        else if (request.File is { } part && file is not null)
        {
            // Written the way curl writes it (quoted names, a bare boundary, no types on the text fields), since
            // that is what the documentation's examples send.
            var form = new MultipartFormDataContent();
            var audio = new ByteArrayContent(file);
            audio.Headers.ContentType = new MediaTypeHeaderValue(part.ContentType);
            audio.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
            {
                Name = Quoted(part.FieldName),
                FileName = Quoted(part.FileName),
            };
            form.Add(audio);
            foreach (var field in request.FormFields)
            {
                var text = new StringContent(field.Value, Encoding.UTF8);
                text.Headers.ContentType = null;
                text.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = Quoted(field.Key) };
                form.Add(text);
            }
            if (form.Headers.ContentType?.Parameters.FirstOrDefault(p => p.Name == "boundary") is { Value: { } boundary } parameter)
                parameter.Value = boundary.Trim('"');
            message.Content = form;
        }
        foreach (var header in request.Headers)
        {
            if (header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
            message.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return message;
    }

    static string Quoted(string name) => "\"" + name.Replace("\"", "") + "\"";
}
