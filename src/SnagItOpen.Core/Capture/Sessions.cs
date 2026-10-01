using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Stitching;

namespace SnagItOpen.Core.Capture;

public enum ScrollStopReason
{
    None, UserStopped, Canceled, Unchanged, LowConfidence, Unstable, FrameLimit, SizeLimit, TimeLimit, TargetLost, CaptureFailed,
}

/// <summary>Bounded defaults for scrolling capture (spec T36). All are configurable.</summary>
public sealed record ScrollingOptions
{
    public int MaxFrames { get; init; } = 80;
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(3);
    public int MaxOutputHeight { get; init; } = Limits.MaxDimension;
    public long MaxOutputPixels { get; init; } = Limits.MaxExportPixels;
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(100);
    public int StableComparisons { get; init; } = 3;
    public TimeSpan StabilizeTimeout { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Initial wheel notches per automatic step; adapted after each frame so frames overlap by about 40 %.</summary>
    public int WheelNotches { get; init; } = 3;
    public int MaxWheelNotches { get; init; } = 15;
    public OverlapOptions Overlap { get; init; } = new();
    /// <summary>Detect a sticky header/footer on the second frame when none is configured.</summary>
    public bool DetectStickyBands { get; init; } = true;
}

/// <summary>An accepted frame and the rows of it that repeat the previous frame.</summary>
public sealed record ScrollFrame<TFrame>(TFrame Frame, int Overlap, OverlapSuggestion? Match);

/// <summary>Outcome of offering one frame to the session.</summary>
public enum FrameVerdict { Accepted, Unchanged, LowConfidence, SizeLimit }

/// <summary>
/// Scrolling capture state machine. Frames are offered in order; each is matched against the last
/// accepted frame. Confident matches are accepted with their overlap; identical frames mean the end
/// was probably reached; uncertain matches stop and keep the frame as <see cref="Pending"/> for manual
/// seam correction. No giant bitmap is built: accepted frames stay separate (later joined as seams).
/// All environment access is injected so the loop is deterministic under test.
/// </summary>
public sealed class ScrollingSession<TFrame> where TFrame : class
{
    private readonly Func<TFrame, LumaImage> _luma;
    private readonly List<ScrollFrame<TFrame>> _frames = [];
    private LumaImage? _lastLuma;
    private long _height;
    private OverlapOptions _overlap;

    public ScrollingSession(Func<TFrame, LumaImage> luma, ScrollingOptions? options = null)
    {
        _luma = luma;
        Options = options ?? new ScrollingOptions();
        _overlap = Options.Overlap;
        NextNotches = Math.Clamp(Options.WheelNotches, 1, Options.MaxWheelNotches);
    }

    /// <summary>Sticky rows excluded from matching and kept once when stitching (configured or detected).</summary>
    public int HeaderRows => _overlap.HeaderRows;
    public int FooterRows => _overlap.FooterRows;
    /// <summary>Wheel notches for the next automatic step.</summary>
    public int NextNotches { get; private set; }

    public ScrollingOptions Options { get; }
    public IReadOnlyList<ScrollFrame<TFrame>> Frames => _frames;
    /// <summary>Frame that could not be matched confidently (kept for manual correction).</summary>
    public TFrame? Pending { get; private set; }
    public OverlapSuggestion? PendingMatch { get; private set; }
    public ScrollStopReason StopReason { get; private set; }
    public bool IsStopped => StopReason != ScrollStopReason.None;
    public long OutputHeight => _height;
    public event Action? Changed;

    /// <summary>Offers the next frame (guided mode calls this directly after the user scrolls).</summary>
    public FrameVerdict Offer(TFrame frame)
    {
        var luma = _luma(frame);
        if (_lastLuma is null)
        {
            if (!Fits(luma.Width, luma.Height)) { Stop(ScrollStopReason.SizeLimit); return FrameVerdict.SizeLimit; }
            Accept(frame, luma, 0, null);
            return FrameVerdict.Accepted;
        }
        if (OverlapMatcher.AreSame(_lastLuma, luma)) return FrameVerdict.Unchanged;
        if (_frames.Count == 1 && Options.DetectStickyBands && _overlap.HeaderRows == 0 && _overlap.FooterRows == 0)
        {
            var (header, footer) = OverlapMatcher.StickyRows(_lastLuma, luma);
            if (header > 0 || footer > 0) _overlap = _overlap with { HeaderRows = header, FooterRows = footer };
        }
        var m = OverlapMatcher.FindVertical(_lastLuma, luma, _overlap);
        if (!m.IsConfident)
        {
            Pending = frame;
            PendingMatch = m;
            Changed?.Invoke();
            return FrameVerdict.LowConfidence;
        }
        long newHeight = _height + luma.Height - m.Overlap;
        if (!Fits(luma.Width, newHeight)) { Stop(ScrollStopReason.SizeLimit); return FrameVerdict.SizeLimit; }
        Accept(frame, luma, m.Overlap, m);
        return FrameVerdict.Accepted;
    }

    /// <summary>Accepts the pending frame with a user-chosen overlap (manual seam correction).</summary>
    public bool AcceptPending(int overlap)
    {
        if (Pending is not { } f || _lastLuma is null) return false;
        var luma = _luma(f);
        if (overlap < 0 || overlap >= luma.Height) return false;
        if (!Fits(luma.Width, _height + luma.Height - overlap)) return false;
        Accept(f, luma, overlap, PendingMatch);
        Pending = null;
        PendingMatch = null;
        if (StopReason == ScrollStopReason.LowConfidence) StopReason = ScrollStopReason.None;
        return true;
    }

    public void Stop(ScrollStopReason reason)
    {
        if (StopReason == ScrollStopReason.None) StopReason = reason;
        Changed?.Invoke();
    }

    /// <summary>Clears a stop so a guided session can continue after manual correction.</summary>
    public void Resume()
    {
        if (StopReason is ScrollStopReason.LowConfidence or ScrollStopReason.Unchanged or ScrollStopReason.Unstable or ScrollStopReason.UserStopped)
            StopReason = ScrollStopReason.None;
        Changed?.Invoke();
    }

    private bool Fits(long w, long h) => h <= Options.MaxOutputHeight && w * h <= Options.MaxOutputPixels && w <= Limits.MaxDimension;

    private void Accept(TFrame frame, LumaImage luma, int overlap, OverlapSuggestion? m)
    {
        _frames.Add(new ScrollFrame<TFrame>(frame, overlap, m));
        Pending = null; PendingMatch = null;
        _height = _frames.Count == 1 ? luma.Height : _height + luma.Height - overlap;
        _lastLuma = luma;
        // Adapt the scroll step: aim for ~60 % new content per frame (40 % overlap keeps matching reliable).
        int content = luma.Height - _overlap.HeaderRows - _overlap.FooterRows;
        if (_frames.Count > 1 && content > 0 && _lastNotches > 0)
        {
            // overlap is in full-frame rows (repeated content + header + footer): new rows = content - repeated content.
            int repeated = Math.Max(0, overlap - _overlap.HeaderRows - _overlap.FooterRows);
            double perNotch = Math.Max(1, content - repeated) / (double)_lastNotches;
            NextNotches = Math.Clamp((int)Math.Round(content * 0.6 / perNotch), 1, Options.MaxWheelNotches);
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Automatic mode: capture first frame, then repeatedly scroll, wait for the content to settle,
    /// and offer the frame, until a stop condition. Never scrolls after returning.
    /// </summary>
    private int _lastNotches;

    public async Task<ScrollStopReason> RunAutomaticAsync(
        Func<CancellationToken, Task<TFrame>> capture,
        Func<int, bool> scroll,
        Func<TimeSpan, CancellationToken, Task> delay,
        Func<DateTimeOffset> clock,
        CancellationToken ct)
    {
        var start = clock();
        try
        {
            if (_frames.Count == 0)
            {
                var first = await capture(ct).ConfigureAwait(false);
                if (Offer(first) != FrameVerdict.Accepted) return StopReason;
            }
            while (!IsStopped)
            {
                ct.ThrowIfCancellationRequested();
                if (_frames.Count >= Options.MaxFrames) { Stop(ScrollStopReason.FrameLimit); break; }
                if (clock() - start >= Options.MaxDuration) { Stop(ScrollStopReason.TimeLimit); break; }
                _lastNotches = NextNotches;
                if (!scroll(_lastNotches)) { Stop(ScrollStopReason.TargetLost); break; }

                var frame = await WaitStableAsync(capture, delay, clock, ct).ConfigureAwait(false);
                if (frame is null) { Stop(ScrollStopReason.Unstable); break; }
                var verdict = Offer(frame);
                if (verdict == FrameVerdict.LowConfidence && _lastNotches > 1)
                {
                    // Usually an overshoot (no shared rows). Scroll back half the step and try once more before asking.
                    int back = Math.Max(1, _lastNotches / 2);
                    if (!scroll(-back)) { Stop(ScrollStopReason.TargetLost); break; }
                    _lastNotches -= back; NextNotches = Math.Max(1, _lastNotches);
                    frame = await WaitStableAsync(capture, delay, clock, ct).ConfigureAwait(false);
                    if (frame is null) { Stop(ScrollStopReason.Unstable); break; }
                    verdict = Offer(frame);
                }
                switch (verdict)
                {
                    case FrameVerdict.Unchanged: Stop(ScrollStopReason.Unchanged); break;
                    case FrameVerdict.LowConfidence: Stop(ScrollStopReason.LowConfidence); break;
                    case FrameVerdict.SizeLimit: break; // already stopped
                }
            }
        }
        catch (OperationCanceledException) { Stop(ScrollStopReason.Canceled); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { Stop(ScrollStopReason.CaptureFailed); }
        return StopReason;
    }

    /// <summary>Polls until <see cref="ScrollingOptions.StableComparisons"/> consecutive frames match; null on timeout.</summary>
    private async Task<TFrame?> WaitStableAsync(Func<CancellationToken, Task<TFrame>> capture,
        Func<TimeSpan, CancellationToken, Task> delay, Func<DateTimeOffset> clock, CancellationToken ct)
    {
        var begin = clock();
        await delay(Options.PollInterval, ct).ConfigureAwait(false);
        var prev = await capture(ct).ConfigureAwait(false);
        var prevLuma = _luma(prev);
        int stable = 0;
        while (stable < Options.StableComparisons)
        {
            if (clock() - begin >= Options.StabilizeTimeout) return null;
            await delay(Options.PollInterval, ct).ConfigureAwait(false);
            var cur = await capture(ct).ConfigureAwait(false);
            var curLuma = _luma(cur);
            if (OverlapMatcher.AreSame(prevLuma, curLuma)) stable++;
            else stable = 0;
            prev = cur;
            prevLuma = curLuma;
        }
        return prev;
    }
}

/// <summary>
/// Explicitly started interval capture: one capture per interval, at most <see cref="MaxFrames"/>.
/// Captures are awaited sequentially, so a slow capture never overlaps the next one; the following
/// tick is scheduled from its completion. Nothing is captured after Stop/cancel.
/// </summary>
public sealed class IntervalCaptureSession<TFrame>
{
    public const int MinSeconds = 1, MaxSeconds = 60;
    private readonly List<TFrame> _frames = [];

    public IntervalCaptureSession(TimeSpan interval, int maxFrames = 20)
    {
        if (interval < TimeSpan.FromSeconds(MinSeconds) || interval > TimeSpan.FromSeconds(MaxSeconds))
            throw new ArgumentOutOfRangeException(nameof(interval), "Interval must be 1–60 seconds.");
        if (maxFrames is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(maxFrames));
        Interval = interval;
        MaxFrames = maxFrames;
    }

    public TimeSpan Interval { get; }
    public int MaxFrames { get; }
    public IReadOnlyList<TFrame> Frames => _frames;
    public bool IsRunning { get; private set; }
    public int CapturesInFlight { get; private set; }
    public int MaxConcurrentCaptures { get; private set; }
    public event Action<int, TimeSpan>? Tick;

    /// <summary>Runs until MaxFrames, cancellation, or <paramref name="shouldStop"/> (e.g. topology change).</summary>
    public async Task<IReadOnlyList<TFrame>> RunAsync(Func<CancellationToken, Task<TFrame>> capture,
        Func<TimeSpan, CancellationToken, Task> delay, CancellationToken ct, Func<bool>? shouldStop = null)
    {
        if (IsRunning) throw new InvalidOperationException("Session already running.");
        IsRunning = true;
        try
        {
            while (_frames.Count < MaxFrames)
            {
                if (ct.IsCancellationRequested || shouldStop?.Invoke() == true) break;
                Tick?.Invoke(_frames.Count, Interval);
                if (_frames.Count > 0)
                {
                    try { await delay(Interval, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
                if (ct.IsCancellationRequested || shouldStop?.Invoke() == true) break;
                CapturesInFlight++;
                MaxConcurrentCaptures = Math.Max(MaxConcurrentCaptures, CapturesInFlight);
                try { _frames.Add(await capture(ct).ConfigureAwait(false)); }
                catch (OperationCanceledException) { break; }
                finally { CapturesInFlight--; }
            }
        }
        finally { IsRunning = false; }
        return _frames;
    }
}
