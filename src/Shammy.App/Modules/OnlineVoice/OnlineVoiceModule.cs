using System.Globalization;
using Shammy.Core.Activities;
using Shammy.Core.OnlineVoice;
using Shammy.Infrastructure;
using Shammy.Modules.Settings;
using Shammy.Search;
using Shammy.Shell;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Shammy.Modules.OnlineVoice;

/// <summary>What this module keeps: the user's own Gnani key, in the module store (DPAPI, this Windows account only).</summary>
internal sealed class OnlineVoiceStore
{
    public string? Key { get; set; }
}

/// <summary>The running module, for the Settings screen's "Online voice (Gnani)" rows; null while the feature is turned off.</summary>
internal static class OnlineVoiceLink
{
    public static OnlineVoiceModule? Current { get; set; }
}

/// <summary>
/// Optional online voice through Gnani (docs.gnani.ai), with the user's own key. Everything is off until a key is
/// pasted in Settings and a switch is turned on; without both, nothing is ever sent.
/// <para>"Shammy speaks": a finished timer, a due reminder and a ringing alarm are said aloud (the modules call
/// <see cref="HostLinks.Say"/>). One utterance at a time, a newer one replacing a waiting one; never during a
/// call, a meeting or a focus session, never with the app's sounds off; the same words in the same voice are
/// fetched once and then played from disk. A failure is a log line only, and three in a row pause speaking for
/// ten minutes.</para>
/// <para>"Hinglish listening online": the talk key's recording goes to Gnani and comes back in Latin letters,
/// through <see cref="HostLinks.OnlineTranscribe"/>. No answer in four seconds, or any error, returns null and
/// the local voice model takes over.</para>
/// The key is kept in the module store, never in settings.json, the log or a pipe reply.
/// Pipes: "onlinevoice-state", "onlinevoice-say &lt;text&gt;", "onlinevoice-fake-tts &lt;wav path&gt;",
/// "onlinevoice-fake-stt &lt;text&gt;", "onlinevoice-request tts|stt [text]", "onlinevoice-listen".
/// </summary>
internal sealed class OnlineVoiceModule : IShammyModule
{
    const string SpeakingId = "onlinevoice.speaking";

    /// <summary>Under everything else (media is 20): anything worth showing keeps the pet.</summary>
    const int SpeakingPriority = 19;

    /// <summary>The chime that comes with a timer or reminder plays first, even when the words are already cached.</summary>
    static readonly TimeSpan AfterChime = TimeSpan.FromMilliseconds(700);

    /// <summary>How long to wait before the one retry of a "too many requests" answer.</summary>
    static readonly TimeSpan BusyRetryAfter = TimeSpan.FromMilliseconds(700);

    /// <summary>Words that waited this long behind another utterance are no longer news.</summary>
    static readonly TimeSpan WaitingGoesStale = TimeSpan.FromSeconds(20);

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    sealed record Utterance(string Text, bool Test, Action<string?>? Done, DateTime AskedUtc, string? FakePath);

    readonly SpeakGate _speakGate = new();
    readonly SpeakGate _listenGate = new();
    readonly SpeechPlayer _player = new();
    ModuleContext _ctx = null!;
    GnaniClient _client = null!;
    SpeechCacheStore _cache = null!;
    OnlineVoiceStore _store = new();
    Utterance? _waiting;
    bool _speaking, _listenNeedsLanguage;
    string? _fakeTts, _fakeStt;
    string _lastSay = "nothing yet", _lastListen = "nothing yet";

    public string Name => "onlinevoice";

    public void Start(ModuleContext ctx)
    {
        _ctx = ctx;
        _client = new GnaniClient(ctx.Http);
        _cache = new SpeechCacheStore(Path.Combine(Paths.Root, "onlinevoice-cache"), ctx.Warn);
        _store = ctx.Load<OnlineVoiceStore>();
        _store.Key = KeyMask.Clean(_store.Key);

        HostLinks.Say = Say;
        HostLinks.OnlineTranscribe = TranscribeAsync;
        HostLinks.OnlineListening = ListeningOnline;
        OnlineVoiceLink.Current = this;
        QuietState.Changed += OnQuietChanged;

        ctx.AddPipeCommand("onlinevoice-state", _ => State());
        ctx.AddPipeCommand("onlinevoice-say", arg => Speak(arg));
        ctx.AddPipeCommand("onlinevoice-fake-tts", arg =>
        {
            var path = arg.Trim().Trim('"');
            if (path.Length == 0 || path == "off")
            {
                _fakeTts = null;
                return "ok: fake speech off";
            }
            if (!File.Exists(path)) return "error: no such file";
            _fakeTts = path;
            return "ok: the next say plays this file";
        });
        ctx.AddPipeCommand("onlinevoice-fake-stt", arg =>
        {
            _fakeStt = arg.Trim().Length == 0 || arg.Trim() == "off" ? null : arg.Trim();
            return _fakeStt is null ? "ok: fake transcript off" : "ok: the next online transcription returns this text";
        });
        ctx.AddPipeCommand("onlinevoice-request", WouldSend);
        ctx.AddPipeCommand("onlinevoice-listen", _ =>
        {
            // What a hold of the talk key would get from the online path, on a second of silence.
            if (!ListeningOnline()) return "local (online listening is not active)";
            var task = TranscribeAsync(new float[MicRecorder.SampleRate], CancellationToken.None);
            return task.IsCompletedSuccessfully ? task.Result is { } text ? $"online: '{text}'" : "local (online gave nothing)" : "started: see last_listen in onlinevoice-state";
        });
    }

    public void Dispose()
    {
        QuietState.Changed -= OnQuietChanged;
        if (OnlineVoiceLink.Current == this) OnlineVoiceLink.Current = null;
        HostLinks.Say = null;
        HostLinks.OnlineTranscribe = null;
        HostLinks.OnlineListening = null;
        _player.Dispose();
    }

    // ---- The key ----------------------------------------------------------------------------------

    public bool HasKey => _store.Key is { Length: > 0 };

    /// <summary>"••••1234": all that is ever shown of a saved key.</summary>
    public string MaskedKey => KeyMask.Mask(_store.Key);

    /// <summary>Saves a pasted key (encrypted, for this Windows account). False when it can't be a key.</summary>
    public bool SaveKey(string? pasted)
    {
        if (KeyMask.Clean(pasted) is not { } key) return false;
        _store.Key = key;
        _ctx.Save(_store);
        _speakGate.Reset();
        _listenGate.Reset();
        _listenNeedsLanguage = false;
        _ctx.Info("A Gnani key was saved");
        return true;
    }

    /// <summary>Forgets the key, turns both switches off and deletes the cached speech.</summary>
    public void RemoveKey()
    {
        _store.Key = null;
        _ctx.Save(_store);
        _waiting = null;
        _player.Stop();
        SetSetting(nameof(AppSettings.OnlineVoiceSpeaks), false);
        SetSetting(nameof(AppSettings.OnlineVoiceListens), false);
        _ = Task.Run(() => _cache.Clear());
        _ctx.Info("The Gnani key was removed");
    }

    void SetSetting(string name, object value)
    {
        if (SettingsCatalog.Find(name) is { } info && !Equals(SettingsCatalog.Get(_ctx.Settings, info), value))
            SettingsCatalog.Set(_ctx.Settings, info, value);
    }

    // ---- Shammy speaks ----------------------------------------------------------------------------

    bool SoundsOn => NotchSound.Enabled && NotchSound.Volume > 0;

    /// <summary>What the Test button says: the pet introduces itself.</summary>
    string Hello => SpokenPhrases.HelloFrom(_ctx.Settings.PetName);

    /// <summary><see cref="HostLinks.Say"/>: says a line aloud if the user turned speaking on. Never throws.</summary>
    void Say(string text)
    {
        try
        {
            if (_ctx.Ui.CheckAccess()) Speak(text);
            else _ctx.Ui.BeginInvoke(() => Speak(text));
        }
        catch (Exception ex)
        {
            _ctx.Warn("Speaking failed", ex);
        }
    }

    /// <summary>
    /// The Settings screen's Test: says hello with the saved key whatever the "Shammy speaks" switch says, and
    /// reports the problem in plain words (null when it was said).
    /// </summary>
    public void Test(Action<string?> done) => Speak(Hello, test: true, done);

    /// <summary>
    /// Starts (or queues) an utterance and says at once what happened: "started", "queued", or why not
    /// ("no key", "off", "sounds off", "quiet", "paused"). Nothing is sent to Gnani unless it started.
    /// </summary>
    string Speak(string? raw, bool test = false, Action<string?>? done = null)
    {
        var text = SpokenPhrases.Clean(raw);
        string? fake = _fakeTts;
        var now = DateTime.UtcNow;
        var block = VoiceRules.Speak(text, HasKey || fake is not null, _ctx.Settings.OnlineVoiceSpeaks || test || fake is not null,
            SoundsOn, QuietState.IsQuiet, paused: !test && fake is null && !_speakGate.Allows(now));
        if (block != SpeakBlock.None)
        {
            _lastSay = "skipped: " + VoiceRules.Describe(block);
            Tell(done, VoiceRules.Friendly(block));
            return VoiceRules.Describe(block);
        }
        _fakeTts = null;
        var utterance = new Utterance(text, test, done, now, fake);
        if (_speaking)
        {
            // One at a time; a newer line replaces the one that was waiting.
            if (_waiting is { } replaced) Tell(replaced.Done, "Something newer came up");
            _waiting = utterance;
            return "queued";
        }
        _speaking = true;
        _ = RunAsync(utterance);
        return fake is not null ? "started (fake)" : "started";
    }

    async Task RunAsync(Utterance utterance)
    {
        string? problem;
        try
        {
            problem = await SayAsync(utterance);
        }
        catch (Exception ex)
        {
            // No output device, a file that vanished: the notch already showed the alert, so a log line is enough.
            _ctx.Warn("Speaking failed", ex);
            _lastSay = "failed: " + ex.GetType().Name;
            problem = "That didn’t work";
        }
        _ctx.Remove(SpeakingId);
        _speaking = false;
        Tell(utterance.Done, problem);

        if (_waiting is not { } next) return;
        _waiting = null;
        if (DateTime.UtcNow - next.AskedUtc > WaitingGoesStale)
        {
            _lastSay = "skipped: waited too long";
            Tell(next.Done, "Try again");
            return;
        }
        _speaking = true;
        _ = RunAsync(next);
    }

    /// <summary>Fetches (or finds) the speech and plays it. Returns the problem in plain words, or null when it was said.</summary>
    async Task<string?> SayAsync(Utterance u)
    {
        if (StaySilent() is { } early) return early;
        var sw = Stopwatch.StartNew();
        byte[] wav;
        WavInfo? info;
        string source;
        if (u.FakePath is { } path)
        {
            wav = await Task.Run(() => File.ReadAllBytes(path));
            if (!Wav.TryRead(wav, out info))
            {
                _lastSay = "failed: the fake file isn't a PCM WAV";
                return "That file isn’t a WAV Shammy can play";
            }
            source = "fake";
        }
        else
        {
            var voice = GnaniVoices.Find(_ctx.Settings.OnlineVoiceName);
            var language = GnaniVoices.LanguageFor(voice, u.Text);
            var cacheKey = SpeechCache.Key(u.Text, voice.Name, language);
            var cached = await Task.Run(() => _cache.Read(cacheKey));
            if (cached is not null && Wav.TryRead(cached, out info))
            {
                wav = cached;
                source = "cache";
            }
            else
            {
                if (_store.Key is not { Length: > 0 } key)
                {
                    _lastSay = "skipped: no key";
                    return VoiceRules.Friendly(SpeakBlock.NoKey);
                }
                var reply = await FetchAsync(GnaniApi.Tts(new TtsOptions(u.Text, voice.Name, language), key));
                if (!reply.Ok) return SpeakingFailed(reply.Error ?? GnaniReplies.Unreachable);
                _speakGate.Succeeded();
                wav = reply.Audio!;
                info = reply.Info!;
                source = string.Create(Inv, $"network {sw.ElapsedMilliseconds} ms");
                var fresh = wav;
                _ = Task.Run(() => _cache.Write(cacheKey, fresh));
            }
        }

        if (!u.Test && sw.Elapsed < AfterChime) await Task.Delay(AfterChime - sw.Elapsed);
        // A call may have started, or the sounds been turned off, while the speech was on its way.
        if (StaySilent() is { } late) return late;

        // The pet talks only when the notch has nothing else to show; a card, an alert or a search keeps it.
        if (_ctx.Notch.Mode == NotchMode.Idle)
            _ctx.Post(new Activity(SpeakingId, ActivityKind.Custom, SpeakingPriority,
                new SpeakingContent(_ctx.Settings.PetName, u.Text), DateTime.UtcNow + info.Duration + TimeSpan.FromSeconds(1.5)));

        var playing = _player.PlayAsync(wav, info, NotchSound.Volume);
        // A device that never reports the end must not hold up the next line.
        await Task.WhenAny(playing, Task.Delay(info.Duration + TimeSpan.FromSeconds(3)));
        if (!playing.IsCompleted) _player.Stop();
        _lastSay = string.Create(Inv, $"said ({source}, {info.Duration.TotalSeconds:0.0} s of speech)");
        return null;
    }

    async Task<TtsReply> FetchAsync(GnaniRequest request)
    {
        using var limit = new CancellationTokenSource(GnaniApi.TtsTimeout);
        try
        {
            var reply = await _client.SpeakAsync(request, limit.Token);
            // Some accounts may run one request at a time (a voice command may be in flight): one more try, inside the same six seconds.
            if (reply.Error is not { Problem: GnaniProblem.Busy }) return reply;
            await Task.Delay(BusyRetryAfter, limit.Token);
            return await _client.SpeakAsync(request, limit.Token);
        }
        catch (OperationCanceledException)
        {
            return new TtsReply(null, null, GnaniReplies.TimedOut);
        }
    }

    /// <summary>Why nothing may be played right now (a call, the sounds off), or null.</summary>
    string? StaySilent()
    {
        var block = QuietState.IsQuiet ? SpeakBlock.Quiet : !SoundsOn ? SpeakBlock.SoundsOff : SpeakBlock.None;
        if (block == SpeakBlock.None) return null;
        _lastSay = "skipped: " + VoiceRules.Describe(block);
        return VoiceRules.Friendly(block);
    }

    string SpeakingFailed(GnaniError error)
    {
        _speakGate.Failed(DateTime.UtcNow);
        bool paused = _speakGate.PausedUntilUtc is not null;
        // The service's own words, never the text that was to be said.
        _ctx.Warn($"Speaking failed: {error.Short}{(error.Message.Length > 0 ? " · " + error.Message : "")}" +
                  (paused ? "; speaking is paused for 10 minutes" : ""));
        _lastSay = "failed: " + error.Short;
        return GnaniReplies.Friendly(error);
    }

    void Tell(Action<string?>? done, string? problem)
    {
        if (done is null) return;
        try { done(string.IsNullOrEmpty(problem) ? null : problem); }
        catch (Exception ex) { _ctx.Warn("Reporting a speech result failed", ex); }
    }

    // A call, a meeting or a focus session started: stop talking, and forget what was waiting.
    void OnQuietChanged(bool quiet)
    {
        if (!quiet) return;
        _waiting = null;
        _player.Stop();
    }

    // ---- Hinglish listening online ------------------------------------------------------------------

    /// <summary>
    /// <see cref="HostLinks.OnlineListening"/>: whether a recording made now would be sent to Gnani (the listening
    /// view then says "online"). False after three failures in a row, for ten minutes.
    /// </summary>
    bool ListeningOnline() =>
        _fakeStt is not null ||
        (VoiceRules.ListenOnline(HasKey, _ctx.Settings.OnlineVoiceListens, GnaniClient.NetworkUp()) && _listenGate.Allows(DateTime.UtcNow));

    /// <summary>
    /// <see cref="HostLinks.OnlineTranscribe"/>: the recording (16 kHz mono) as text in Latin letters, or null
    /// when the local model should be used instead: the mode is off, Gnani took longer than four seconds, or
    /// anything went wrong. Throws only when <paramref name="ct"/> is cancelled.
    /// </summary>
    async Task<string?> TranscribeAsync(float[] samples, CancellationToken ct)
    {
        if (_fakeStt is { } fake)
        {
            _fakeStt = null;
            _lastListen = "fake";
            return fake;
        }
        if (!ListeningOnline() || _store.Key is not { Length: > 0 } key) return null;

        var sw = Stopwatch.StartNew();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(GnaniApi.SttTimeout);
        try
        {
            var wav = await Task.Run(() => SttAudio.ToWav(samples, MicRecorder.SampleRate), limit.Token);
            var reply = await SendAsync(wav, key, limit.Token);
            if (!reply.Ok && !_listenNeedsLanguage && reply.Error is { } first && GnaniReplies.WantsLanguage(first))
            {
                // The documentation says the language is optional here; if the service disagrees, name its default.
                _listenNeedsLanguage = true;
                reply = await SendAsync(wav, key, limit.Token);
            }
            if (reply.Ok)
            {
                _listenGate.Succeeded();
                // Never what was said: the log isn't a transcript archive.
                int words = reply.Transcript!.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                _lastListen = string.Create(Inv, $"heard {words} words in {sw.ElapsedMilliseconds} ms{(reply.Truncated ? " (cut short)" : "")}");
                return reply.Transcript;
            }
            return ListeningFailed(reply.Error ?? GnaniReplies.Unreachable);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ListeningFailed(GnaniReplies.TimedOut);
        }
        catch (Exception ex)
        {
            _ctx.Warn("Online listening failed; using the local voice", ex);
            _listenGate.Failed(DateTime.UtcNow);
            _lastListen = "failed: " + ex.GetType().Name;
            return null;
        }
    }

    Task<SttReply> SendAsync(byte[] wav, string key, CancellationToken ct)
    {
        var options = new SttOptions(LanguageCode: _listenNeedsLanguage ? GnaniApi.SttAutoLanguage : null);
        return _client.TranscribeAsync(GnaniApi.Stt(options, key, wav.Length), wav, ct);
    }

    string? ListeningFailed(GnaniError error)
    {
        _listenGate.Failed(DateTime.UtcNow);
        bool paused = _listenGate.PausedUntilUtc is not null;
        _ctx.Warn($"Online listening failed, using the local voice: {error.Short}{(error.Message.Length > 0 ? " · " + error.Message : "")}" +
                  (paused ? "; online listening is paused for 10 minutes" : ""));
        _lastListen = "failed: " + error.Short;
        return null;
    }

    // ---- Pipes ------------------------------------------------------------------------------------

    string State()
    {
        var s = _ctx.Settings;
        var now = DateTime.UtcNow;
        var (count, bytes) = _cache.Size();
        string Paused(SpeakGate gate) => gate.Allows(now) || gate.PausedUntilUtc is not { } until ? "no"
            : string.Create(Inv, $"{Math.Ceiling((until - now).TotalSeconds)}s");
        return string.Create(Inv, $"key={(HasKey ? "saved" : "none")} speaks={(s.OnlineVoiceSpeaks ? "on" : "off")} listens={(s.OnlineVoiceListens ? "on" : "off")} ") +
               $"voice={GnaniVoices.Find(s.OnlineVoiceName).Name} listening_online={(ListeningOnline() ? "yes" : "no")} " +
               $"speaking={(_speaking ? "yes" : "no")} waiting={(_waiting is null ? "no" : "yes")} pet={(_ctx.Get(SpeakingId) is null ? "no" : "talking")} " +
               $"sounds={(SoundsOn ? "on" : "off")} quiet={(QuietState.IsQuiet ? "yes" : "no")} " +
               $"last_say='{_lastSay}' say_failures={_speakGate.Failures} say_paused={Paused(_speakGate)} " +
               $"last_listen='{_lastListen}' listen_failures={_listenGate.Failures} listen_paused={Paused(_listenGate)} " +
               string.Create(Inv, $"cache={count} files {bytes / 1024} KB ") +
               $"fake_tts={(_fakeTts is null ? "off" : "armed")} fake_stt={(_fakeStt is null ? "off" : "armed")}";
    }

    /// <summary>"onlinevoice-request tts|stt [text]": the request that would be sent, with "&lt;key&gt;" where the key goes.</summary>
    string WouldSend(string arg)
    {
        var words = arg.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        switch (words.Length > 0 ? words[0].ToLowerInvariant() : "")
        {
            case "tts":
            {
                var text = SpokenPhrases.Clean(words.Length > 1 ? words[1] : SpokenPhrases.Clock("Tea"));
                var voice = GnaniVoices.Find(_ctx.Settings.OnlineVoiceName);
                var request = GnaniApi.Tts(new TtsOptions(text, voice.Name, GnaniVoices.LanguageFor(voice, text)), GnaniApi.KeyPlaceholder);
                return request.Describe() + " -> 200 with the WAV file itself; 6 s limit";
            }
            case "stt":
            {
                // One second of 16 kHz mono, to show the size of a real upload.
                int bytes = Wav.HeaderLength + MicRecorder.SampleRate * 2;
                var options = new SttOptions(LanguageCode: _listenNeedsLanguage ? GnaniApi.SttAutoLanguage : null);
                return GnaniApi.Stt(options, GnaniApi.KeyPlaceholder, bytes).Describe() +
                       " (multipart/form-data; 16 kHz mono 16-bit WAV, here 1 s) -> 200 with JSON {\"transcript\": …}; 4 s limit, then the local voice";
            }
            default:
                return "error: usage onlinevoice-request tts|stt [text]";
        }
    }
}
