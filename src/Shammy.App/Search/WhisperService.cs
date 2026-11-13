using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using Shammy.Core.Music;
using Shammy.Core.Search;
using Shammy.Infrastructure;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace Shammy.Search;

/// <summary>
/// The offline backup for voice commands. Voice commands are heard online (Gnani, the user's own key); this only
/// runs when that isn't possible: no key, no internet, or Gnani failed.
/// <list type="bullet">
/// <item>Only the small multilingual "base" model (~148 MB on disk), on the processor. Nothing is loaded at
/// start-up: the model is read in when a command needs it and freed again after a minute without one.</item>
/// <item>The model is pinned to one published revision and its SHA-256, because the file is parsed by native
/// code and a swapped or tampered model must never load. It is downloaded only when it is missing and wanted.</item>
/// <item>Speech is decoded as English once. Two cases listen again with the same model: a play command, primed
/// with song names so Hindi titles come out spelled right, and a transcript that looks wrong for English,
/// retried as Hindi written in Latin letters.</item>
/// </list>
/// </summary>
internal sealed class WhisperService : IDisposable
{
    internal sealed record VoiceModel(string Name, string File, long Bytes, string Sha256)
    {
        public string Url => $"https://huggingface.co/ggerganov/whisper.cpp/resolve/{Revision}/{File}";
        public string LocalPath => Path.Combine(Paths.Models, File);
    }

    const string Revision = "5359861c739e955e79d9a303bcbc70fb988958b1";

    /// <summary>The one model Shammy keeps (see <see cref="Shammy.Core.Search.ModelCleanup"/>).</summary>
    internal static readonly VoiceModel Base = new("base", Shammy.Core.Search.ModelCleanup.Keep, 147_951_465,
        "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe");

    /// <summary>How long the model stays in memory after the last command that used it.</summary>
    static readonly TimeSpan IdleUnload = TimeSpan.FromSeconds(60);

    static readonly Regex Noise = new(@"\[[^\]]*\]|\([^)]*\)|♪", RegexOptions.Compiled);

    // A play command's second pass. ~250 characters is about 20 names: past that the CPU pass slows down
    // more than the extra names help (the catalog match after the pass catches near misses anyway).
    const int SongPromptBudget = 250;

    // The Hindi retry is told what Hinglish looks like in Latin letters, so it writes "kal subah ... laga do"
    // rather than Devanagari: readable in the notch, and what the Hinglish command rewrite expects.
    const string HinglishPrompt = "kal subah alarm laga do. mujhe ek achha phone batao. aaj mausam kaisa hai. paanch minute ka timer laga do.";

    readonly SemaphoreSlim _gate = new(1, 1);
    readonly int _threads = Math.Clamp(Environment.ProcessorCount - 2, 2, 8);
    Engine? _engine;
    Timer? _idle;
    Task<bool>? _download;
    bool _disposed;

    static WhisperService()
    {
        // The processor build only: the graphics-card build would load the video driver (and its memory) even
        // for a model that runs on the processor.
        RuntimeOptions.RuntimeLibraryOrder = new List<RuntimeLibrary> { RuntimeLibrary.Cpu };
    }

    /// <summary>The backup model is on disk (it is read into memory only when a command needs it).</summary>
    public bool IsReady
    {
        get
        {
            try
            {
                var info = new FileInfo(Base.LocalPath);
                return info.Exists && info.Length == Base.Bytes;
            }
            catch (Exception) { return false; }
        }
    }

    /// <summary>The model is in memory right now.</summary>
    public bool IsLoaded => _engine is not null;

    public bool IsDownloading { get; private set; }
    public double DownloadProgress { get; private set; }
    public string? Failure { get; private set; }

    /// <summary>"base (CPU)" while in memory, "base (on disk)" or "-" otherwise.</summary>
    public string ActiveModel => _engine is not null ? "base (CPU)" : IsReady ? "base (on disk)" : "-";

    public event Action<double>? Progress;
    public event Action? Ready;
    public event Action<string>? Failed;

    /// <summary>"auto" detects the language per query (Hindi, English...) at the cost of an extra pass.</summary>
    public string Language { get; set; } = "en";

    /// <summary>Words to prime recognition with (commands, installed app names); read when a model loads.</summary>
    public Func<IEnumerable<string>>? Vocabulary { get; set; }

    /// <summary>Song titles and artists this PC played lately, best first; read on every play command.</summary>
    public Func<IEnumerable<string>>? SongHistory { get; set; }

    static readonly string[] BuiltInSongs = SongCatalog.Songs.Concat(SongCatalog.Artists).ToArray();

    // ---- Getting the model -------------------------------------------------------------------------

    /// <summary>
    /// Fetches the backup model if it isn't here yet (one download at a time). True when it is on disk and good.
    /// Progress, Ready and Failed are raised as it goes.
    /// </summary>
    public Task<bool> DownloadAsync() => _download is { IsCompleted: false } running ? running : _download = DownloadCoreAsync();

    async Task<bool> DownloadCoreAsync()
    {
        try
        {
            if (await Task.Run(() => HasExpectedHash(Base))) return true;
            if (File.Exists(Base.LocalPath)) Log.Warn("The offline voice model didn't match its expected hash; downloading it again");
            await DownloadFileAsync(Base);
            Failure = null;
            Log.Info("Offline voice model downloaded");
            Ready?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            Failure = ex is HttpRequestException ? "Couldn’t download the offline voice (offline?)" : "The offline voice couldn’t be saved";
            Log.Error("Offline voice download failed", ex);
            Failed?.Invoke(Failure);
            return false;
        }
    }

    /// <summary>
    /// Downloads a model to "&lt;file&gt;.part", then checks its hash and moves it into place. An interrupted
    /// download is resumed next time; a finished one that fails the hash is deleted, never loaded.
    /// </summary>
    async Task DownloadFileAsync(VoiceModel model)
    {
        IsDownloading = true;
        DownloadProgress = 0;
        var part = model.LocalPath + ".part";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Shammy/0.9");
            long have = File.Exists(part) ? new FileInfo(part).Length : 0;
            if (have >= model.Bytes) have = 0;
            using var request = new HttpRequestMessage(HttpMethod.Get, model.Url);
            if (have > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            // A server that ignores the range sends the whole file again: start over.
            bool resumed = have > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent;
            long read = resumed ? have : 0;
            long total = resumed ? model.Bytes : response.Content.Headers.ContentLength ?? model.Bytes;
            await using (var src = await response.Content.ReadAsStreamAsync())
            await using (var dst = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write))
            {
                var buffer = new byte[1 << 16];
                double lastReported = -1;
                int n;
                while ((n = await src.ReadAsync(buffer)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n));
                    read += n;
                    double fraction = (double)read / total;
                    if (fraction - lastReported < 0.01) continue;
                    lastReported = fraction;
                    DownloadProgress = fraction;
                    Progress?.Invoke(fraction);
                }
            }
            if (!await Task.Run(() => HasExpectedHash(model, part)))
            {
                File.Delete(part);
                throw new HttpRequestException($"{model.Name} model download failed its integrity check");
            }
            File.Move(part, model.LocalPath, true);
        }
        finally
        {
            IsDownloading = false;
        }
    }

    static bool HasExpectedHash(VoiceModel model) => HasExpectedHash(model, model.LocalPath);

    /// <summary>
    /// Whether the file is the model it should be. A file that passed once is remembered by its size and
    /// last-write time (verified.txt next to the models) and only hashed again when either changes.
    /// </summary>
    static bool HasExpectedHash(VoiceModel model, string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != model.Bytes) return false;
        string stamp = $"{info.Length}|{info.LastWriteTimeUtc.Ticks}|{model.Sha256}";
        if (Verified.Seen(path, stamp)) return true;
        using var fs = File.OpenRead(path);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs));
        bool ok = string.Equals(hash, model.Sha256, StringComparison.OrdinalIgnoreCase);
        if (ok) Verified.Remember(path, stamp);
        return ok;
    }

    /// <summary>Model files already hashed and found good, by size, write time and expected hash.</summary>
    static class Verified
    {
        static readonly object Gate = new();

        static string FilePath(string modelPath) => Path.Combine(Path.GetDirectoryName(modelPath) ?? ".", "verified.txt");

        // Read when asked rather than kept: it is consulted once per model load.
        static Dictionary<string, string> Load(string modelPath)
        {
            var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var file = FilePath(modelPath);
                if (File.Exists(file))
                    foreach (var line in File.ReadAllLines(file))
                        if (line.Split('\t') is [var name, var stamp]) entries[name] = stamp;
            }
            catch (Exception ex) { Log.Warn("Reading the verified-models list failed", ex); }
            return entries;
        }

        public static bool Seen(string modelPath, string stamp)
        {
            lock (Gate) return Load(modelPath).TryGetValue(Path.GetFileName(modelPath), out var known) && known == stamp;
        }

        public static void Remember(string modelPath, string stamp)
        {
            lock (Gate)
            {
                // Only the kept model is remembered; entries for models that are gone are dropped.
                var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [Path.GetFileName(modelPath)] = stamp };
                try { File.WriteAllLines(FilePath(modelPath), entries.Select(e => e.Key + "\t" + e.Value)); }
                catch (Exception ex) { Log.Warn("Saving the verified-models list failed", ex); }
            }
        }
    }

    // ---- Loading and freeing -----------------------------------------------------------------------

    /// <summary>Reads the model in (caller holds the gate). Null when it isn't on disk or won't load.</summary>
    Engine? LoadLocked()
    {
        if (_engine is not null) return _engine;
        if (!HasExpectedHash(Base))
        {
            Failure = IsReady ? "The offline voice model is damaged" : "The offline voice isn’t downloaded";
            return null;
        }
        try
        {
            var sw = Stopwatch.StartNew();
            var factory = WhisperFactory.FromPath(Base.LocalPath, new WhisperFactoryOptions { UseGpu = false });
            _engine = new Engine(this, factory);
            Failure = null;
            Log.Info($"Offline voice loaded in {sw.ElapsedMilliseconds} ms");
            return _engine;
        }
        catch (Exception ex)
        {
            Failure = "The offline voice couldn’t be loaded";
            Log.Error("Loading the offline voice failed", ex);
            return null;
        }
    }

    /// <summary>Frees the model a minute after it was last used (restarted by every use).</summary>
    void ScheduleUnload()
    {
        if (_disposed) return;
        _idle ??= new Timer(_ => _ = UnloadIfIdleAsync());
        _idle.Change(IdleUnload, Timeout.InfiniteTimeSpan);
    }

    async Task UnloadIfIdleAsync()
    {
        // In use right now: the use itself schedules the next try.
        if (!await _gate.WaitAsync(0)) return;
        try
        {
            if (_engine is null) return;
            _engine.Dispose();
            _engine = null;
            _idle?.Dispose();
            _idle = null;
            Log.Info("Offline voice freed after a quiet minute");
        }
        finally
        {
            _gate.Release();
        }
        // The model's managed wrappers and the decode buffers go back now, not at some later GC.
        GC.Collect(2, GCCollectionMode.Forced, blocking: false);
    }

    /// <summary>Test hook: frees the model now (as a quiet minute would). False when it wasn't loaded.</summary>
    public bool UnloadNow()
    {
        _gate.Wait();
        try
        {
            if (_engine is null) return false;
            _engine.Dispose();
            _engine = null;
            _idle?.Dispose();
            _idle = null;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- Transcribing --------------------------------------------------------------------------------

    /// <summary>
    /// What was said, or "" when nothing could be heard (the model missing or failing: <see cref="Failure"/> says
    /// why). Loads the model if it isn't in memory, and frees it a minute after the last use.
    /// </summary>
    public async Task<string> TranscribeAsync(float[] samples, CancellationToken ct = default)
    {
        // The quiet tail while the key is still held is what Whisper loops on ("Play X. Play X.").
        samples = AudioTrim.TrimSilence(samples, MicRecorder.SampleRate);
        // Whisper is unreliable on clips shorter than a second; pad with silence.
        if (samples.Length < MicRecorder.SampleRate)
            Array.Resize(ref samples, MicRecorder.SampleRate + MicRecorder.SampleRate / 4);

        await _gate.WaitAsync(ct);
        try
        {
            _idle?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            var engine = await Task.Run(LoadLocked, ct);
            if (engine is null) return "";

            var (result, probability) = await DecodeAsync(engine.For(samples.Length), samples, ct);
            if (EchoesPrompt(result, engine.CurrentPrompt))
            {
                // It recited the priming text instead of hearing the user: listen again without it.
                Log.Info("Voice: the transcript echoed the prompt; retrying without it");
                (result, probability) = await DecodeAsync(engine.Unprimed(samples.Length), samples, ct);
            }

            // Only play commands and doubtful transcripts pay for another pass; plain English never does.
            if (MusicCommand.LooksLikePlay(result))
            {
                result = await SongPassAsync(engine, samples, result, probability, ct);
            }
            else if (Language == "en" && TranscriptCheck.Assess(result, probability) is var doubt and not TranscriptDoubt.None)
            {
                var sw = Stopwatch.StartNew();
                var (hindi, hindiProbability) = await DecodeAsync(engine.Hindi(samples.Length), samples, ct);
                bool use = TranscriptCheck.PreferRetry(doubt, result, probability, hindi, hindiProbability);
                Log.Info($"Voice: English looked wrong ({doubt}); Hindi pass {sw.ElapsedMilliseconds} ms, {(use ? "used it" : "kept English")}");
                if (use) result = hindi;
            }

            // A Hindi song request in Devanagari is searched for in Latin letters, as music services spell it.
            if (Devanagari.Contains(result) && Devanagari.ToLatin(result) is var latin && MusicCommand.LooksLikePlay(latin)) result = latin;
            return MusicCommand.CorrectSong(result, q => SongMatcher.Correct(q, KnownSongs(), out _));
        }
        finally
        {
            ScheduleUnload();
            _gate.Release();
        }
    }

    /// <summary>
    /// The first pass heard a play command, but song names (Hindi ones especially) are what it gets wrong:
    /// "play kesariya" comes out "Play case Rhea". Listen again with the same model primed with song names, the
    /// user's own first, and keep whichever transcript names a known song (else the more confident one).
    /// </summary>
    async Task<string> SongPassAsync(Engine engine, float[] samples, string first, float firstProbability, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var known = KnownSongs();
        var names = SongMatcher.PromptNames(first, History(), BuiltInSongs, SongPromptBudget);
        string second;
        float secondProbability;
        await using (var processor = engine.Song("Play " + string.Join(", ", names) + ".", samples.Length))
            (second, secondProbability) = await DecodeAsync(processor, samples, ct);
        second = MusicCommand.FirstRequest(second);

        static int Words(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        // A transcript much longer than the first one is the name list recited back, not the user.
        bool recited = Words(second) > Words(first) + 6;
        var firstMatch = MusicCommand.Parse(first) is { } a ? SongMatcher.FindBest(a.Query, known) : null;
        var secondMatch = MusicCommand.Parse(second) is { } b ? SongMatcher.FindBest(b.Query, known) : null;
        bool useSecond = !recited && MusicCommand.LooksLikePlay(second) &&
                         (secondMatch is not null ? firstMatch is null || secondMatch.Score >= firstMatch.Score
                                                  : firstMatch is null && secondProbability > firstProbability);
        Log.Info($"Voice: song pass {sw.ElapsedMilliseconds} ms, {(useSecond ? "used it" : "kept the first")}");
        return useSecond ? second : first;
    }

    IEnumerable<string> History()
    {
        try { return SongHistory?.Invoke().ToList() ?? new List<string>(); }
        catch (Exception ex)
        {
            Log.Warn("Reading the song history failed", ex);
            return Array.Empty<string>();
        }
    }

    IReadOnlyList<string> KnownSongs() => History().Concat(BuiltInSongs).ToList();

    /// <summary>The cleaned transcript and Whisper's average token probability (0..1).</summary>
    static async Task<(string Text, float Probability)> DecodeAsync(WhisperProcessor processor, float[] samples, CancellationToken ct)
    {
        var text = new StringBuilder();
        float sum = 0;
        int count = 0;
        await foreach (var segment in processor.ProcessAsync(samples, ct))
        {
            text.Append(segment.Text);
            sum += segment.Probability;
            count++;
        }
        return (Clean(text.ToString()), count > 0 ? sum / count : 0);
    }

    /// <summary>Encoder frames for a clip on the CPU: 50 per second plus headroom, in steps of 128.</summary>
    static int ClipContext(int sampleCount)
    {
        double seconds = (double)sampleCount / MicRecorder.SampleRate;
        return Math.Clamp(((int)(seconds * 50) + 128 + 127) / 128 * 128, 384, 1500);
    }

    /// <summary>
    /// Primes Whisper with what Shammy listens for, so names and commands come out spelled right. It reads like
    /// earlier speech, never like instructions: instruction-like prompts get recited back as the transcript.
    /// Kept short, since the prompt uses up the model's context.
    /// </summary>
    string Prompt()
    {
        // No numbers in the examples: they'd nudge "volume 40" towards whatever number is written here.
        var sb = new StringBuilder("Play the next song. Open Spotify. Search files. Set the brightness. Turn on Bluetooth. " +
            "Turn up the volume. Mute the mic. Start a timer. Remind me tomorrow. Translate good morning to Hindi.");
        try
        {
            if (Vocabulary?.Invoke() is { } extra)
            {
                var names = extra.Where(w => !string.IsNullOrWhiteSpace(w)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (names.Count > 0)
                {
                    sb.Append(" Open ");
                    int start = sb.Length;
                    foreach (var name in names)
                    {
                        if (sb.Length - start + name.Length > 320) break;
                        if (sb.Length > start) sb.Append(", ");
                        sb.Append(name);
                    }
                    sb.Append('.');
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Building the voice vocabulary failed", ex);
        }
        return sb.ToString();
    }

    /// <summary>
    /// True when the "transcript" is mostly the prompt recited back (Whisper does this on unclear audio):
    /// six or more consecutive prompt words in a row (a real command repeats at most a few).
    /// </summary>
    internal static bool EchoesPrompt(string text, string prompt)
    {
        static string[] Words(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^\p{L}\p{N}\s]", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var t = Words(text);
        const int Run = 6;
        if (t.Length < Run) return false;
        var p = " " + string.Join(' ', Words(prompt)) + " ";
        for (int i = 0; i + Run <= t.Length; i++)
            if (p.Contains(" " + string.Join(' ', t, i, Run) + " ", StringComparison.Ordinal)) return true;
        return false;
    }

    static string Clean(string text) =>
        SpeechFixes.Apply(TranscriptCleaner.RemoveRepeats(Regex.Replace(Noise.Replace(text, " "), @"\s+", " ").Trim()));

    /// <summary>The language or vocabulary changed: the next command builds its processors afresh.</summary>
    public void ResetProcessors()
    {
        _gate.Wait();
        try { _engine?.Reset(); }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            _disposed = true;
            _idle?.Dispose();
            _idle = null;
            _engine?.Dispose();
            _engine = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The loaded model and its processors (cached per window size while it stays loaded).</summary>
    sealed class Engine : IDisposable
    {
        readonly WhisperService _owner;
        readonly WhisperFactory _factory;
        readonly Dictionary<int, WhisperProcessor> _byContext = new();
        readonly Dictionary<int, WhisperProcessor> _unprimed = new();
        readonly Dictionary<int, WhisperProcessor> _hindi = new();
        string? _prompt;

        public Engine(WhisperService owner, WhisperFactory factory)
        {
            _owner = owner;
            _factory = factory;
        }

        public string CurrentPrompt => _prompt ??= _owner.Prompt();

        public WhisperProcessor For(int sampleCount) => Get(sampleCount, _byContext, CurrentPrompt);

        /// <summary>Same settings without the prompt; made only when a transcript echoed the prompt.</summary>
        public WhisperProcessor Unprimed(int sampleCount) => Get(sampleCount, _unprimed, null);

        /// <summary>The Hindi retry for doubtful English transcripts.</summary>
        public WhisperProcessor Hindi(int sampleCount) => Get(sampleCount, _hindi, HinglishPrompt, hindi: true);

        /// <summary>A play command's second pass (not cached: its prompt changes every time).</summary>
        public WhisperProcessor Song(string prompt, int sampleCount) =>
            _factory.CreateBuilder()
                .WithLanguage("en")
                .WithThreads(_owner._threads)
                .WithNoContext()
                .WithSingleSegment()
                .WithProbabilities()
                .WithPrompt(prompt)
                .WithAudioContextSize(ClipContext(sampleCount))
                // One try, capped: a slow fallback or a looping recital costs more than it could fix.
                .WithTemperatureInc(0)
                .WithMaxTokensPerSegment(48)
                .Build();

        WhisperProcessor Get(int sampleCount, Dictionary<int, WhisperProcessor> cache, string? prompt, bool hindi = false)
        {
            // Whisper normally encodes a full 30 s window (1500 frames) even for a 3 s query. Sizing the
            // window to the clip (50 frames per second, plus headroom) cuts CPU time several-fold.
            int context = ClipContext(sampleCount);
            if (cache.TryGetValue(context, out var p)) return p;
            var builder = _factory.CreateBuilder()
                .WithLanguage(hindi ? "hi" : _owner.Language)
                .WithThreads(_owner._threads)
                .WithNoContext()
                .WithSingleSegment()
                .WithProbabilities();
            if (!string.IsNullOrEmpty(prompt)) builder = builder.WithPrompt(prompt);
            if (context < 1500) builder = builder.WithAudioContextSize(context);
            // A retry gets one try: temperature fallbacks made a single Hindi pass take up to 8 s.
            if (hindi) builder = builder.WithTemperatureInc(0).WithMaxTokensPerSegment(96);
            return cache[context] = builder.Build();
        }

        public void Reset()
        {
            foreach (var p in _byContext.Values.Concat(_unprimed.Values).Concat(_hindi.Values)) p.Dispose();
            _byContext.Clear();
            _unprimed.Clear();
            _hindi.Clear();
            _prompt = null;
        }

        public void Dispose()
        {
            Reset();
            _factory.Dispose();
        }
    }
}
