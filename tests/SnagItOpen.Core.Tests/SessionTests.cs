using SnagItOpen.Core.Capture;
using SnagItOpen.Core.Stitching;

namespace SnagItOpen.Core.Tests;

public class ScrollingSessionTests
{
    /// <summary>Deterministic textured "page"; each row has distinct content.</summary>
    private static float[] Page(int width, int height, int seed = 7)
    {
        var rnd = new Random(seed);
        var d = new float[width * height];
        for (int i = 0; i < d.Length; i++) d[i] = (float)rnd.NextDouble();
        return d;
    }

    private static LumaImage Window(float[] page, int width, int top, int height)
    {
        var d = new float[width * height];
        Array.Copy(page, top * width, d, 0, width * height);
        return new LumaImage(width, height, d);
    }

    private sealed class FakeClock
    {
        public DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public Task Delay(TimeSpan t, CancellationToken ct) { ct.ThrowIfCancellationRequested(); Now += t; return Task.CompletedTask; }
    }

    [Fact]
    public void Guided_frames_with_known_overlap_are_accepted()
    {
        const int w = 64, h = 100;
        var page = Page(w, 400);
        var s = new ScrollingSession<LumaImage>(x => x);
        Assert.Equal(FrameVerdict.Accepted, s.Offer(Window(page, w, 0, h)));
        Assert.Equal(FrameVerdict.Accepted, s.Offer(Window(page, w, 60, h))); // overlap 40
        Assert.Equal(40, s.Frames[1].Overlap);
        Assert.Equal(160, s.OutputHeight);
    }

    [Fact]
    public void Identical_frame_is_unchanged_and_not_added()
    {
        var page = Page(64, 200);
        var s = new ScrollingSession<LumaImage>(x => x);
        s.Offer(Window(page, 64, 0, 100));
        Assert.Equal(FrameVerdict.Unchanged, s.Offer(Window(page, 64, 0, 100)));
        Assert.Single(s.Frames);
    }

    [Fact]
    public void Unrelated_frame_is_pending_for_manual_correction()
    {
        var s = new ScrollingSession<LumaImage>(x => x);
        s.Offer(Window(Page(64, 200, 1), 64, 0, 100));
        var other = Window(Page(64, 200, 2), 64, 0, 100);
        Assert.Equal(FrameVerdict.LowConfidence, s.Offer(other));
        Assert.Same(other, s.Pending);
        Assert.Single(s.Frames);
        Assert.True(s.AcceptPending(0));
        Assert.Equal(2, s.Frames.Count);
        Assert.Equal(200, s.OutputHeight);
    }

    [Fact]
    public void Size_limit_stops_session()
    {
        var page = Page(64, 400);
        var s = new ScrollingSession<LumaImage>(x => x, new ScrollingOptions { MaxOutputHeight = 150 });
        s.Offer(Window(page, 64, 0, 100));
        Assert.Equal(FrameVerdict.SizeLimit, s.Offer(Window(page, 64, 60, 100)));
        Assert.Equal(ScrollStopReason.SizeLimit, s.StopReason);
    }

    [Fact]
    public async Task Automatic_mode_stops_at_page_end_and_never_scrolls_after()
    {
        const int w = 64, h = 100, step = 50, pageH = 300;
        var page = Page(w, pageH);
        int top = 0, scrolls = 0;
        var clock = new FakeClock();
        var s = new ScrollingSession<LumaImage>(x => x);
        bool Scroll() { scrolls++; top = Math.Min(top + step, pageH - h); return true; }
        var reason = await s.RunAutomaticAsync(_ => Task.FromResult(Window(page, w, top, h)), Scroll, clock.Delay, () => clock.Now, CancellationToken.None);
        Assert.Equal(ScrollStopReason.Unchanged, reason);
        Assert.Equal(pageH, s.OutputHeight);
        int after = scrolls;
        await Task.Yield();
        Assert.Equal(after, scrolls);
    }

    [Fact]
    public async Task Automatic_mode_honors_frame_limit()
    {
        var page = Page(64, 2000);
        int top = 0;
        var clock = new FakeClock();
        var s = new ScrollingSession<LumaImage>(x => x, new ScrollingOptions { MaxFrames = 3 });
        var r = await s.RunAutomaticAsync(_ => Task.FromResult(Window(page, 64, top, 100)), () => { top += 50; return true; }, clock.Delay, () => clock.Now, CancellationToken.None);
        Assert.Equal(ScrollStopReason.FrameLimit, r);
        Assert.Equal(3, s.Frames.Count);
    }

    [Fact]
    public async Task Automatic_mode_stops_when_target_lost()
    {
        var page = Page(64, 400);
        var clock = new FakeClock();
        var s = new ScrollingSession<LumaImage>(x => x);
        var r = await s.RunAutomaticAsync(_ => Task.FromResult(Window(page, 64, 0, 100)), () => false, clock.Delay, () => clock.Now, CancellationToken.None);
        Assert.Equal(ScrollStopReason.TargetLost, r);
        Assert.Single(s.Frames);
    }

    [Fact]
    public async Task Never_stabilizing_content_stops_as_unstable()
    {
        int n = 0;
        var clock = new FakeClock();
        var s = new ScrollingSession<LumaImage>(x => x);
        var r = await s.RunAutomaticAsync(_ => Task.FromResult(Window(Page(64, 100, n++), 64, 0, 100)), () => true, clock.Delay, () => clock.Now, CancellationToken.None);
        Assert.Equal(ScrollStopReason.Unstable, r);
    }

    [Fact]
    public async Task Cancellation_stops_session()
    {
        using var cts = new CancellationTokenSource();
        var page = Page(64, 400);
        var clock = new FakeClock();
        var s = new ScrollingSession<LumaImage>(x => x);
        var r = await s.RunAutomaticAsync(_ => Task.FromResult(Window(page, 64, 0, 100)), () => { cts.Cancel(); return true; }, clock.Delay, () => clock.Now, cts.Token);
        Assert.Equal(ScrollStopReason.Canceled, r);
    }
}

public class IntervalCaptureTests
{
    [Fact]
    public async Task Captures_up_to_max_frames_sequentially()
    {
        var delays = new List<TimeSpan>();
        var s = new IntervalCaptureSession<int>(TimeSpan.FromSeconds(5), 5);
        int n = 0;
        var frames = await s.RunAsync(async ct => { await Task.Yield(); return n++; }, (t, _) => { delays.Add(t); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal([0, 1, 2, 3, 4], frames);
        Assert.Equal(4, delays.Count);
        Assert.Equal(1, s.MaxConcurrentCaptures);
        Assert.False(s.IsRunning);
    }

    [Fact]
    public async Task No_capture_after_stop()
    {
        using var cts = new CancellationTokenSource();
        var s = new IntervalCaptureSession<int>(TimeSpan.FromSeconds(1), 20);
        int n = 0;
        var frames = await s.RunAsync(_ => { n++; if (n == 2) cts.Cancel(); return Task.FromResult(n); },
            (_, ct) => ct.IsCancellationRequested ? Task.FromCanceled(ct) : Task.CompletedTask, cts.Token);
        Assert.Equal(2, frames.Count);
        Assert.Equal(2, n);
    }

    [Fact]
    public async Task External_stop_condition_ends_session()
    {
        var s = new IntervalCaptureSession<int>(TimeSpan.FromSeconds(1), 20);
        int n = 0;
        var frames = await s.RunAsync(_ => Task.FromResult(n++), (_, _) => Task.CompletedTask, CancellationToken.None, () => n >= 3);
        Assert.Equal(3, frames.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    public void Interval_out_of_range_rejected(int sec) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new IntervalCaptureSession<int>(TimeSpan.FromSeconds(sec)));
}
