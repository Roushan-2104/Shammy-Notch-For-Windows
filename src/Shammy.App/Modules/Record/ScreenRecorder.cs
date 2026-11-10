using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Shammy.Core.Record;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage.Streams;

namespace Shammy.Modules.Record;

/// <summary>What to record and where to save it. Exactly one of <see cref="Monitor"/>, <see cref="Window"/>, <see cref="Item"/>.</summary>
internal sealed record RecordOptions
{
    /// <summary>The MP4 to write; it must not exist yet.</summary>
    public required string Path { get; init; }

    /// <summary>A whole screen (an HMONITOR).</summary>
    public IntPtr Monitor { get; init; }

    /// <summary>One window (an HWND).</summary>
    public IntPtr Window { get; init; }

    /// <summary>Something the user chose in Windows' own picker.</summary>
    public GraphicsCaptureItem? Item { get; init; }

    /// <summary>Only this part of the source, in its pixels; null for all of it.</summary>
    public (int X, int Y, int Width, int Height)? Area { get; init; }

    public bool Sound { get; init; } = true;
    public bool Mic { get; init; }

    /// <summary>Stop by itself after this long (the test pipe); null to run until stopped or a limit.</summary>
    public TimeSpan? RunFor { get; init; }

    /// <summary>The largest video; a bigger source is scaled down to fit.</summary>
    public int MaxWidth { get; init; } = RecordMaths.MaxWidth;

    public int MaxHeight { get; init; } = RecordMaths.MaxHeight;
}

/// <summary>How a recording ended. <see cref="Path"/> is null when nothing usable was saved (see <see cref="Failure"/>).</summary>
internal sealed record RecordResult(RecordStop Reason, string? Path, TimeSpan Length, long Bytes, int Width, int Height)
{
    /// <summary>A <see cref="RecordFailure"/> code when nothing was saved.</summary>
    public string? Failure { get; init; }

    public bool Saved => Path is not null;

    /// <summary>Pictures handed to the encoder, and how many were skipped because it could not keep up.</summary>
    public long Frames { get; init; }

    /// <summary>Pictures Windows delivered (it only sends one when the screen changes).</summary>
    public long Captured { get; init; }

    public long Dropped { get; init; }

    /// <summary>Whether Windows delivered any sound from the sources that were on.</summary>
    public bool SoundHeard { get; init; }

    /// <summary>Each sound source's format and loudest moment, for the log and the test pipe.</summary>
    public string Sounds { get; init; } = "";

    /// <summary>The most frame textures in use at once (a few; it would grow if the encoder never let go of them).</summary>
    public int Textures { get; init; }

    public override string ToString() => FormattableString.Invariant(
        $"reason={Reason} saved={Saved} failure={Failure ?? "-"} length={Length.TotalSeconds:0.00}s bytes={Bytes} size={Width}x{Height} frames={Frames} captured={Captured} dropped={Dropped} sound={SoundHeard} textures={Textures} sounds=[{Sounds}]");
}

/// <summary>
/// Records a screen or a window to an H.264/AAC MP4. It knows nothing about the notch: a source and options in, a
/// file out. Windows' capture API delivers frames as Direct3D textures; each is copied to a canvas of the
/// recording's size, a pacing thread hands the canvas to the encoder thirty times a second (Windows only sends a
/// frame when the picture changes), and PC sound and the microphone are mixed on the same clock.
/// One recording at a time. <see cref="Finished"/> is raised (on a background thread) for every recording that
/// started, whatever ended it: a stop, a limit, the source closing, or a failure.
/// </summary>
internal sealed class ScreenRecorder : IDisposable
{
    readonly Action<string, Exception?> _warn;
    readonly object _gate = new();
    Session? _session;

    public ScreenRecorder(Action<string, Exception?> warn) => _warn = warn;

    /// <summary>Raised once per started recording, on a background thread.</summary>
    public event Action<RecordResult>? Finished;

    /// <summary>A recording is starting, running or being saved.</summary>
    public bool IsBusy
    {
        get
        {
            lock (_gate) return _session is not null;
        }
    }

    /// <summary>A recording is running (started, and not yet asked to stop).</summary>
    public bool IsRecording
    {
        get
        {
            lock (_gate) return _session is { Running: true };
        }
    }

    /// <summary>How long the running recording has been going.</summary>
    public TimeSpan Elapsed
    {
        get
        {
            lock (_gate) return _session?.Elapsed ?? TimeSpan.Zero;
        }
    }

    /// <summary>Starts recording. Returns null once it is running, or a <see cref="RecordFailure"/> code.</summary>
    public async Task<string?> StartAsync(RecordOptions options)
    {
        Session session;
        lock (_gate)
        {
            if (_session is not null) return RecordFailure.Busy;
            _session = session = new Session(options, _warn, OnFinished);
        }
        string? failure;
        try
        {
            // Off the caller's thread: creating the device and the encoder takes a moment.
            failure = await Task.Run(session.StartAsync).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _warn("Starting the recording failed", ex);
            failure = RecordFailure.Unknown;
        }
        if (failure is null) return null;
        await Task.Run(() => session.Abandon()).ConfigureAwait(false);
        lock (_gate)
        {
            if (ReferenceEquals(_session, session)) _session = null;
        }
        return failure;
    }

    /// <summary>Stops the running recording and finishes the file. Null when nothing is recording.</summary>
    public Task<RecordResult?> StopAsync(RecordStop reason = RecordStop.User)
    {
        Session? session;
        lock (_gate) session = _session;
        if (session is not { Started: true }) return Task.FromResult<RecordResult?>(null);
        return session.StopAsync(reason).ContinueWith(t => (RecordResult?)t.Result, TaskScheduler.Default);
    }

    void OnFinished(Session session, RecordResult result)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_session, session)) _session = null;
        }
        try { Finished?.Invoke(result); }
        catch (Exception ex) { _warn("Handling the finished recording failed", ex); }
    }

    /// <summary>Finishes a running recording so the file is playable (a few seconds at most).</summary>
    public void Dispose()
    {
        try { StopAsync().Wait(TimeSpan.FromSeconds(8)); }
        catch (Exception ex) { _warn("Finishing the recording on exit failed", ex); }
    }

    // ---- One recording ---------------------------------------------------------------------------

    sealed class Session
    {
        const int AudioChunk = RecordSound.Rate / 50; // 20 ms
        const int MaxQueued = 6, MaxTextures = 12;
        // Sound is read this far behind the clock, so a burst Windows delivers late is still in time.
        static readonly TimeSpan AudioLatency = TimeSpan.FromMilliseconds(120);
        static readonly TimeSpan FrameLength = RecordMaths.FrameTime(1);

        sealed class FrameTexture(IntPtr texture, IDirect3DSurface surface)
        {
            public IntPtr Texture { get; } = texture;
            public IDirect3DSurface Surface { get; } = surface;

            /// <summary>
            /// The sample the encoder is holding this texture through. Kept here so it stays alive until its
            /// Processed event: once the garbage collector takes the sample object, that event never arrives and
            /// the texture would never come back.
            /// </summary>
            public MediaStreamSample? InFlight { get; set; }
        }

        readonly record struct QueuedFrame(FrameTexture Texture, TimeSpan Time);

        readonly RecordOptions _options;
        readonly Action<string, Exception?> _warn;
        readonly Action<Session, RecordResult> _finished;
        readonly object _gpuGate = new(), _stopGate = new(), _startGate = new();
        readonly Stopwatch _clock = new();
        readonly ManualResetEventSlim _firstFrame = new(false);
        readonly ManualResetEvent _stopEvent = new(false);
        readonly BlockingCollection<QueuedFrame> _queue = new(new ConcurrentQueue<QueuedFrame>());
        readonly Stack<FrameTexture> _free = new();
        readonly List<FrameTexture> _textures = new();
        readonly List<RecordSound> _sounds = new();

        RecordGpu? _gpu;
        GraphicsCaptureItem? _item;
        Direct3D11CaptureFramePool? _pool;
        GraphicsCaptureSession? _capture;
        IRandomAccessStream? _stream;
        MediaStreamSource? _source;
        Task? _transcode;
        Thread? _pacer;
        Task<RecordResult>? _stopTask;
        IntPtr _latest, _black;
        SizeInt32 _poolSize, _contentSize;
        int _canvasWidth, _canvasHeight, _outWidth, _outHeight;
        long _frames, _sent, _dropped, _audioFrames, _stopAtTicks;
        volatile bool _stopping, _released;
        volatile string? _failure;
        RecordStop _reason;
        bool _fileCreated, _hasAudio;

        public Session(RecordOptions options, Action<string, Exception?> warn, Action<Session, RecordResult> finished)
        {
            _options = options;
            _warn = warn;
            _finished = finished;
        }

        /// <summary>The start succeeded: the recording is running or being saved.</summary>
        public bool Started { get; private set; }

        public bool Running => Started && !_stopping;

        public TimeSpan Elapsed => _stopping ? TimeSpan.FromTicks(Interlocked.Read(ref _stopAtTicks)) : _clock.Elapsed;

        // ---- Start ---------------------------------------------------------------------------------

        public async Task<string?> StartAsync()
        {
            try
            {
                if (!GraphicsCaptureSession.IsSupported()) return RecordFailure.NotSupported;
            }
            catch (Exception ex)
            {
                _warn("Screen capture is not available", ex);
                return RecordFailure.NotSupported;
            }

            var path = System.IO.Path.GetFullPath(_options.Path);
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            }
            catch (Exception ex)
            {
                _warn("Creating the recordings folder failed", ex);
                return RecordFailure.NoFolder;
            }
            if (!RecordLimits.CanStart(FreeBytes(path))) return RecordFailure.DiskLow;

            try
            {
                _gpu = RecordGpu.Create();
            }
            catch (Exception ex)
            {
                _warn("Creating the graphics device failed", ex);
                return RecordFailure.NoGraphics;
            }

            try
            {
                _item = _options.Item
                        ?? (_options.Window != IntPtr.Zero ? RecordGpu.ItemForWindow(_options.Window) : RecordGpu.ItemForMonitor(_options.Monitor));
                var size = _item.Size;
                if (size.Width < 2 || size.Height < 2) return RecordFailure.NoSource;
                var (width, height) = _options.Area is { } area ? (area.Width, area.Height) : (size.Width, size.Height);
                _canvasWidth = RecordMaths.Even(width);
                _canvasHeight = RecordMaths.Even(height);
                (_outWidth, _outHeight) = RecordMaths.OutputSize(_canvasWidth, _canvasHeight, _options.MaxWidth, _options.MaxHeight);

                _black = _gpu.CreateTexture(_canvasWidth, _canvasHeight, black: true);
                _latest = _gpu.CreateTexture(_canvasWidth, _canvasHeight);
                _gpu.Copy(_latest, _black);

                _poolSize = size;
                _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_gpu.Device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
                _pool.FrameArrived += OnFrame;
                _capture = _pool.CreateCaptureSession(_item);
                try { _capture.IsCursorCaptureEnabled = true; }
                catch (Exception ex) { _warn("Asking for the pointer in the recording failed", ex); }
                _item.Closed += OnSourceClosed;
                _capture.StartCapture();
            }
            catch (Exception ex)
            {
                _warn("Capturing that window or screen failed", ex);
                return RecordFailure.NoSource;
            }

            if (_options.Sound) AddSound(loopback: true);
            if (_options.Mic) AddSound(loopback: false);
            _hasAudio = _options.Sound || _options.Mic;

            try
            {
                var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 1 << 16);
                _fileCreated = true;
                _stream = file.AsRandomAccessStream();
            }
            catch (Exception ex)
            {
                _warn("Creating the video file failed", ex);
                return RecordFailure.NoFile;
            }

            PrepareTranscodeResult? prepared = null;
            foreach (bool hardware in new[] { true, false })
            {
                try
                {
                    _source = NewSource();
                    var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = hardware };
                    prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(_source, _stream, NewProfile());
                    if (prepared.CanTranscode) break;
                    _warn($"The video encoder refused the recording ({prepared.FailureReason}, hardware={hardware})", null);
                }
                catch (Exception ex)
                {
                    _warn($"Preparing the video encoder failed (hardware={hardware})", ex);
                }
                prepared = null;
            }
            if (prepared is null) return RecordFailure.Encoder;

            // A still screen sends nothing; don't hold the start for it.
            _firstFrame.Wait(TimeSpan.FromSeconds(1.5));

            _transcode = RunTranscodeAsync(prepared);
            Started = true;
            return null;
        }

        void AddSound(bool loopback)
        {
            var sound = new RecordSound(loopback, NowFrame, _warn);
            if (sound.Start()) _sounds.Add(sound);
            else sound.Dispose();
        }

        MediaStreamSource NewSource()
        {
            var video = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)_canvasWidth, (uint)_canvasHeight);
            video.FrameRate.Numerator = RecordMaths.FrameRate;
            video.FrameRate.Denominator = 1;
            video.PixelAspectRatio.Numerator = 1;
            video.PixelAspectRatio.Denominator = 1;
            var videoStream = new VideoStreamDescriptor(video);
            MediaStreamSource source;
            if (_hasAudio)
            {
                var audio = new AudioStreamDescriptor(AudioEncodingProperties.CreatePcm(RecordSound.Rate, RecordSound.Channels, 16));
                source = new MediaStreamSource(videoStream, audio);
            }
            else
            {
                source = new MediaStreamSource(videoStream);
            }
            source.BufferTime = TimeSpan.Zero;
            source.Starting += (_, e) => e.Request.SetActualStartPosition(TimeSpan.Zero);
            source.SampleRequested += OnSampleRequested;
            return source;
        }

        MediaEncodingProfile NewProfile()
        {
            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
            profile.Video.Width = (uint)_outWidth;
            profile.Video.Height = (uint)_outHeight;
            profile.Video.Bitrate = (uint)RecordMaths.Bitrate(_outWidth, _outHeight);
            profile.Video.FrameRate.Numerator = RecordMaths.FrameRate;
            profile.Video.FrameRate.Denominator = 1;
            profile.Video.PixelAspectRatio.Numerator = 1;
            profile.Video.PixelAspectRatio.Denominator = 1;
            profile.Audio = _hasAudio ? AudioEncodingProperties.CreateAac(RecordSound.Rate, RecordSound.Channels, RecordMaths.AudioBitrate) : null;
            return profile;
        }

        async Task RunTranscodeAsync(PrepareTranscodeResult prepared)
        {
            try
            {
                await prepared.TranscodeAsync().AsTask().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _warn("The video encoder stopped", ex);
                _failure ??= RecordFailure.Encoder;
            }
            // The encoder ended by itself (it broke, or both streams ran out): make sure the file gets finished.
            if (!_stopping) _ = StopAsync(RecordStop.Failed);
        }

        // ---- The clock -----------------------------------------------------------------------------

        /// <summary>The clock starts when the encoder first asks for something, so the file begins at zero.</summary>
        void EnsureRunning()
        {
            if (_clock.IsRunning) return;
            lock (_startGate)
            {
                if (_clock.IsRunning || _stopping) return;
                _clock.Start();
                _pacer = new Thread(Pace) { IsBackground = true, Name = "Shammy recording" };
                _pacer.Start();
            }
        }

        /// <summary>The clock in 48 kHz frames; far in the past until the recording has begun.</summary>
        long NowFrame() => _clock.IsRunning ? _clock.Elapsed.Ticks * RecordSound.Rate / TimeSpan.TicksPerSecond : -RecordSound.Rate * 60L;

        // ---- Video ---------------------------------------------------------------------------------

        void OnFrame(Direct3D11CaptureFramePool pool, object args)
        {
            try
            {
                SizeInt32 content;
                using (var frame = pool.TryGetNextFrame())
                {
                    if (frame is null) return;
                    content = frame.ContentSize;
                    lock (_gpuGate)
                    {
                        if (_released || _gpu is null) return;
                        var texture = RecordGpu.TextureOf(frame.Surface);
                        try
                        {
                            var (textureWidth, textureHeight) = RecordGpu.SizeOf(texture);
                            int x = _options.Area?.X ?? 0, y = _options.Area?.Y ?? 0;
                            int width = Math.Min(_canvasWidth, Math.Min(content.Width, textureWidth) - x);
                            int height = Math.Min(_canvasHeight, Math.Min(content.Height, textureHeight) - y);
                            if (content.Width != _contentSize.Width || content.Height != _contentSize.Height)
                            {
                                // A resized window: wipe what the old size left behind it.
                                if (_contentSize.Width > 0) _gpu.Copy(_latest, _black);
                                _contentSize = content;
                            }
                            _gpu.CopyRegion(_latest, texture, x, y, width, height);
                        }
                        finally
                        {
                            RecordGpu.Release(texture);
                        }
                        _frames++;
                    }
                }
                _firstFrame.Set();

                // The window grew or shrank: later frames need buffers of the new size.
                if (content.Width > 0 && content.Height > 0 && (content.Width != _poolSize.Width || content.Height != _poolSize.Height))
                {
                    lock (_gpuGate)
                    {
                        if (_released || _gpu is null) return;
                        _poolSize = content;
                        pool.Recreate(_gpu.Device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, content);
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_released) _warn("Reading a captured frame failed", ex);
            }
        }

        void OnSourceClosed(GraphicsCaptureItem sender, object args) => _ = StopAsync(RecordStop.SourceClosed);

        /// <summary>
        /// Whether the window was closed or the screen unplugged. Windows only raises the item's Closed event on a
        /// thread with a dispatcher queue, which a recording has none of; but a closed window's item reports a size
        /// of nothing (a minimised one keeps a size), and a screen that is gone no longer answers for its handle.
        /// </summary>
        bool SourceGone()
        {
            try
            {
                if (_item is not { } item) return false;
                var size = item.Size;
                if (size.Width <= 0 || size.Height <= 0) return true;
                if (_options.Item is null && _options.Window == IntPtr.Zero)
                {
                    var info = new MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
                    return !GetMonitorInfo(_options.Monitor, ref info);
                }
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct MonitorInfo
        {
            public int Size;
            public int Left, Top, Right, Bottom, WorkLeft, WorkTop, WorkRight, WorkBottom;
            public uint Flags;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        /// <summary>Thirty times a second: the canvas as it is now goes to the encoder. Also watches the limits.</summary>
        void Pace()
        {
            long index = 0;
            var nextSourceCheck = TimeSpan.FromSeconds(1);
            var nextDiskCheck = TimeSpan.Zero;
            long? free = null;
            try
            {
                while (!_stopping)
                {
                    var now = _clock.Elapsed;
                    var due = RecordMaths.FrameTime(index);
                    if (due > now)
                    {
                        if (_stopEvent.WaitOne(due - now)) break;
                        now = _clock.Elapsed;
                    }
                    else if (now - due > FrameLength)
                    {
                        // Fell behind (a busy PC): skip ahead rather than send frames stamped in the past.
                        long current = now.Ticks * RecordMaths.FrameRate / TimeSpan.TicksPerSecond;
                        _dropped += current - index;
                        index = current;
                        due = RecordMaths.FrameTime(index);
                    }
                    Send(due);
                    index++;

                    if (now >= nextSourceCheck)
                    {
                        nextSourceCheck = now + TimeSpan.FromSeconds(1);
                        if (SourceGone())
                        {
                            _ = StopAsync(RecordStop.SourceClosed);
                            continue;
                        }
                    }
                    if (now >= nextDiskCheck)
                    {
                        nextDiskCheck = now + RecordLimits.DiskCheckEvery;
                        free = FreeBytes(_options.Path);
                    }
                    if (RecordLimits.Check(now, free, _options.RunFor) is { } reason) _ = StopAsync(reason);
                }
            }
            catch (Exception ex)
            {
                _warn("The recording's pacing stopped", ex);
                _failure ??= RecordFailure.Unknown;
                _ = StopAsync(RecordStop.Failed);
            }
            finally
            {
                _queue.CompleteAdding();
            }
        }

        void Send(TimeSpan time)
        {
            if (_queue.Count >= MaxQueued)
            {
                // The encoder is behind; this picture is skipped and the next one carries the right time.
                _dropped++;
                return;
            }
            FrameTexture? texture;
            lock (_gpuGate)
            {
                if (_released || _gpu is null) return;
                texture = Rent();
                if (texture is null)
                {
                    _dropped++;
                    return;
                }
                _gpu.Copy(texture.Texture, _latest);
            }
            _queue.Add(new QueuedFrame(texture, time));
        }

        /// <summary>A texture the encoder is not holding; null when it holds them all. Call under the GPU lock.</summary>
        FrameTexture? Rent()
        {
            if (_free.Count > 0) return _free.Pop();
            if (_textures.Count >= MaxTextures) return null;
            var texture = _gpu!.CreateTexture(_canvasWidth, _canvasHeight);
            var made = new FrameTexture(texture, RecordGpu.Wrap(texture));
            _textures.Add(made);
            return made;
        }

        void Return(FrameTexture texture)
        {
            lock (_gpuGate)
            {
                texture.InFlight = null;
                if (!_released) _free.Push(texture);
            }
        }

        // ---- Feeding the encoder -------------------------------------------------------------------

        void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
        {
            if (!ReferenceEquals(sender, _source)) return;
            try
            {
                EnsureRunning();
                if (args.Request.StreamDescriptor is AudioStreamDescriptor) args.Request.Sample = NextAudio();
                else args.Request.Sample = NextVideo();
            }
            catch (Exception ex)
            {
                _warn("Handing the encoder a sample failed", ex);
                _failure ??= RecordFailure.Unknown;
                // No sample ends that stream; the other follows once the stop below is under way.
                args.Request.Sample = null;
                _ = StopAsync(RecordStop.Failed);
            }
        }

        /// <summary>The next picture, waiting for it; null once the recording has stopped and the queue is empty.</summary>
        MediaStreamSample? NextVideo()
        {
            if (!_queue.TryTake(out var frame, Timeout.Infinite)) return null;
            var sample = MediaStreamSample.CreateFromDirect3D11Surface(frame.Texture.Surface, frame.Time);
            sample.Duration = FrameLength;
            var texture = frame.Texture;
            texture.InFlight = sample;
            sample.Processed += (_, _) => Return(texture);
            _sent++;
            return sample;
        }

        /// <summary>The next 20 ms of mixed sound, once the clock has passed it; null when the recording's end is reached.</summary>
        MediaStreamSample? NextAudio()
        {
            long position = _audioFrames;
            var end = TimeSpan.FromTicks((position + AudioChunk) * TimeSpan.TicksPerSecond / RecordSound.Rate);
            while (!_stopping)
            {
                var wait = end + AudioLatency - _clock.Elapsed;
                if (wait <= TimeSpan.Zero || _stopEvent.WaitOne(wait)) break;
            }
            var start = TimeSpan.FromTicks(position * TimeSpan.TicksPerSecond / RecordSound.Rate);
            if (_stopping && start.Ticks >= Interlocked.Read(ref _stopAtTicks)) return null;

            var mix = new float[AudioChunk * RecordSound.Channels];
            foreach (var sound in _sounds) sound.Timeline.Read(position, mix);
            var pcm = new byte[mix.Length * 2];
            PcmMix.ToInt16(mix, pcm);
            _audioFrames = position + AudioChunk;

            var sample = MediaStreamSample.CreateFromBuffer(pcm.AsBuffer(), start);
            sample.Duration = end - start;
            return sample;
        }

        // ---- Stop ----------------------------------------------------------------------------------

        public Task<RecordResult> StopAsync(RecordStop reason)
        {
            lock (_stopGate)
            {
                if (_stopTask is not null) return _stopTask;
                _reason = reason;
                Interlocked.Exchange(ref _stopAtTicks, _clock.Elapsed.Ticks);
                _stopping = true;
                _stopEvent.Set();
                return _stopTask = Task.Run(FinishAsync);
            }
        }

        async Task<RecordResult> FinishAsync()
        {
            RecordResult result;
            try
            {
                EndPacing();

                bool ended = true;
                if (_transcode is { } transcode)
                {
                    // Both streams have run out; the encoder writes what it holds and closes the file's index.
                    ended = await Task.WhenAny(transcode, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(false) == transcode;
                    if (!ended)
                    {
                        _warn("The video encoder did not finish in time", null);
                        _failure ??= RecordFailure.Encoder;
                    }
                }
                var length = TimeSpan.FromTicks(Interlocked.Read(ref _stopAtTicks));
                int textures = _textures.Count;
                Release(flush: ended);

                long bytes = 0;
                try { bytes = new FileInfo(_options.Path).Length; }
                catch (Exception ex) { _warn("Reading the saved video's size failed", ex); }
                if (_failure is null && (_sent == 0 || bytes < 1024)) _failure = RecordFailure.Unknown;

                if (_failure is { } failure)
                {
                    DeleteFile();
                    result = new RecordResult(RecordStop.Failed, null, length, 0, _outWidth, _outHeight) { Failure = failure };
                }
                else
                {
                    result = new RecordResult(_reason == RecordStop.Failed ? RecordStop.User : _reason, System.IO.Path.GetFullPath(_options.Path), length, bytes,
                        _outWidth, _outHeight);
                }
                result = result with { Frames = _sent, Captured = _frames, Dropped = _dropped, SoundHeard = _sounds.Any(s => s.Heard), Textures = textures, Sounds = string.Join("; ", _sounds.Select(s => s.Describe())) };
            }
            catch (Exception ex)
            {
                _warn("Finishing the recording failed", ex);
                Release(flush: false);
                DeleteFile();
                result = new RecordResult(RecordStop.Failed, null, TimeSpan.Zero, 0, _outWidth, _outHeight) { Failure = RecordFailure.Unknown };
            }
            _finished(this, result);
            return result;
        }

        /// <summary>A start that failed part-way: let go of everything and leave no file behind.</summary>
        public void Abandon()
        {
            lock (_stopGate)
            {
                // Nothing that still fires (a late frame, the source closing) may finish this as a recording.
                _stopTask ??= Task.FromResult(new RecordResult(RecordStop.Failed, null, TimeSpan.Zero, 0, 0, 0));
                _stopping = true;
                _stopEvent.Set();
            }
            EndPacing();
            Release(flush: false);
            DeleteFile();
        }

        /// <summary>Waits for the pacing thread to end (it closes the frame queue), or closes the queue if it never ran.</summary>
        void EndPacing()
        {
            lock (_startGate)
            {
                // The encoder never asked for a sample: no pacing thread exists to close the queue.
                if (_pacer is null) _queue.CompleteAdding();
            }
            _pacer?.Join(TimeSpan.FromSeconds(5));
        }

        void Release(bool flush)
        {
            foreach (var sound in _sounds) sound.Dispose();

            if (_item is not null)
            {
                try { _item.Closed -= OnSourceClosed; }
                catch (Exception) { /* the item is already gone */ }
            }
            Quietly(() => _capture?.Dispose(), "Closing the capture failed");
            if (_pool is not null) Quietly(() => _pool.FrameArrived -= OnFrame, "Letting go of the capture failed");
            Quietly(() => _pool?.Dispose(), "Closing the capture buffers failed");

            if (_source is not null) Quietly(() => _source.SampleRequested -= OnSampleRequested, "Letting go of the encoder failed");
            if (_stream is not null)
            {
                if (flush) Quietly(() => _stream.FlushAsync().AsTask().Wait(TimeSpan.FromSeconds(10)), "Flushing the video file failed");
                Quietly(() => _stream.Dispose(), "Closing the video file failed");
                _stream = null;
            }

            lock (_gpuGate)
            {
                if (_released) return;
                _released = true;
                foreach (var texture in _textures)
                {
                    Quietly(() => texture.Surface.Dispose(), "Closing a frame failed");
                    RecordGpu.Release(texture.Texture);
                }
                _textures.Clear();
                _free.Clear();
                RecordGpu.Release(_latest);
                RecordGpu.Release(_black);
                _latest = _black = IntPtr.Zero;
                _gpu?.Dispose();
                _gpu = null;
            }
        }

        void DeleteFile()
        {
            if (!_fileCreated) return;
            try { File.Delete(_options.Path); }
            catch (Exception ex) { _warn("Deleting the unfinished video failed", ex); }
        }

        void Quietly(Action action, string what)
        {
            try { action(); }
            catch (Exception ex) { _warn(what, ex); }
        }

        /// <summary>Free bytes on the drive the video is saved on; null when that can't be read (a network folder).</summary>
        static long? FreeBytes(string path)
        {
            try
            {
                var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(path));
                return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
