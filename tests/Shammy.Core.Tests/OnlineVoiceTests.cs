using System.Text;
using System.Text.Json;
using Shammy.Core.OnlineVoice;

namespace Shammy.Core.Tests;

public class GnaniRequestTests
{
    const string Key = "gn_live_0123456789abcdef";

    [Fact]
    public void Speech_is_asked_for_at_the_documented_address_with_the_key_in_its_header()
    {
        var r = GnaniApi.Tts(new TtsOptions("Tea's ready!", "Poorvi", "hi-en"), Key);
        Assert.Equal("https://api.vachana.ai/api/v1/tts/inference", r.Url);
        Assert.Equal("POST", r.Method);
        Assert.Contains(r.Headers, h => h.Key == "X-API-Key-ID" && h.Value == Key);
        Assert.Contains(r.Headers, h => h.Key == "Content-Type" && h.Value == "application/json");
        Assert.Equal(2, r.Headers.Count);
        Assert.Null(r.File);
        Assert.Empty(r.FormFields);
    }

    [Fact]
    public void The_speech_body_names_the_text_voice_model_language_and_a_wav_of_16_bit_mono()
    {
        var r = GnaniApi.Tts(new TtsOptions("Tea's ready!", "Poorvi", "hi-en"), Key);
        using var doc = JsonDocument.Parse(r.JsonBody!);
        var body = doc.RootElement;
        Assert.Equal("Tea's ready!", body.GetProperty("text").GetString());
        Assert.Equal("Poorvi", body.GetProperty("voice").GetString());
        Assert.Equal("timbre-v2.5", body.GetProperty("model").GetString());
        Assert.Equal("hi-en", body.GetProperty("language").GetString());
        Assert.Equal(1.0, body.GetProperty("speed").GetDouble());
        var audio = body.GetProperty("audio_config");
        Assert.Equal(24000, audio.GetProperty("sample_rate").GetInt32());
        Assert.Equal(1, audio.GetProperty("num_channels").GetInt32());
        Assert.Equal(2, audio.GetProperty("sample_width").GetInt32());
        Assert.Equal("linear_pcm", audio.GetProperty("encoding").GetString());
        Assert.Equal("wav", audio.GetProperty("container").GetString());
    }

    [Fact]
    public void Hindi_text_and_quotes_survive_the_trip_into_json()
    {
        var r = GnaniApi.Tts(new TtsOptions("चाय \"ready\" है", "Nalini", "hi-IN"), Key);
        using var doc = JsonDocument.Parse(r.JsonBody!);
        Assert.Equal("चाय \"ready\" है", doc.RootElement.GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(12345, 24000)]
    [InlineData(0, 24000)]
    [InlineData(48000, 48000)]
    [InlineData(16000, 16000)]
    public void A_sample_rate_the_service_does_not_list_falls_back_to_24_kHz(int asked, int sent)
    {
        var r = GnaniApi.Tts(new TtsOptions("Hi", "Poorvi", "hi-en", SampleRate: asked), Key);
        using var doc = JsonDocument.Parse(r.JsonBody!);
        Assert.Equal(sent, doc.RootElement.GetProperty("audio_config").GetProperty("sample_rate").GetInt32());
    }

    [Theory]
    [InlineData(3.0, 1.15)]
    [InlineData(0.1, 0.85)]
    [InlineData(1.0, 1.0)]
    public void Speed_stays_inside_the_documented_range(double asked, double sent)
    {
        var r = GnaniApi.Tts(new TtsOptions("Hi", "Poorvi", "hi-en", Speed: asked), Key);
        using var doc = JsonDocument.Parse(r.JsonBody!);
        Assert.Equal(sent, doc.RootElement.GetProperty("speed").GetDouble());
    }

    [Fact]
    public void A_recording_is_uploaded_as_a_wav_file_part_asking_for_latin_letters()
    {
        var r = GnaniApi.Stt(new SttOptions(), Key, 64044);
        Assert.Equal("https://api.vachana.ai/stt/v3", r.Url);
        Assert.Null(r.JsonBody);
        Assert.Equal(new GnaniFile("audio_file", "speech.wav", "audio/wav", 64044), r.File);
        Assert.Equal(new[] { "model=prisma-v3.0", "format=transliterate" }, r.FormFields.Select(f => $"{f.Key}={f.Value}"));
        // Only the key: the multipart content type is the sender's to set, with its boundary.
        Assert.Equal(new[] { "X-API-Key-ID" }, r.Headers.Select(h => h.Key));
    }

    [Fact]
    public void A_language_is_sent_only_when_one_is_given()
    {
        Assert.DoesNotContain(GnaniApi.Stt(new SttOptions(LanguageCode: " "), Key, 100).FormFields, f => f.Key == "language_code");
        var r = GnaniApi.Stt(new SttOptions(LanguageCode: GnaniApi.SttAutoLanguage), Key, 100);
        Assert.Contains(r.FormFields, f => f.Key == "language_code" && f.Value == "indic-auto");
    }

    [Fact]
    public void Describing_a_request_never_prints_the_key()
    {
        foreach (var r in new[] { GnaniApi.Tts(new TtsOptions("Hi", "Poorvi", "hi-en"), Key), GnaniApi.Stt(new SttOptions(), Key, 100) })
        {
            Assert.DoesNotContain(Key, r.Describe());
            Assert.DoesNotContain(Key, r.ToString());
            Assert.Contains("X-API-Key-ID: <key>", r.Describe());
            Assert.Contains(r.Url, r.Describe());
        }
    }

    [Fact]
    public void A_described_upload_shows_its_fields_and_the_size_of_the_file()
    {
        var text = GnaniApi.Stt(new SttOptions(), Key, 64044).Describe();
        Assert.Contains("audio_file=@speech.wav (audio/wav, 64044 bytes)", text);
        Assert.Contains("model=prisma-v3.0", text);
        Assert.Contains("format=transliterate", text);
    }

    [Fact]
    public void The_waits_are_six_seconds_to_speak_and_four_to_listen()
    {
        Assert.Equal(TimeSpan.FromSeconds(6), GnaniApi.TtsTimeout);
        Assert.Equal(TimeSpan.FromSeconds(4), GnaniApi.SttTimeout);
    }
}

public class GnaniVoiceTests
{
    [Fact]
    public void The_picker_starts_on_the_hinglish_voice()
    {
        Assert.Equal("Poorvi", GnaniVoices.Default);
        Assert.Equal("hi-en", GnaniVoices.Find(null).Language);
        Assert.Equal("Poorvi · Hinglish", GnaniVoices.All[0].Label);
    }

    [Fact]
    public void Every_voice_speaks_hinglish_hindi_or_english()
    {
        Assert.All(GnaniVoices.All, v => Assert.Contains(v.Language, new[] { "hi-en", "hi-IN", "en-IN" }));
        Assert.Equal(GnaniVoices.All.Count, GnaniVoices.All.Select(v => v.Name).Distinct().Count());
    }

    [Theory]
    [InlineData("kaveri", "Kaveri")]
    [InlineData(" Deepak ", "Deepak")]
    [InlineData("Shivani", "Poorvi")] // a voice of another model
    [InlineData("", "Poorvi")]
    public void An_unknown_voice_becomes_the_default(string saved, string used) => Assert.Equal(used, GnaniVoices.Normalize(saved));

    [Fact]
    public void Each_voice_is_used_with_its_own_language()
    {
        Assert.Equal("hi-en", GnaniVoices.LanguageFor(GnaniVoices.Find("Poorvi"), "Tea's ready!"));
        Assert.Equal("hi-IN", GnaniVoices.LanguageFor(GnaniVoices.Find("Nalini"), "Tea's ready!"));
        Assert.Equal("en-IN", GnaniVoices.LanguageFor(GnaniVoices.Find("Kaveri"), "Tea's ready!"));
    }

    [Fact]
    public void Hindi_letters_given_to_an_english_voice_are_left_for_the_service_to_detect()
    {
        Assert.Equal("auto", GnaniVoices.LanguageFor(GnaniVoices.Find("Kaveri"), "Reminder: दवा लेना"));
        Assert.Equal("hi-en", GnaniVoices.LanguageFor(GnaniVoices.Find("Poorvi"), "Reminder: दवा लेना"));
    }
}

public class GnaniErrorTests
{
    [Fact]
    public void A_rejected_key_is_told_apart_from_a_missing_one()
    {
        var bad = GnaniReplies.Error(401, """{"detail":{"error_code":"INVALID_API_KEY","message":"Unauthorized","status_code":401}}""");
        Assert.Equal(GnaniProblem.BadKey, bad.Problem);
        Assert.Equal("INVALID_API_KEY", bad.Code);
        Assert.Equal("Unauthorized", bad.Message);
        var missing = GnaniReplies.Error(401, """{"detail":{"error_code":"MISSING_API_KEY","message":"Missing API key","status_code":401}}""");
        Assert.Equal(GnaniProblem.MissingKey, missing.Problem);
        Assert.Equal("Gnani didn’t accept the key", GnaniReplies.Friendly(bad));
        Assert.Equal("Gnani didn’t accept the key", GnaniReplies.Friendly(missing));
    }

    [Fact]
    public void A_402_means_out_of_credits_whatever_its_body()
    {
        Assert.Equal(GnaniProblem.NoCredits, GnaniReplies.Error(402, null).Problem);
        Assert.Equal(GnaniProblem.NoCredits, GnaniReplies.Error(402, "Payment Required").Problem);
        Assert.Equal("Out of credits", GnaniReplies.Friendly(GnaniReplies.Error(402, """{"detail":{"message":"Insufficient credit"}}""")));
    }

    [Fact]
    public void A_key_without_the_scope_is_not_allowed()
    {
        var e = GnaniReplies.Error(403, """{"detail":{"error_code":"SCOPE_NOT_ALLOWED","message":"Service 'STT' not in allowed scopes","status_code":403}}""");
        Assert.Equal(GnaniProblem.NotAllowed, e.Problem);
        Assert.Equal("This key isn’t allowed to do that", GnaniReplies.Friendly(e));
    }

    [Fact]
    public void A_plain_403_is_a_refusal_and_only_a_stated_credit_shortage_is_out_of_credits()
    {
        var listed = GnaniReplies.Error(403, """{"success":false,"error":{"type":"FORBIDDEN","message":"You do not have permission to access this resource. Check your API key, organization status, or available credits."}}""");
        Assert.Equal(GnaniProblem.NotAllowed, listed.Problem);
        Assert.Equal("FORBIDDEN", listed.Code);
        Assert.Equal("Gnani refused this key", GnaniReplies.Friendly(listed));
        var broke = GnaniReplies.Error(403, """{"success":false,"error":{"type":"FORBIDDEN","message":"Insufficient credits for this request"}}""");
        Assert.Equal(GnaniProblem.NoCredits, broke.Problem);
    }

    [Fact]
    public void Too_many_requests_at_once_is_busy()
    {
        var e = GnaniReplies.Error(429, """{"detail":{"error_code":"RATE_LIMITED","message":"Only 1 active request is allowed at a time. Wait for your current request to finish before sending another.","status_code":429}}""");
        Assert.Equal(GnaniProblem.Busy, e.Problem);
        Assert.Equal("Gnani is busy, try again in a moment", GnaniReplies.Friendly(e));
    }

    [Fact]
    public void The_speech_to_text_envelope_gives_its_type_and_message()
    {
        var e = GnaniReplies.Error(400, """{"success":false,"error":{"type":"INVALID_FORMAT","message":"Invalid format. Supported values are: verbatim, transcribe, codemix, transliterate, or omit format."}}""");
        Assert.Equal(GnaniProblem.BadRequest, e.Problem);
        Assert.Equal("INVALID_FORMAT", e.Code);
        Assert.StartsWith("Invalid format.", e.Message);
    }

    [Fact]
    public void A_recording_over_thirty_seconds_is_too_long()
    {
        var e = GnaniReplies.Error(400, """{"success":false,"error":{"type":"MAX_AUDIO_DURATION_EXCEEDED","message":"Audio duration exceeds maximum allowed duration of 30 seconds"}}""");
        Assert.Equal(GnaniProblem.AudioTooLong, e.Problem);
    }

    [Fact]
    public void The_flat_text_to_speech_error_gives_its_message()
    {
        var e = GnaniReplies.Error(400, """{"success": false, "message": "Invalid request: Field required at body -> text", "status_code": 400}""");
        Assert.Equal(GnaniProblem.BadRequest, e.Problem);
        Assert.Equal("", e.Code);
        Assert.Equal("Invalid request: Field required at body -> text", e.Message);
        var down = GnaniReplies.Error(500, """{"success": false, "message": "We are facing technical difficulties. Please try again later.", "status_code": 500}""");
        Assert.Equal(GnaniProblem.Server, down.Problem);
        Assert.Equal("Gnani has a problem right now", GnaniReplies.Friendly(down));
    }

    [Fact]
    public void A_missing_field_names_the_field()
    {
        var e = GnaniReplies.Error(422, """{"detail":[{"type":"missing","loc":["body","language_code"],"msg":"Field required","input":null}]}""");
        Assert.Equal(GnaniProblem.BadRequest, e.Problem);
        Assert.Equal("language_code: Field required", e.Message);
        Assert.True(GnaniReplies.WantsLanguage(e));
        Assert.False(GnaniReplies.WantsLanguage(GnaniReplies.Error(422, """{"detail":[{"loc":["body","audio_file"],"msg":"Field required"}]}""")));
    }

    [Fact]
    public void The_gateway_timeout_has_its_own_shape()
    {
        var e = GnaniReplies.Error(504, """{"error":"Gateway Timeout","code":"UPSTREAM_TIMEOUT","message":"The speech-to-text model failed to respond within the allocated 5000ms limit."}""");
        Assert.Equal(GnaniProblem.Timeout, e.Problem);
        Assert.Equal("UPSTREAM_TIMEOUT", e.Code);
        Assert.Equal("Gnani took too long", GnaniReplies.Friendly(e));
    }

    [Fact]
    public void A_service_that_is_down_is_a_server_problem()
    {
        var e = GnaniReplies.Error(503, """{"success":false,"error":{"type":"SERVICE_UNAVAILABLE","message":"Text-to-speech service is temporarily unavailable."}}""");
        Assert.Equal(GnaniProblem.Server, e.Problem);
        Assert.Equal("SERVICE_UNAVAILABLE", e.Code);
    }

    [Theory]
    [InlineData(401, "<html>Unauthorized</html>", GnaniProblem.BadKey)]
    [InlineData(429, "{not json", GnaniProblem.Busy)]
    [InlineData(500, "", GnaniProblem.Server)]
    [InlineData(400, "[1,2,3]", GnaniProblem.BadRequest)]
    [InlineData(418, "\"teapot\"", GnaniProblem.BadRequest)]
    [InlineData(302, null, GnaniProblem.Unexpected)]
    public void A_body_that_is_not_the_documented_json_is_judged_by_its_status(int status, string? body, GnaniProblem problem) =>
        Assert.Equal(problem, GnaniReplies.Error(status, body).Problem);

    [Fact]
    public void Unknown_fields_are_ignored()
    {
        var e = GnaniReplies.Error(429, """{"trace":"abc","detail":{"error_code":"RATE_LIMITED","message":"Slow down","retry_in":3,"extra":{"a":1}},"new_thing":[1]}""");
        Assert.Equal(GnaniProblem.Busy, e.Problem);
        Assert.Equal("Slow down", e.Message);
    }

    [Fact]
    public void A_very_long_message_is_cut_short_for_the_log()
    {
        var e = GnaniReplies.Error(400, JsonSerializer.Serialize(new { message = new string('x', 5000) }));
        Assert.True(e.Message.Length <= 201);
    }

    [Fact]
    public void No_network_and_no_answer_have_their_own_words()
    {
        Assert.Equal("No internet", GnaniReplies.Friendly(GnaniReplies.Offline));
        Assert.Equal("Couldn’t reach Gnani", GnaniReplies.Friendly(GnaniReplies.Unreachable));
        Assert.Equal("Gnani took too long", GnaniReplies.Friendly(GnaniReplies.TimedOut));
    }

    [Fact]
    public void The_short_form_of_an_error_carries_no_message()
    {
        var e = GnaniReplies.Error(401, """{"detail":{"error_code":"INVALID_API_KEY","message":"Unauthorized"}}""");
        Assert.Equal("BadKey 401 INVALID_API_KEY", e.Short);
        Assert.Equal("Server 500", GnaniReplies.Error(500, null).Short);
    }
}

public class GnaniSpeechReplyTests
{
    static byte[] Speech(int samples = 2400, int rate = 24000) => Wav.Pcm16(new float[samples], rate);

    [Fact]
    public void A_wav_file_in_the_answer_is_the_speech()
    {
        var wav = Speech();
        var reply = GnaniReplies.Tts(200, wav);
        Assert.True(reply.Ok);
        Assert.Same(wav, reply.Audio);
        Assert.Equal(24000, reply.Info!.SampleRate);
        Assert.Equal(TimeSpan.FromMilliseconds(100), reply.Info.Duration);
        Assert.Null(reply.Error);
    }

    [Fact]
    public void An_error_status_is_never_taken_for_speech()
    {
        var reply = GnaniReplies.Tts(401, Encoding.UTF8.GetBytes("""{"detail":{"error_code":"INVALID_API_KEY","message":"Unauthorized","status_code":401}}"""));
        Assert.False(reply.Ok);
        Assert.Null(reply.Audio);
        Assert.Equal(GnaniProblem.BadKey, reply.Error!.Problem);
    }

    [Fact]
    public void An_unknown_voice_comes_back_as_a_bad_request()
    {
        var reply = GnaniReplies.Tts(400, Encoding.UTF8.GetBytes("""{"success": false, "message": "Voice 'Shivani' is not supported by timbre-v2.5", "status_code": 400}"""));
        Assert.Equal(GnaniProblem.BadRequest, reply.Error!.Problem);
        Assert.Contains("Shivani", reply.Error.Message);
    }

    [Fact]
    public void Speech_wrapped_in_json_as_base64_is_still_played()
    {
        var wav = Speech();
        var json = JsonSerializer.Serialize(new { success = true, request_id = "r1", audio = Convert.ToBase64String(wav) });
        var reply = GnaniReplies.Tts(200, Encoding.UTF8.GetBytes(json));
        Assert.True(reply.Ok);
        Assert.Equal(wav, reply.Audio);

        var nested = JsonSerializer.Serialize(new { data = new { audio_content = "data:audio/wav;base64," + Convert.ToBase64String(wav) } });
        Assert.Equal(wav, GnaniReplies.Tts(200, Encoding.UTF8.GetBytes(nested)).Audio);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"success\":true}")]
    [InlineData("{\"audio\":\"not base64 at all, and long enough to be tried as if it were some audio\"}")]
    [InlineData("{broken")]
    [InlineData("ID3\u0003 an mp3, not a wav")]
    public void Anything_else_with_a_200_is_an_unexpected_answer_and_not_speech(string body)
    {
        var reply = GnaniReplies.Tts(200, Encoding.UTF8.GetBytes(body));
        Assert.False(reply.Ok);
        Assert.Equal(GnaniProblem.Unexpected, reply.Error!.Problem);
    }

    [Fact]
    public void A_200_that_says_it_failed_keeps_its_reason()
    {
        var reply = GnaniReplies.Tts(200, Encoding.UTF8.GetBytes("""{"success":false,"detail":{"error_code":"RATE_LIMITED","message":"Only 1 active request"}}"""));
        Assert.False(reply.Ok);
        Assert.Equal(GnaniProblem.Busy, reply.Error!.Problem);
    }

    [Fact]
    public void A_missing_body_is_handled()
    {
        Assert.Equal(GnaniProblem.Unexpected, GnaniReplies.Tts(200, null).Error!.Problem);
        Assert.Equal(GnaniProblem.Server, GnaniReplies.Tts(500, null).Error!.Problem);
    }
}

public class GnaniTranscriptReplyTests
{
    [Fact]
    public void The_transcript_is_read_from_the_documented_answer()
    {
        var reply = GnaniReplies.Stt(200, """{"success":true,"request_id":"5c1f0a52","transcript":"kal subah saat baje ka alarm laga do","model":"prisma-v3.0","processing_time":0.62,"end_to_end_latency":0.71}""");
        Assert.True(reply.Ok);
        Assert.Equal("kal subah saat baje ka alarm laga do", reply.Transcript);
        Assert.False(reply.Truncated);
    }

    [Fact]
    public void No_speech_is_an_empty_transcript_and_not_an_error()
    {
        var reply = GnaniReplies.Stt(200, """{"success": true, "request_id": "r", "transcript": ""}""");
        Assert.True(reply.Ok);
        Assert.Equal("", reply.Transcript);
    }

    [Fact]
    public void A_transcript_cut_short_says_so()
    {
        var reply = GnaniReplies.Stt(200, """{"success":true,"transcript":"…","model":"prisma-v3.0","truncated":true}""");
        Assert.True(reply.Truncated);
    }

    [Fact]
    public void Stray_spaces_and_line_breaks_are_tidied()
    {
        Assert.Equal("timer five minutes", GnaniReplies.Stt(200, "{\"transcript\":\"  timer  five\\nminutes \"}").Transcript);
    }

    [Fact]
    public void Older_shapes_of_the_answer_are_understood_too()
    {
        Assert.Equal("play music", GnaniReplies.Stt(200, """{"success":true,"text":"play music"}""").Transcript);
        Assert.Equal("play music", GnaniReplies.Stt(200, """{"success":true,"transcript":{"text":"play music","lang":"en"}}""").Transcript);
        Assert.Equal("play music", GnaniReplies.Stt(200, """{"success":true,"output":{"literal":"play music"}}""").Transcript);
        Assert.Equal("", GnaniReplies.Stt(200, """{"success":true,"transcript":null}""").Transcript);
    }

    [Fact]
    public void Extra_fields_do_not_matter()
    {
        var reply = GnaniReplies.Stt(200, """{"new":{"deep":[1,2]},"transcript":"open notepad","detected_language_code":"en-IN","words":[{"w":"open"}]}""");
        Assert.Equal("open notepad", reply.Transcript);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("<html>")]
    [InlineData("[\"a\"]")]
    [InlineData("{\"success\":true}")]
    [InlineData("{\"transcript\":42}")]
    [InlineData("{\"transcript\":\"cut off")]
    public void A_200_without_a_usable_transcript_is_an_unexpected_answer(string? body)
    {
        var reply = GnaniReplies.Stt(200, body);
        Assert.False(reply.Ok);
        Assert.Equal(GnaniProblem.Unexpected, reply.Error!.Problem);
    }

    [Fact]
    public void A_200_that_says_it_failed_is_an_error()
    {
        var reply = GnaniReplies.Stt(200, """{"success":false,"error":{"type":"TRANSCRIPTION_FAILED","message":"Speech recognition service failed to transcribe audio. Please try again."}}""");
        Assert.False(reply.Ok);
        Assert.Equal("TRANSCRIPTION_FAILED", reply.Error!.Code);
    }

    [Fact]
    public void Error_statuses_carry_their_reason()
    {
        Assert.Equal(GnaniProblem.BadKey, GnaniReplies.Stt(401, """{"detail":{"error_code":"INVALID_API_KEY","message":"Unauthorized","status_code":401}}""").Error!.Problem);
        Assert.Equal(GnaniProblem.Timeout, GnaniReplies.Stt(504, """{"error":"Gateway Timeout","code":"UPSTREAM_TIMEOUT","message":"…"}""").Error!.Problem);
        Assert.Equal(GnaniProblem.Server, GnaniReplies.Stt(500, """{"success":false,"error":{"type":"INTERNAL_SERVER_ERROR","message":"Internal server error occurred. Please try again later."}}""").Error!.Problem);
    }
}

public class WavTests
{
    [Fact]
    public void The_header_of_a_16_kHz_mono_recording_is_byte_for_byte_the_standard_one()
    {
        var file = Wav.Pcm16(new float[16000], 16000);
        Assert.Equal(44 + 32000, file.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(file, 0, 4));
        Assert.Equal(36 + 32000, BitConverter.ToInt32(file, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(file, 8, 4));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(file, 12, 4));
        Assert.Equal(16, BitConverter.ToInt32(file, 16));
        Assert.Equal(1, BitConverter.ToInt16(file, 20)); // PCM
        Assert.Equal(1, BitConverter.ToInt16(file, 22)); // mono
        Assert.Equal(16000, BitConverter.ToInt32(file, 24));
        Assert.Equal(32000, BitConverter.ToInt32(file, 28)); // bytes a second
        Assert.Equal(2, BitConverter.ToInt16(file, 32)); // bytes a sample
        Assert.Equal(16, BitConverter.ToInt16(file, 34));
        Assert.Equal("data", Encoding.ASCII.GetString(file, 36, 4));
        Assert.Equal(32000, BitConverter.ToInt32(file, 40));
    }

    [Fact]
    public void Samples_become_little_endian_16_bit_numbers_and_loud_ones_are_clipped()
    {
        var file = Wav.Pcm16(new[] { 0f, 0.5f, -0.5f, 1f, -1f, 2f, -2f }, 16000);
        short At(int i) => BitConverter.ToInt16(file, 44 + i * 2);
        Assert.Equal(0, At(0));
        Assert.Equal(16384, At(1));
        Assert.Equal(-16384, At(2));
        Assert.Equal(32767, At(3));
        Assert.Equal(-32767, At(4));
        Assert.Equal(32767, At(5));
        Assert.Equal(-32768, At(6));
    }

    [Fact]
    public void An_empty_recording_is_still_a_valid_file_with_no_sound()
    {
        var file = Wav.Pcm16(Array.Empty<float>(), 16000);
        Assert.Equal(44, file.Length);
        Assert.Equal(0, BitConverter.ToInt32(file, 40));
        Assert.False(Wav.TryRead(file, out _));
    }

    [Fact]
    public void What_was_written_reads_back()
    {
        Assert.True(Wav.TryRead(Wav.Pcm16(new float[8000], 16000), out var info));
        Assert.Equal(new WavInfo(1, 16000, 1, 16, 44, 16000), info);
        Assert.Equal(TimeSpan.FromMilliseconds(500), info.Duration);
    }

    [Fact]
    public void Chunks_before_the_sound_are_skipped()
    {
        // RIFF, fmt, an odd-length LIST chunk (padded), then data.
        var sound = new byte[] { 1, 0, 2, 0, 3, 0, 4, 0 };
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            w.Write(Encoding.ASCII.GetBytes("RIFF"));
            w.Write(0);
            w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16);
            w.Write((short)1);
            w.Write((short)2);
            w.Write(48000);
            w.Write(48000 * 4);
            w.Write((short)4);
            w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("LIST"));
            w.Write(3);
            w.Write(new byte[] { 9, 9, 9, 0 });
            w.Write(Encoding.ASCII.GetBytes("data"));
            w.Write(sound.Length);
            w.Write(sound);
        }
        var file = ms.ToArray();
        Assert.True(Wav.TryRead(file, out var info));
        Assert.Equal(48000, info.SampleRate);
        Assert.Equal(2, info.Channels);
        Assert.Equal(sound.Length, info.DataLength);
        Assert.Equal(sound, file.AsSpan(info.DataOffset, info.DataLength).ToArray());
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0xFFFFFFFFu)]
    public void A_streamed_file_with_no_real_length_uses_the_rest_of_the_file(uint written)
    {
        var file = Wav.Pcm16(new float[1000], 24000);
        BitConverter.TryWriteBytes(file.AsSpan(40, 4), written);
        Assert.True(Wav.TryRead(file, out var info));
        Assert.Equal(2000, info.DataLength);
    }

    [Fact]
    public void A_length_that_ends_mid_sample_is_rounded_down()
    {
        var file = Wav.Pcm16(new float[10], 24000).AsSpan(0, 44 + 19).ToArray();
        Assert.True(Wav.TryRead(file, out var info));
        Assert.Equal(18, info.DataLength);
    }

    [Fact]
    public void Compressed_or_broken_files_are_refused()
    {
        var good = Wav.Pcm16(new float[100], 24000);
        Assert.False(Wav.TryRead(Array.Empty<byte>(), out _));
        Assert.False(Wav.TryRead(Encoding.ASCII.GetBytes("RIFF1234WAVE"), out _));
        Assert.False(Wav.TryRead(Encoding.ASCII.GetBytes("OggS and so on, long enough to look at"), out _));

        var adpcm = (byte[])good.Clone();
        adpcm[20] = 2; // not PCM
        Assert.False(Wav.TryRead(adpcm, out _));

        var noChannels = (byte[])good.Clone();
        noChannels[22] = 0;
        Assert.False(Wav.TryRead(noChannels, out _));

        var headerOnly = good.AsSpan(0, 44).ToArray();
        Assert.False(Wav.TryRead(headerOnly, out _));

        var wildChunk = (byte[])good.Clone();
        Encoding.ASCII.GetBytes("junk").CopyTo(wildChunk, 36); // a chunk claiming to be huge, and no data after it
        BitConverter.TryWriteBytes(wildChunk.AsSpan(40, 4), 0x7FFFFFF0);
        Assert.False(Wav.TryRead(wildChunk, out _));
    }

    [Fact]
    public void The_upload_drops_the_quiet_ends_and_never_passes_the_limit()
    {
        const int rate = 16000;
        // 40 s of a steady tone: longer than the service takes.
        var tone = Enumerable.Range(0, rate * 40).Select(i => 0.3f * MathF.Sin(i * 0.2f)).ToArray();
        var wav = SttAudio.ToWav(tone, rate);
        Assert.True(Wav.TryRead(wav, out var info));
        Assert.Equal(rate, info.SampleRate);
        Assert.Equal(1, info.Channels);
        Assert.True(info.Duration <= TimeSpan.FromSeconds(29));

        // 1 s of speech inside 5 s of silence: most of the silence is not sent.
        var clip = new float[rate * 5];
        for (int i = rate * 2; i < rate * 3; i++) clip[i] = 0.3f * MathF.Sin(i * 0.2f);
        Assert.True(Wav.TryRead(SttAudio.ToWav(clip, rate), out var small));
        Assert.InRange(small.Duration.TotalSeconds, 1.0, 2.0);
    }
}

public class SpeechCacheTests
{
    static readonly DateTime Day = new(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_same_words_in_the_same_voice_share_one_file()
    {
        Assert.Equal(SpeechCache.Key("Tea's ready!", "Poorvi", "hi-en"), SpeechCache.Key("Tea's ready!", "Poorvi", "hi-en"));
    }

    [Fact]
    public void Any_difference_in_words_voice_language_model_or_quality_is_another_file()
    {
        var key = SpeechCache.Key("Tea's ready!", "Poorvi", "hi-en");
        Assert.NotEqual(key, SpeechCache.Key("Tea's ready", "Poorvi", "hi-en"));
        Assert.NotEqual(key, SpeechCache.Key("tea's ready!", "Poorvi", "hi-en"));
        Assert.NotEqual(key, SpeechCache.Key("Tea's ready!", "Nalini", "hi-en"));
        Assert.NotEqual(key, SpeechCache.Key("Tea's ready!", "Poorvi", "hi-IN"));
        Assert.NotEqual(key, SpeechCache.Key("Tea's ready!", "Poorvi", "hi-en", "timbre-v3.0"));
        Assert.NotEqual(key, SpeechCache.Key("Tea's ready!", "Poorvi", "hi-en", sampleRate: 48000));
        // The parts can't be shuffled into one another.
        Assert.NotEqual(SpeechCache.Key("a\nb", "c", "d"), SpeechCache.Key("b", "c\na", "d"));
    }

    [Fact]
    public void The_file_name_is_a_plain_hash_and_never_the_words()
    {
        var key = SpeechCache.Key("Reminder: call the bank about the loan", "Poorvi", "hi-en");
        Assert.Matches("^[0-9a-f]{32}$", key);
    }

    [Fact]
    public void Nothing_is_deleted_while_the_cache_fits()
    {
        var entries = new[] { new CacheEntry("a", 5_000_000, Day), new CacheEntry("b", 15_000_000, Day.AddHours(1)) };
        Assert.Empty(SpeechCache.Evict(entries));
        Assert.Empty(SpeechCache.Evict(Array.Empty<CacheEntry>()));
    }

    [Fact]
    public void The_cap_is_twenty_megabytes() => Assert.Equal(20 * 1024 * 1024, SpeechCache.MaxBytes);

    [Fact]
    public void The_least_recently_said_go_first_until_it_fits()
    {
        var entries = new[]
        {
            new CacheEntry("newest", 40, Day.AddHours(3)),
            new CacheEntry("oldest", 30, Day),
            new CacheEntry("middle", 30, Day.AddHours(1)),
            new CacheEntry("recent", 30, Day.AddHours(2)),
        };
        Assert.Equal(new[] { "oldest" }, SpeechCache.Evict(entries, 100));
        Assert.Equal(new[] { "oldest", "middle" }, SpeechCache.Evict(entries, 70));
        Assert.Equal(new[] { "oldest", "middle", "recent", "newest" }, SpeechCache.Evict(entries, 0));
    }

    [Fact]
    public void Files_of_the_same_age_go_in_name_order()
    {
        var entries = new[] { new CacheEntry("b", 10, Day), new CacheEntry("a", 10, Day), new CacheEntry("c", 10, Day) };
        Assert.Equal(new[] { "a", "b" }, SpeechCache.Evict(entries, 10));
    }

    [Fact]
    public void The_one_just_written_is_kept_even_when_it_is_the_oldest_by_the_clock()
    {
        var entries = new[] { new CacheEntry("fresh", 60, Day), new CacheEntry("old", 30, Day.AddHours(1)), new CacheEntry("older", 30, Day.AddMinutes(1)) };
        Assert.Equal(new[] { "older", "old" }, SpeechCache.Evict(entries, 60, keep: "fresh"));
    }
}

public class KeyMaskTests
{
    [Fact]
    public void A_saved_key_shows_four_dots_and_its_last_four_characters()
    {
        Assert.Equal("••••1234", KeyMask.Mask("gn_live_abcdefgh1234"));
        Assert.Equal("••••1234", KeyMask.Mask("  gn_live_abcdefgh1234 "));
    }

    [Fact]
    public void A_short_key_shows_only_dots() => Assert.Equal("••••", KeyMask.Mask("abc1234"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_key_shows_nothing(string? key) => Assert.Equal("", KeyMask.Mask(key));

    [Fact]
    public void The_mask_never_holds_more_than_four_characters_of_the_key()
    {
        const string key = "sk-AAAABBBBCCCCDDDDEEEE";
        var mask = KeyMask.Mask(key);
        Assert.Equal(8, mask.Length);
        Assert.DoesNotContain(key[..^4], mask);
    }

    [Theory]
    [InlineData("  abc123XYZ  ", "abc123XYZ")]
    [InlineData("\"abc123XYZ\"", "abc123XYZ")]
    [InlineData("'abc123XYZ'\r\n", "abc123XYZ")]
    [InlineData("X-API-Key-ID: abc123XYZ", "abc123XYZ")]
    [InlineData("x-api-key-id:abc123XYZ", "abc123XYZ")]
    [InlineData("<abc123XYZ>", "abc123XYZ")]
    [InlineData("ab:cd-ef_gh.12", "ab:cd-ef_gh.12")]
    public void A_pasted_key_is_tidied(string pasted, string key) => Assert.Equal(key, KeyMask.Clean(pasted));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("two words")]
    [InlineData("key\twith\ttabs")]
    [InlineData("line one\nline two")]
    [InlineData("चाबी")]
    [InlineData("X-API-Key-ID:")]
    public void What_cannot_be_a_key_is_refused(string? pasted) => Assert.Null(KeyMask.Clean(pasted));

    [Fact]
    public void A_pasted_page_is_not_a_key() => Assert.Null(KeyMask.Clean(new string('a', 513)));
}

public class SpokenPhraseTests
{
    [Theory]
    [InlineData("Tea", "Tea's ready!")]
    [InlineData("tea", "Tea's ready!")]
    [InlineData("Pasta", "Pasta's ready!")]
    public void A_timer_with_a_one_word_name_is_ready(string label, string said) => Assert.Equal(said, SpokenPhrases.Clock(label));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("Time's up")]
    public void A_timer_without_a_name_is_times_up(string? label) => Assert.Equal("Time's up!", SpokenPhrases.Clock(label));

    [Theory]
    [InlineData("call the bank", "Time's up: call the bank.")]
    [InlineData("Eggs", "Time's up: Eggs.")]
    [InlineData("25m", "Time's up: 25m.")]
    [InlineData("चाय", "Time's up: चाय.")]
    public void A_longer_or_awkward_name_is_announced_as_it_is(string label, string said) => Assert.Equal(said, SpokenPhrases.Clock(label));

    [Theory]
    [InlineData("Break time", "Break time!")]
    [InlineData("Long break", "Time for a long break!")]
    [InlineData("Back to focus", "Back to focus!")]
    [InlineData("Focus done", "Focus done!")]
    public void Pomodoro_and_focus_say_what_the_pet_shows(string label, string said) => Assert.Equal(said, SpokenPhrases.Clock(label));

    [Fact]
    public void A_reminder_is_read_out()
    {
        Assert.Equal("Reminder: call mom", SpokenPhrases.Reminder(" call  mom "));
        Assert.Equal("You have a reminder.", SpokenPhrases.Reminder(""));
        Assert.Equal("You have a reminder.", SpokenPhrases.Reminder(null));
    }

    [Fact]
    public void An_alarm_says_the_time_and_its_name()
    {
        Assert.Equal("It's 7:00 AM.", SpokenPhrases.Alarm("7:00 AM", ""));
        Assert.Equal("It's 7:00 AM. Gym.", SpokenPhrases.Alarm("7:00 AM", "gym"));
        Assert.Equal("Your alarm.", SpokenPhrases.Alarm("", null));
    }

    [Fact]
    public void Links_are_not_read_aloud()
    {
        Assert.Equal("Reminder: pay the bill at", SpokenPhrases.Reminder("pay the bill at https://bank.example.com/pay?id=123"));
        Assert.Equal("see and reply", SpokenPhrases.Clean("see www.example.com/x and reply"));
    }

    [Fact]
    public void Long_text_is_cut_at_a_word()
    {
        var text = string.Join(' ', Enumerable.Repeat("remember", 60));
        var said = SpokenPhrases.Clean(text);
        Assert.True(said.Length <= SpokenPhrases.MaxLength);
        Assert.EndsWith("remember", said);
        // One endless word is simply cut.
        Assert.Equal(SpokenPhrases.MaxLength, SpokenPhrases.Clean(new string('a', 400)).Length);
    }

    [Theory]
    [InlineData("Hi!", "Hi!")]
    [InlineData("Time's up", "Time's up")]
    [InlineData("Tea's ready!", "")]
    [InlineData("Hi, I'm Shammy", "")]
    [InlineData(null, "")]
    public void The_pet_shows_the_words_only_when_they_fit_nine_characters(string? text, string shown) =>
        Assert.Equal(shown, SpokenPhrases.PetValue(text));

    [Fact]
    public void The_test_button_says_hello() => Assert.Equal("Hi, I'm Shammy", SpokenPhrases.Hello);

    [Theory]
    [InlineData("Shammy", "Hi, I'm Shammy")]
    [InlineData("Mochi", "Hi, I'm Mochi")]
    [InlineData("", "Hi, I'm Shammy")]
    [InlineData(null, "Hi, I'm Shammy")]
    public void A_renamed_pet_says_its_own_name(string? name, string said) => Assert.Equal(said, SpokenPhrases.HelloFrom(name));
}

public class SpeakGateTests
{
    static readonly DateTime Now = new(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Two_failures_in_a_row_change_nothing()
    {
        var gate = new SpeakGate();
        gate.Failed(Now);
        gate.Failed(Now.AddSeconds(5));
        Assert.True(gate.Allows(Now.AddSeconds(6)));
        Assert.Equal(2, gate.Failures);
        Assert.Null(gate.PausedUntilUtc);
    }

    [Fact]
    public void The_third_failure_in_a_row_pauses_speaking_for_ten_minutes()
    {
        var gate = new SpeakGate();
        gate.Failed(Now);
        gate.Failed(Now.AddMinutes(1));
        gate.Failed(Now.AddMinutes(2));
        Assert.Equal(Now.AddMinutes(12), gate.PausedUntilUtc);
        Assert.False(gate.Allows(Now.AddMinutes(2)));
        Assert.False(gate.Allows(Now.AddMinutes(11).AddSeconds(59)));
        Assert.True(gate.Allows(Now.AddMinutes(12)));
    }

    [Fact]
    public void A_success_in_between_starts_the_count_again()
    {
        var gate = new SpeakGate();
        gate.Failed(Now);
        gate.Failed(Now);
        gate.Succeeded();
        gate.Failed(Now);
        gate.Failed(Now);
        Assert.True(gate.Allows(Now));
        Assert.Equal(2, gate.Failures);
    }

    [Fact]
    public void After_the_pause_it_takes_three_more_failures_to_pause_again()
    {
        var gate = new SpeakGate();
        for (int i = 0; i < 3; i++) gate.Failed(Now);
        var later = Now + SpeakGate.Pause;
        Assert.True(gate.Allows(later));
        Assert.Equal(0, gate.Failures);
        gate.Failed(later);
        gate.Failed(later);
        Assert.True(gate.Allows(later));
        gate.Failed(later);
        Assert.False(gate.Allows(later));
        Assert.Equal(later + SpeakGate.Pause, gate.PausedUntilUtc);
    }

    [Fact]
    public void Failures_reported_during_the_pause_do_not_extend_it()
    {
        var gate = new SpeakGate();
        for (int i = 0; i < 3; i++) gate.Failed(Now);
        gate.Failed(Now.AddMinutes(5));
        Assert.Equal(Now.AddMinutes(10), gate.PausedUntilUtc);
        Assert.Equal(3, gate.Failures);
    }

    [Fact]
    public void A_new_key_clears_the_pause()
    {
        var gate = new SpeakGate();
        for (int i = 0; i < 3; i++) gate.Failed(Now);
        gate.Reset();
        Assert.True(gate.Allows(Now));
        Assert.Equal(0, gate.Failures);
    }
}

public class VoiceRulesTests
{
    static SpeakBlock Speak(string text = "Tea's ready!", bool key = true, bool on = true, bool sounds = true, bool quiet = false, bool paused = false) =>
        VoiceRules.Speak(text, key, on, sounds, quiet, paused);

    [Fact]
    public void With_a_key_the_switch_on_and_sounds_on_shammy_speaks() => Assert.Equal(SpeakBlock.None, Speak());

    [Fact]
    public void Without_a_key_nothing_is_said_whatever_else_is_on()
    {
        Assert.Equal(SpeakBlock.NoKey, Speak(key: false));
        Assert.Equal(SpeakBlock.NoKey, Speak(key: false, on: false, sounds: false, quiet: true, paused: true));
        Assert.Equal("no key", VoiceRules.Describe(SpeakBlock.NoKey));
    }

    [Fact]
    public void The_switch_is_off_until_someone_turns_it_on() => Assert.Equal(SpeakBlock.SwitchOff, Speak(on: false));

    [Fact]
    public void Nothing_is_said_while_the_apps_sounds_are_off() => Assert.Equal(SpeakBlock.SoundsOff, Speak(sounds: false));

    [Fact]
    public void Nothing_is_said_during_a_call_or_meeting() => Assert.Equal(SpeakBlock.Quiet, Speak(quiet: true));

    [Fact]
    public void Nothing_is_said_while_paused_after_failures() => Assert.Equal(SpeakBlock.Paused, Speak(paused: true));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void There_must_be_something_to_say(string text) => Assert.Equal(SpeakBlock.Empty, Speak(text));

    [Fact]
    public void Every_reason_has_words_for_the_pipe()
    {
        var said = Enum.GetValues<SpeakBlock>().Select(VoiceRules.Describe).ToList();
        Assert.Equal(said.Count, said.Distinct().Count());
        Assert.All(said, s => Assert.False(string.IsNullOrWhiteSpace(s)));
    }

    [Fact]
    public void The_test_button_explains_why_it_stayed_silent()
    {
        Assert.Equal("Paste your Gnani key first", VoiceRules.Friendly(SpeakBlock.NoKey));
        Assert.Equal("Notch sounds are off", VoiceRules.Friendly(SpeakBlock.SoundsOff));
        Assert.Equal("Not during a call or meeting", VoiceRules.Friendly(SpeakBlock.Quiet));
    }

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    public void Listening_goes_online_only_with_a_key_the_switch_and_a_network(bool key, bool on, bool network, bool online) =>
        Assert.Equal(online, VoiceRules.ListenOnline(key, on, network));
}
