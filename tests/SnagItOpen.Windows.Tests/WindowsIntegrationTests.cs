using System.Runtime.InteropServices;
using System.Windows.Input;
using SnagItOpen.Core;
using SnagItOpen.Core.Geometry;
using SnagItOpen.Windows.Capture;
using SnagItOpen.Windows.Clipboard;
using SnagItOpen.Windows.Shell;

namespace SnagItOpen.Windows.Tests;

[Collection("Process resources")]
public class CaptureBufferTests
{
    [Fact]
    public void Bottom_up_buffer_with_padded_stride_is_flipped_and_opaque()
    {
        // 2×2 BGRX, stride 12 (4 bytes padding). Bottom-up: first row in memory is the image's bottom row.
        const int w = 2, h = 2, stride = 12;
        var src = new byte[stride * h];
        void Put(int memRow, int x, byte b, byte g, byte r) { int o = memRow * stride + x * 4; src[o] = b; src[o + 1] = g; src[o + 2] = r; src[o + 3] = 0; }
        Put(0, 0, 1, 2, 3);   // bottom-left
        Put(0, 1, 4, 5, 6);   // bottom-right
        Put(1, 0, 7, 8, 9);   // top-left
        Put(1, 1, 10, 11, 12);// top-right
        var px = CaptureBuffers.FromBgrx(src, w, h, stride, bottomUp: true);
        Assert.Equal(0xFF090807u, px[0]);
        Assert.Equal(0xFF0C0B0Au, px[1]);
        Assert.Equal(0xFF030201u, px[2]);
        Assert.Equal(0xFF060504u, px[3]);
    }

    [Fact]
    public void Undersized_buffer_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => CaptureBuffers.FromBgrx(new byte[10], 2, 2, 8, false));
    }

    [Fact]
    public void Monitor_gaps_become_fully_transparent()
    {
        // Union 0..30 wide, monitors at [0,10) and [20,30), gap [10,20).
        var bounds = new PixelRect(0, 0, 30, 1);
        var px = Enumerable.Repeat(0x12345678u, 30).ToArray();
        CaptureBuffers.ApplyMonitorCoverage(px, bounds, [new PixelRect(0, 0, 10, 1), new PixelRect(20, 0, 10, 1)]);
        Assert.All(px[..10], p => Assert.Equal(0xFF345678u, p));
        Assert.All(px[10..20], p => Assert.Equal(0u, p));
        Assert.All(px[20..], p => Assert.Equal(0xFF345678u, p));
    }

    [Fact]
    public void Frame_crop_uses_physical_coordinates_with_negative_origin()
    {
        var bounds = new PixelRect(-4, -2, 4, 2);
        var px = Enumerable.Range(0, 8).Select(i => (uint)i).ToArray();
        var f = new CapturedFrame(bounds, px).Crop(new PixelRect(-3, -1, 2, 1));
        Assert.Equal(new PixelRect(-3, -1, 2, 1), f.Bounds);
        Assert.Equal([5u, 6u], f.Pixels);
    }

    [Fact]
    public void Crop_outside_frame_throws()
    {
        var f = new CapturedFrame(new PixelRect(0, 0, 2, 2), new uint[4]);
        Assert.Throws<ArgumentOutOfRangeException>(() => f.Crop(new PixelRect(10, 10, 2, 2)));
    }

    /// <summary>Real desktop integration: repeated GDI captures must not leak GDI objects.</summary>
    [Fact]
    public void Hundred_small_captures_do_not_leak_gdi_objects()
    {
        using var ms = new MonitorService();
        var monitors = ms.GetMonitors();
        Assert.NotEmpty(monitors);
        var m = monitors[0].Bounds;
        var area = new PixelRect(m.X, m.Y, Math.Min(32, m.Width), Math.Min(32, m.Height));
        // Warm up both paths: the first cursor draw lets the system cache cursor resources once.
        for (int i = 0; i < 4; i++) GdiScreenCapture.Capture(area, monitors, includeCursor: i % 2 == 0);
        int baseline = GdiScreenCapture.GdiObjectCount();
        void Batch()
        {
            for (int i = 0; i < 100; i++)
            {
                var f = GdiScreenCapture.Capture(area, monitors, includeCursor: i % 2 == 0);
                Assert.Equal(area, f.Bounds);
                Assert.All(f.Pixels, p => Assert.Equal(0xFFu, p >> 24));
            }
        }
        Batch();
        int afterFirst = GdiScreenCapture.GdiObjectCount();
        Batch();
        int afterSecond = GdiScreenCapture.GdiObjectCount();
        // A per-capture leak would grow by >= 50 per batch. Only growth indicates a leak; counts may
        // drop when unrelated process resources are released. Allow small one-time caching noise.
        Assert.True(afterFirst - baseline <= 10, $"GDI objects grew by {afterFirst - baseline} over 100 captures.");
        Assert.True(afterSecond - afterFirst <= 5, $"GDI objects grew by {afterSecond - afterFirst} over the second 100 captures.");
    }
}

/// <summary>Tests that measure process-wide resources must not run alongside WPF rendering tests.</summary>
[CollectionDefinition("Process resources", DisableParallelization = true)]
public sealed class ProcessResourceCollection;

public class WindowFilterTests
{
    private static readonly PixelRect Desktop = new(0, 0, 1000, 800);

    private static WindowInfo W(string title, PixelRect b, uint pid = 1, bool visible = true, bool min = false, bool cloaked = false, bool tool = false, string cls = "App") =>
        new(new IntPtr(title.GetHashCode()), title, cls, b, pid, visible, min, cloaked, tool);

    [Fact]
    public void Filters_invisible_minimized_cloaked_tool_own_and_shell_windows()
    {
        var list = new[]
        {
            W("ok", new PixelRect(10, 10, 100, 100)),
            W("hidden", new PixelRect(10, 10, 100, 100), visible: false),
            W("min", new PixelRect(10, 10, 100, 100), min: true),
            W("cloaked", new PixelRect(10, 10, 100, 100), cloaked: true),
            W("tool", new PixelRect(10, 10, 100, 100), tool: true),
            W("mine", new PixelRect(10, 10, 100, 100), pid: 42),
            W("desktop", Desktop, cls: "Progman"),
            W("", new PixelRect(10, 10, 100, 100)),
            W("offscreen", new PixelRect(5000, 5000, 100, 100)),
        };
        var e = WindowFilter.Eligible(list, ownProcessId: 42, Desktop);
        Assert.Equal(["ok"], e.Select(x => x.Title));
    }

    [Fact]
    public void Bounds_are_clipped_to_desktop()
    {
        var e = WindowFilter.Eligible([W("a", new PixelRect(-50, -50, 200, 200))], 0, Desktop);
        Assert.Equal(new PixelRect(0, 0, 150, 150), e[0].Bounds);
    }

    [Fact]
    public void At_returns_topmost_in_z_order()
    {
        var top = W("top", new PixelRect(0, 0, 100, 100));
        var below = W("below", new PixelRect(0, 0, 500, 500));
        var e = WindowFilter.Eligible([top, below], 0, Desktop);
        Assert.Equal("top", WindowFilter.At(e, new PixelPoint(50, 50))!.Title);
        Assert.Equal("below", WindowFilter.At(e, new PixelPoint(300, 300))!.Title);
        Assert.Null(WindowFilter.At(e, new PixelPoint(900, 700)));
    }
}

public class ClipboardRetryTests
{
    private static Task NoDelay(TimeSpan _, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }

    [Fact]
    public async Task Busy_four_times_then_success()
    {
        int calls = 0;
        var r = await ClipboardRetry.RunAsync(() => { if (++calls < 5) throw new COMException("busy"); return 7; }, delay: NoDelay);
        Assert.True(r.IsSuccess);
        Assert.Equal(7, r.Value);
        Assert.Equal(5, calls);
    }

    [Fact]
    public async Task Always_busy_fails_after_five_attempts()
    {
        int calls = 0;
        var r = await ClipboardRetry.RunAsync<int>(() => { calls++; throw new COMException("busy"); }, delay: NoDelay);
        Assert.Equal(ErrorCode.ClipboardBusy, r.Error);
        Assert.Equal(5, calls);
    }

    [Fact]
    public async Task Total_wait_is_bounded_to_one_second()
    {
        var waited = TimeSpan.Zero;
        await ClipboardRetry.RunAsync<int>(() => throw new COMException("busy"),
            delay: (t, _) => { waited += t; return Task.CompletedTask; });
        Assert.True(waited <= TimeSpan.FromSeconds(1), $"waited {waited}");
    }

    [Fact]
    public async Task Cancellation_stops_retrying()
    {
        using var cts = new CancellationTokenSource();
        int calls = 0;
        var r = await ClipboardRetry.RunAsync<int>(() => { calls++; cts.Cancel(); throw new COMException("busy"); }, cts.Token, delay: NoDelay);
        Assert.True(r.IsCanceled);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Non_busy_exceptions_propagate()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ClipboardRetry.RunAsync<int>(() => throw new InvalidOperationException(), delay: NoDelay));
    }

    private sealed class FakeClipboard : IClipboardAdapter
    {
        public int Busy;
        public int Sets;
        public void SetImage(System.Windows.Media.Imaging.BitmapSource b, byte[] png) { if (Busy-- > 0) throw new COMException("busy"); Sets++; }
        public ClipboardContent Read() => new(null, [], true);
    }

    [Fact]
    public async Task Service_reads_nonimage_as_no_image()
    {
        var svc = new ClipboardImageService(new FakeClipboard());
        var r = await svc.ReadAsync();
        Assert.True(r.IsSuccess);
        Assert.False(r.Value!.HasImage);
    }
}

public class HotkeyLifecycleTests
{
    private sealed class FakeRegistrar : IHotkeyRegistrar, IDisposable
    {
        public readonly HashSet<(uint, uint)> Taken = [];
        public readonly Dictionary<int, (uint Mods, uint Vk)> Registered = [];
        public bool Disposed;
        public event Action<int>? HotkeyPressed;
        public bool Register(int id, uint modifiers, uint vk)
        {
            if (Taken.Contains((modifiers, vk)) || Registered.Values.Contains((modifiers, vk))) return false;
            Registered[id] = (modifiers, vk);
            return true;
        }
        public void Unregister(int id) => Registered.Remove(id);
        public void Press(int id) => HotkeyPressed?.Invoke(id);
        public void Dispose() => Disposed = true;
    }

    private static HotkeyGesture G(string s) { Assert.True(HotkeyGesture.TryParse(s, out var g), s); return g; }

    [Theory]
    [InlineData("Ctrl+Shift+1", ModifierKeys.Control | ModifierKeys.Shift, Key.D1)]
    [InlineData("alt+F5", ModifierKeys.Alt, Key.F5)]
    [InlineData("PrintScreen", ModifierKeys.None, Key.Snapshot)]
    public void Parses_gestures(string text, ModifierKeys mods, Key key)
    {
        var g = G(text);
        Assert.Equal(mods, g.Modifiers);
        Assert.Equal(key, g.Key);
        Assert.True(HotkeyGesture.TryParse(g.ToString(), out var again));
        Assert.Equal(g, again);
    }

    [Theory]
    [InlineData("")]
    [InlineData("A")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl+Nonsense")]
    public void Rejects_invalid_gestures(string text) => Assert.False(HotkeyGesture.TryParse(text, out _));

    [Fact]
    public void Conflict_keeps_previous_binding()
    {
        var reg = new FakeRegistrar();
        using var svc = new GlobalHotkeyService(reg);
        Assert.True(svc.Bind("region", G("Ctrl+Shift+1")).IsSuccess);
        var taken = G("Ctrl+Shift+9");
        reg.Taken.Add((taken.Win32Modifiers, taken.VirtualKey));
        var r = svc.Bind("region", taken);
        Assert.Equal(ErrorCode.HotkeyUnavailable, r.Error);
        Assert.Equal(G("Ctrl+Shift+1"), svc.Bindings["region"]);
        Assert.Single(reg.Registered);
    }

    [Fact]
    public void Rebind_registers_new_before_releasing_old()
    {
        var reg = new FakeRegistrar();
        using var svc = new GlobalHotkeyService(reg);
        svc.Bind("region", G("Ctrl+Shift+1"));
        Assert.True(svc.Bind("region", G("Ctrl+Shift+2")).IsSuccess);
        Assert.Single(reg.Registered);
        Assert.Equal(G("Ctrl+Shift+2"), svc.Bindings["region"]);
    }

    [Fact]
    public void Same_gesture_for_two_names_is_rejected()
    {
        var reg = new FakeRegistrar();
        using var svc = new GlobalHotkeyService(reg);
        svc.Bind("a", G("Ctrl+Shift+1"));
        Assert.False(svc.Bind("b", G("Ctrl+Shift+1")).IsSuccess);
    }

    [Fact]
    public void Press_raises_named_event_and_dispose_unregisters_all()
    {
        var reg = new FakeRegistrar();
        var svc = new GlobalHotkeyService(reg);
        svc.Bind("region", G("Ctrl+Shift+1"));
        svc.Bind("window", G("Ctrl+Shift+2"));
        string? pressed = null;
        svc.Pressed += n => pressed = n;
        reg.Press(reg.Registered.Keys.Max());
        Assert.Equal("window", pressed);
        svc.Bind("window", null);
        Assert.Single(reg.Registered);
        svc.Dispose();
        Assert.Empty(reg.Registered);
        Assert.True(reg.Disposed);
    }
}
