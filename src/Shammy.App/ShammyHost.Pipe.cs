using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Shammy.Core.Activities;
using Shammy.Infrastructure;
using Shammy.Interop;
using Shammy.Notifications;
using Shammy.Playback;
using Shammy.Search;
using Shammy.Shelf;
using Shammy.Shell;
using Shammy.SystemInfo;
using Shammy.Views;
using Microsoft.Win32;

namespace Shammy;

/// <summary>The test pipe's host commands (SHAMMY_TEST_PIPE=1 only): states, fakes and test hooks.</summary>
internal sealed partial class ShammyHost
{
    [System.Runtime.InteropServices.DllImport("psapi.dll")]
    static extern bool EmptyWorkingSet(IntPtr process);

    /// <summary>Hands the pages Shammy isn't touching back to Windows (they come back on their own if needed).</summary>
    static void TrimWorkingSet(IntPtr process)
    {
        try { EmptyWorkingSet(process); } catch (Exception) { }
    }

    /// <summary>Commands for scripted testing over the local pipe (see <see cref="CommandServer"/>).</summary>
    string HandleCommand(string line)
    {
        var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "error: empty";
        string arg = parts.Length > 1 ? parts[1] : "";
        double Num(string s) => double.Parse(s, CultureInfo.InvariantCulture);

        switch (parts[0].ToLowerInvariant())
        {
            case "ping": return "pong";
            case "test-action":
                // A clickable message like "update ready", for testing hover and click behaviour. SHAMMY_TEST_TOP=1
                // puts it above everything (e.g. a real call's mic pill) so tests still see it.
                Post(new Activity("test-action", ActivityKind.Message,
                    Environment.GetEnvironmentVariable("SHAMMY_TEST_TOP") == "1" ? 1000 : PriorityDownloadDone,
                    new MessagePayload("", arg.Length > 0 ? arg : "Test action · click me"), In(6))
                {
                    Click = () => _testActionClicks++,
                });
                return "ok";
            case "hover-diag": return _notch.HoverDiag;
            case "glass-wallpaper": return _notch.SetTestWallpaper(arg);
            case "hit":
            {
                // Test: what the notch draws at a screen pixel ("hit 487 318"): the element chain under it.
                var xy = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (xy.Length < 2 || !int.TryParse(xy[0], out var sx) || !int.TryParse(xy[1], out var sy)) return "error: usage hit <x> <y>";
                var local = _notch.PointFromScreen(new System.Windows.Point(sx, sy));
                var found = new List<string>();
                void Walk(System.Windows.DependencyObject d)
                {
                    int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(d);
                    for (int i = 0; i < n; i++)
                    {
                        var c = System.Windows.Media.VisualTreeHelper.GetChild(d, i);
                        if (c is System.Windows.UIElement { IsVisible: false }) continue;
                        if (arg.Contains("paint") && c is System.Windows.Media.Visual v)
                        {
                            var cb = System.Windows.Media.VisualTreeHelper.GetContentBounds(v);
                            if (!cb.IsEmpty)
                                try
                                {
                                    var wb = v.TransformToAncestor(_notch).TransformBounds(cb);
                                    var probe = wb; probe.Inflate(1.5, 1.5);
                                    if (probe.Contains(local) && wb.Width < 200)
                                        found.Add($"paints:{v.GetType().Name}{(v is System.Windows.FrameworkElement pf && pf.Name.Length > 0 ? "#" + pf.Name : "")}{(v is System.Windows.Controls.TextBlock ptb ? $" '{ptb.Text}'" : "")} {wb.Width:0.#}x{wb.Height:0.#} at {wb.X:0.#},{wb.Y:0.#} parent={(System.Windows.Media.VisualTreeHelper.GetParent(v) as System.Windows.FrameworkElement)?.GetType().Name}");
                                }
                                catch (InvalidOperationException) { }
                        }
                        if (c is System.Windows.FrameworkElement fe && fe.ActualWidth > 0 && (arg.Contains("all") || (fe.ActualWidth <= 24 && fe.ActualHeight <= 24)))
                        {
                            try
                            {
                                var box = fe.TransformToAncestor(_notch).TransformBounds(new System.Windows.Rect(0, 0, fe.ActualWidth, fe.ActualHeight));
                                if (!arg.Contains("all")) box.Inflate(3, 3);
                                if (box.Contains(local))
                                {
                                    var parent = System.Windows.Media.VisualTreeHelper.GetParent(fe) as System.Windows.FrameworkElement;
                                    found.Add($"{fe.GetType().Name}{(fe.Name.Length > 0 ? "#" + fe.Name : "")} {fe.ActualWidth:0.#}x{fe.ActualHeight:0.#} op={fe.Opacity:0.##}" +
                                              (fe is System.Windows.Controls.TextBlock tb ? $" '{tb.Text}'" : "") + $" in {parent?.GetType().Name}");
                                }
                            }
                            catch (InvalidOperationException) { }
                        }
                        Walk(c);
                    }
                }
                Walk(_notch);
                var focus = System.Windows.Input.Keyboard.FocusedElement as System.Windows.FrameworkElement;
                string where = "";
                try { if (focus is not null && focus.IsVisible) where = focus.TransformToAncestor(_notch).TransformBounds(new System.Windows.Rect(0, 0, focus.ActualWidth, focus.ActualHeight)).ToString(); } catch (InvalidOperationException) { }
                found.Add($"focus={focus?.GetType().Name ?? "none"}{(focus is { Name.Length: > 0 } ? "#" + focus.Name : "")} {focus?.ActualWidth:0.#}x{focus?.ActualHeight:0.#} at {where} local={local}");
                return string.Join(" | ", found);
            }
            case "render-png":
            {
                // Test: saves what the island draws right now (rendered in-app, device pixels) and replies with where
                // that image's top-left sits on screen, so a screen capture can be compared with it ("render-png C:\x.png").
                if (arg.Length == 0) return "error: usage render-png <path>";
                if (_notch.Content is not System.Windows.FrameworkElement root) return "error: no content";
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(root);
                int w = (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), h = (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY);
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(root);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                using (var fs = System.IO.File.Create(arg)) enc.Save(fs);
                var at = root.PointToScreen(new System.Windows.Point(0, 0));
                return $"ok {(int)Math.Round(at.X)} {(int)Math.Round(at.Y)} {w} {h}";
            }
            case "render-el":
            {
                // Test: renders the first element of a type on its own ("render-el LyricsPanel C:\x.png") and says its size.
                var bits = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (bits.Length < 2) return "error: usage render-el <TypeName> <path>";
                System.Windows.FrameworkElement? found = null;
                void Seek(System.Windows.DependencyObject d)
                {
                    if (found is not null) return;
                    if (d is System.Windows.FrameworkElement fe && fe.GetType().Name == bits[0]) { found = fe; return; }
                    for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++) Seek(System.Windows.Media.VisualTreeHelper.GetChild(d, i));
                }
                Seek(_notch);
                if (found is null) return "error: none found";
                var dpiEl = System.Windows.Media.VisualTreeHelper.GetDpi(found);
                int ew = Math.Max(1, (int)Math.Ceiling(found.ActualWidth * dpiEl.DpiScaleX)), eh = Math.Max(1, (int)Math.Ceiling(found.ActualHeight * dpiEl.DpiScaleY));
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(ew, eh, dpiEl.PixelsPerInchX, dpiEl.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
                var dv = new System.Windows.Media.DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(System.Windows.Media.Brushes.Black, null, new System.Windows.Rect(0, 0, found.ActualWidth, found.ActualHeight));
                    dc.DrawRectangle(new System.Windows.Media.VisualBrush(found), null, new System.Windows.Rect(0, 0, found.ActualWidth, found.ActualHeight));
                }
                bmp.Render(dv);
                var pngEnc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                pngEnc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using (var fs = System.IO.File.Create(bits[1])) pngEnc.Save(fs);
                return $"ok {found.ActualWidth:0}x{found.ActualHeight:0} opacity={found.Opacity:0.00} visible={found.IsVisible}";
            }
            case "paint-probe":
            {
                // Test: renders the island with one layer hidden at a time and reads one screen pixel, to find which
                // layer paints it ("paint-probe 747 318").
                var xy = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (xy.Length < 2 || !int.TryParse(xy[0], out var sx) || !int.TryParse(xy[1], out var sy)) return "error: usage paint-probe <x> <y>";
                if (_notch.Content is not System.Windows.FrameworkElement root) return "error: no content";
                var local = _notch.PointFromScreen(new System.Windows.Point(sx, sy));
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(root);
                string Pixel()
                {
                    int w = (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), h = (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY);
                    var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
                    rtb.Render(root);
                    int px = (int)(local.X * dpi.DpiScaleX), py = (int)(local.Y * dpi.DpiScaleY);
                    var best = "";
                    int bestSum = -1;
                    // The brightest pixel in a 5×5 patch round the point (the mark is 2×1).
                    for (int dy = -2; dy <= 2; dy++)
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            int x = px + dx, y = py + dy;
                            if (x < 0 || y < 0 || x >= w || y >= h) continue;
                            var p = new byte[4];
                            rtb.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), p, 4, 0);
                            int sum = p[0] + p[1] + p[2];
                            if (sum > bestSum) { bestSum = sum; best = $"#{p[3]:X2}{p[2]:X2}{p[1]:X2}{p[0]:X2}@{dx},{dy}"; }
                        }
                    return best;
                }
                var report = new List<string> { "all=" + Pixel() };
                var layers = new List<(string Name, System.Windows.UIElement El)>();
                void Add(string name, object? o) { if (o is System.Windows.UIElement e) layers.Add((name, e)); }
                foreach (var n in new[] { "ShadowShape", "Shape", "Edge", "HitArea", "ContentHost", "Expanded" }) Add(n, _notch.FindName(n));
                if (_notch.FindName("Expanded") is System.Windows.FrameworkElement ex)
                {
                    foreach (var n in new[] { "Body", "HomePanel", "ToolRow" }) Add(n, ex.FindName(n));
                    if (ex.FindName("ToolRow") is System.Windows.Controls.Panel row)
                        for (int i = 0; i < row.Children.Count; i++)
                        {
                            Add($"slot{i}", row.Children[i]);
                            if (row.Children[i] is System.Windows.Controls.Panel slot)
                                for (int j = 0; j < slot.Children.Count; j++) Add($"slot{i}.{j}:{slot.Children[j].GetType().Name}", slot.Children[j]);
                        }
                }
                foreach (var (name, el) in layers)
                {
                    double was = el.Opacity;
                    el.Opacity = 0;
                    el.UpdateLayout();
                    report.Add($"-{name}=" + Pixel());
                    el.Opacity = was;
                }
                return string.Join(" ", report);
            }
            case "mem-diag":
            {
                // Where the memory is: .NET's own heap vs everything else; "mem-diag gc" collects first.
                if (arg.Trim() == "gc")
                {
                    System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(2, GCCollectionMode.Forced, true, true);
                    GC.WaitForPendingFinalizers();
                }
                var info = GC.GetGCMemoryInfo();
                using var me = System.Diagnostics.Process.GetCurrentProcess();
                if (arg.Trim() == "trim") TrimWorkingSet(me.Handle);
                if (arg.Trim() == "mods")
                    return string.Join(",", me.Modules.Cast<System.Diagnostics.ProcessModule>().Select(m => m.ModuleName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
                return $"ws={me.WorkingSet64 / 1048576}MB private={me.PrivateMemorySize64 / 1048576}MB managed={GC.GetTotalMemory(false) / 1048576}MB " +
                       $"heap={info.HeapSizeBytes / 1048576}MB committed={info.TotalCommittedBytes / 1048576}MB gen0={GC.CollectionCount(0)} gen2={GC.CollectionCount(2)} " +
                       $"modules={me.Modules.Count} threads={me.Threads.Count} handles={me.HandleCount}";
            }
            case "test-bubble":
                // A clickable long-running activity, which shows in the side bubble when something outranks it.
                Post(new Activity("test-bubble", ActivityKind.Progress, 30, new ProgressPayload("", "Test", 0.4), In(30))
                {
                    Click = () => _testBubbleClicks++,
                });
                return "ok";
            case "test-action-state":
                return $"clicks={_testActionClicks} bubbleClicks={_testBubbleClicks} shown={(_activities.Get("test-action") is { } t ? $"yes expires={(t.ExpiresAt is null ? "held" : "timed")}" : "no")}";
            case "sound":
                NotchSound.Play(arg.Trim() == "alert" ? Shammy.Core.Audio.PingKind.Alert : Shammy.Core.Audio.PingKind.Notification);
                return "ok";
            case "startup-state":
                return $"asked={_settings.StartupAsked} enabled={_settings.StartWithWindows} card={_startupAnswer}";
            case "edge-flash":
                _notch.FlashEdge((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(arg.Trim()));
                return "ok";
            case "update-state":
                return $"installed={_updates.IsInstalled} version={_updates.CurrentVersion} ready={_updates.ReadyVersion ?? "-"} last={_updates.LastResult}";
            case "update-check": return Task.Run(_updates.CheckAsync).GetAwaiter().GetResult();
            // Starts a check and answers at once, so "update-state" can watch it.
            case "update-check-bg": _ = _updates.CheckAsync(); return "started";
            case "update-restart":
                if (_updates.ReadyVersion is null) return "nothing ready";
                _ui.BeginInvoke(RestartForUpdate);
                return "restarting";
            case "diag": return Motion.Animator.Diagnostics;
            case "unmute":
                _settings.MutedNotificationApps.Clear();
                _settings.Save();
                return "ok";
            // "notifications list": the rows the Alerts view shows right now (text hidden while private).
            case "notifications" when arg.Trim() == "list":
                return $"presenting={PrivacyState.Presenting} rows={_notifications.History.Count} {_notch.ExpandedView.DescribeAlertRows()}";
            // "notify-test App|Title|Body[|appId]": a made-up toast through the real path (mute filter, history, pop-up, privacy).
            case "notify-test":
            {
                var f = arg.Split('|');
                _notifications.InjectForTest(new ShammyNotification
                {
                    Id = (uint)Random.Shared.Next(1 << 30, int.MaxValue),
                    AppId = f.Length > 3 ? f[3].Trim() : "",
                    AppName = f[0].Trim().Length > 0 ? f[0].Trim() : "Test",
                    Title = f.Length > 1 ? f[1].Trim() : "Test",
                    Body = f.Length > 2 ? f[2].Trim() : "",
                    Time = DateTimeOffset.Now,
                });
                return "ok";
            }
            case "notifications":
                return $"available={_notifications.IsAvailable} problem={_notifications.Problem ?? "-"} history={_notifications.History.Count} unread={_notifications.UnreadCount} " +
                       $"muted={_settings.MutedNotificationApps.Count} presenting={PrivacyState.Presenting} " +
                       $"shown={(_activities.Get("notification") is { Payload: NotificationPayload p } ? $"{p.Notification.Title} | {p.Notification.Body}" : "-")}";
            case "state":
                return $"mode={_notch.Mode} activity={_activities.Current?.Id ?? "-"} secondary={_activities.Secondary?.Id ?? "-"} " +
                       $"whisper={(_whisper.IsReady ? "ready:" + _whisper.ActiveModel.Replace(' ', '_') : _whisper.IsDownloading ? $"downloading {_whisper.DownloadProgress:P0}" : _whisper.Failure ?? "loading")} " +
                       $"keys={_keys.IsInstalled} media={(_media.HasSession ? _media.Title : "-")} playing={_media.IsPlaying} height={_notch.Height} " +
                       $"fullscreen={_notch.IsFullscreen}";
            case "expand":
                _notch.ExpandTo(arg.Contains("shelf"), hold: arg.Contains("hold"));
                if (arg.Contains("alerts")) _notch.ExpandedView.ShowAlerts();
                return "ok";
            case "viz":
                return $"running={_visualizer.IsRunning} levels=" +
                       string.Join(",", Enumerable.Range(0, AudioVisualizer.Bands).Select(i => _visualizer.Level(i).ToString("0.00", CultureInfo.InvariantCulture)));
            case "shelf-add": return $"added {_shelf.Add(new[] { arg })}";
            case "collapse": _notch.Collapse(); return "ok";
            // "views": every card's id; "view <id>": the open island on that card, as its tool or chip would.
            case "views": return _notch.ExpandedView.CardIdsForTest();
            case "repaint": _notch.RepaintAll(); return "ok";
            // "island-part waves|rim|chrome on|off": hide one part of the island's drawing, to find which draws a glitch.
            case "island-part": return _notch.SetPartForTest(arg);
            case "view": return _notch.OpenView(arg.Trim(), hold: true) ? "ok" : "error: no such view";
            case "type": _search.OpenTyping(); return "ok";
            case "search": _ = _search.SearchTextAsync(arg); return "ok";
            case "browser-html":
                // Test-only (the pipe is off unless SHAMMY_TEST_PIPE=1): load raw HTML into the results card.
                _ = _notch.ResultView.LoadTestHtmlAsync(arg);
                _search.ShowCardForTest();
                return "ok";
            case "fullpage":
                _ = SaveFullPageAsync();
                return "started";
            case "lens": _ = _search.SearchImageAsync(File.ReadAllBytes(arg)); return "ok";
            case "voice-language":
                _whisper.Language = arg;
                _whisper.ResetProcessors();
                return "ok";
            case "transcribe":
            {
                // Runs a WAV file through the same speech gate + Whisper path the mic uses.
                using var reader = new NAudio.Wave.AudioFileReader(arg);
                NAudio.Wave.ISampleProvider mono = reader.WaveFormat.Channels == 1
                    ? reader
                    : new NAudio.Wave.SampleProviders.StereoToMonoSampleProvider(reader);
                var resampled = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(mono, MicRecorder.SampleRate);
                var samples = new List<float>();
                var buf = new float[4096];
                int n;
                while ((n = resampled.Read(buf, 0, buf.Length)) > 0) samples.AddRange(buf.Take(n));
                var arr = samples.ToArray();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                // Off the UI thread's sync context, otherwise blocking here would deadlock the awaits inside.
                var text = Task.Run(() => _whisper.TranscribeAsync(arr)).GetAwaiter().GetResult();
                return $"speech={MicRecorder.HasSpeech(arr)} ms={sw.ElapsedMilliseconds} text={text}";
            }
            case "voice-route": return _search.RouteForTest(arg);
            case "voice-plan": return _search.PlanFor(arg);
            case "voice-state": return _search.LiveState;
            case "home-state": return _notch.ExpandedView.DescribeHomeTop();
            case "listen-start": _search.ListenUntilSilence(); return "ok";
            case "listen-state": return _search.SilenceState;
            // "voice-hear <words>": as if a held talk key brought these words back; "voice-live <words>": the listening view.
            case "voice-hear": _search.HearForTest(arg); return "ok";
            case "voice-live": _search.ShowLiveForTest(arg); return "ok";
            case "choice-pick": return int.TryParse(arg.Trim(), out var pick) ? _search.PickForTest(pick) : "error: choice-pick <0-based index>";
            // "circle-text <words>": a silent circle around these words (no screen), up to the moment or the search;
            // then "answer-state". "circle-act <label>": a moment's action or send icon by its label or tooltip, as a click.
            // "circle-expand": the moment's top, as a click (the full results).
            case "circle-text": _ = _search.CircleTextForTest(arg); return "ok";
            case "circle-act":
            {
                var run = _notch.AnswerView.RunMomentForTest(arg.Trim());
                return run.IsCompleted ? run.Result : "started";
            }
            case "circle-expand": _search.ExpandMomentForTest(); return "ok";
            // "circle-freeze" then "circle-draw x,y x,y …|seconds" (fractions of the screen): the real freeze and circle, no mouse.
            case "circle-freeze": return _search.FreezeForTest();
            case "circle-draw": return _search.DrawForTest(arg);
            case "circle-read": return "[" + Search.SearchCoordinator.LastReadForTest + "]";
            case "cancel": _search.Cancel(); return "ok";
            // "hold" / "release" = the look key (freeze); "hold talk" / "release talk" = Right Alt.
            case "hold": _search.OnKeyDown(arg.Trim() == "talk" ? HotKey.Talk : HotKey.Look); return "ok";
            case "release": _search.OnKeyUp(arg.Trim() == "talk" ? HotKey.Talk : HotKey.Look); return "ok";
            case "hud":
            {
                var a = arg.Split(' ');
                PostHud(a[0] == "brightness" ? HudKind.Brightness : HudKind.Volume, Num(a[1]), a.Length > 2 && a[2] == "muted");
                return "ok";
            }
            case "charging": Post(new Activity("battery", ActivityKind.Charging, PriorityCharging, new BatteryPayload((int)Num(arg), true), In(3.2))); return "ok";
            case "lowbattery": Post(new Activity("battery", ActivityKind.LowBattery, PriorityBatteryLow, new BatteryPayload((int)Num(arg), false), In(5))); return "ok";
            case "bluetooth":
                // "bluetooth Airdopes 311 Pro|80" previews the pill with a battery level.
                var btParts = arg.Split('|', 2);
                Post(new Activity("bluetooth", ActivityKind.Bluetooth, PriorityBluetooth, new BluetoothPayload(btParts[0].Trim(), true, true,
                    btParts.Length > 1 && int.TryParse(btParts[1], out var btLevel) ? btLevel : null), In(3.5)));
                return "ok";
            case "downloading":
            {
                // "downloading name" (12.4 MB so far) or "downloading name|bytes|total" (shows a percent).
                var d = arg.Split('|');
                long bytes = d.Length > 1 && long.TryParse(d[1], out var soFar) ? soFar : 12_400_000;
                long? total = d.Length > 2 && long.TryParse(d[2], out var size) ? size : null;
                Post(new Activity("download", ActivityKind.Downloading, PriorityDownloading, new DownloadPayload(d[0].Trim(), null, bytes, 1, total)));
                return "ok";
            }
            case "downloaded":
            {
                // "downloaded <path>": with a real file, clicking the pill opens the download card.
                _activities.Remove("download");
                var path = arg.Trim().Trim('"');
                bool file = File.Exists(path);
                Post(new Activity("download-done", ActivityKind.DownloadComplete, PriorityDownloadDone,
                    new DownloadPayload(file ? Path.GetFileName(path) : path, file ? path : null, 0, 1), In(4))
                {
                    Click = file ? () => ShowDownloadCard(path) : null,
                });
                return "ok";
            }
            case "download-card":
                if (!File.Exists(arg.Trim().Trim('"'))) return "error: usage download-card <existing file>";
                ShowDownloadCard(arg.Trim().Trim('"'));
                return "ok";
            case "message": Post(new Activity("message", ActivityKind.Message, PriorityMessage, new MessagePayload("", arg), In(3))); return "ok";
            case "clear":
                foreach (var a in _activities.All.Where(a => a.Id != "media").ToList()) _activities.Remove(a.Id);
                return "ok";
            case "fullscreen": _notch.SetFullscreen(arg == "on"); return "ok";
            case "volume": _volume?.Step(arg == "up" ? 1 : arg == "down" ? -1 : 0); return "ok";
            // "media-demo Title|Artist|cover.png": a made-up song for screenshots; real sessions are ignored until restart.
            case "media-demo":
            {
                var m = arg.Split('|');
                System.Windows.Media.Imaging.BitmapImage? cover = null;
                if (m.Length > 2)
                {
                    cover = new System.Windows.Media.Imaging.BitmapImage();
                    cover.BeginInit();
                    cover.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    cover.UriSource = new Uri(m[2]);
                    cover.EndInit();
                    cover.Freeze();
                }
                _media.ShowDemoForTest(m[0], m.Length > 1 ? m[1] : "", cover);
                return "ok";
            }
            // "lyric-demo some words": a made-up lyric line under the record; with nothing, the real source again (after a restart).
            case "lyric-demo":
            {
                var words = arg.Trim();
                Modules.HostLinks.CurrentLyric = () => words;
                return words.Length > 0 ? "ok" : "ok: none";
            }
            case "media":
                if (arg == "toggle") _media.TogglePlayPause();
                else if (arg == "next") _media.Next();
                return "ok";
            // ---- v2 resting notch, peek, gestures and keyboard pills ----
            // "v2-idle": pet mood, eyes and glance state; "v2-idle bounce" makes the pet hop.
            case "v2-idle":
                if (arg.Trim() == "bounce") _notch.BounceEyes();
                return _notch.DescribeIdle();
            // "v2-hover x,y,ms;x,y,ms;..." runs pointer samples through the hover rule (dry run, nothing opens).
            case "v2-hover": return DryRunHover(arg);
            // "pet-mood music" forces a mood (idle|music|notify|timer|listen|think|meeting|tired|sleepy|happy); "pet-mood auto" undoes it.
            case "pet-mood": return _notch.SetPetMood(arg);
            // "pet-purr": the cat purrs as if the pointer had rested on it.
            case "pet-purr": return _notch.PetPurr();
            // "tuck" slides the notch away as its tab would, "tuck back" brings it back, "tuck state" says where it is.
            case "tuck": return _notch.TuckForTest(arg);
            // "pet-greet": as if the PC had just been unlocked.
            case "pet-greet": return _notch.GreetBack();
            // "notify-dismiss": the message on screen is sent away (the next in the pile comes up).
            case "notify-dismiss":
                DismissNotification();
                return $"under={_underNotification.Count} {_notch.NotificationView.DescribeSheet()}";
            // "notify-swipe 120 [speed]": drag the message that far sideways and let go.
            case "notify-swipe":
            {
                var sw = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (sw.Length == 0 || !double.TryParse(sw[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double dx)) return "error: usage notify-swipe <pixels> [pixels per second]";
                double speed = sw.Length > 1 && double.TryParse(sw[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
                return _notch.NotificationView.TestSwipe(dx, speed);
            }
            // "notify-state": the pile under the message, the card's place and the cards peeking out.
            case "notify-state":
                return $"under={_underNotification.Count} {_notch.NotificationView.DescribeSheet()} {_notch.DescribeStack()}";
            case "v2-peek": return _notch.ForcePeek(true);
            case "v2-rest": return _notch.ForcePeek(false);
            // "v2-gesture next|prev|volup|voldown [dry]".
            case "v2-gesture":
            {
                var g = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return g.Length == 0 ? "error: usage v2-gesture next|prev|volup|voldown [dry]"
                    : _notch.SimulateGesture(g[0].ToLowerInvariant(), g.Contains("dry", StringComparer.OrdinalIgnoreCase));
            }
            case "capslock-test":
                if (_keyboardPills is null) return "error: keyboard pills unavailable";
                _keyboardPills.ShowCaps(arg.Trim() != "off");
                return "ok";
            case "lang-test":
                if (_keyboardPills is null) return "error: keyboard pills unavailable";
                _keyboardPills.ShowLanguage(KeyboardIndicators.CurrentLabel(), arg.Trim().Length > 0 ? arg.Trim() : "हिन्दी");
                return "ok";
            case "quit": Application.Current.Shutdown(); return "bye";
            case "modules": return string.Join(", ", _disposables.OfType<Modules.IShammyModule>().Select(m => m.Name));
            default:
                return _modules.PipeCommands.TryGetValue(parts[0], out var handler) ? handler(arg) : "error: unknown command";
        }
    }
}
