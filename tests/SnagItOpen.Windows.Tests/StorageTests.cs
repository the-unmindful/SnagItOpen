using System.IO;
using System.Text;
using SnagItOpen.Core.Capture;
using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Geometry;
using SnagItOpen.Storage.Assets;
using SnagItOpen.Storage.History;
using SnagItOpen.Storage.Recovery;
using SnagItOpen.Storage.Settings;

namespace SnagItOpen.Windows.Tests;

/// <summary>Unique temporary folder deleted on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir() => Directory.CreateDirectory(Path);
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sio-storage-" + Guid.NewGuid().ToString("N"));
    public string Of(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch (IOException) { } }
}

public class SettingsTests
{
    [Fact]
    public void Roundtrip_and_sanitize_out_of_range_values()
    {
        using var t = new TempDir();
        var store = new SettingsStore(t.Of("settings.json"));
        store.Save(new AppSettings { JpegQuality = 500, CaptureDelaySeconds = 7, CloseToTray = true });
        var r = store.Load();
        Assert.Null(r.Warning);
        Assert.Equal(100, r.Value.JpegQuality);
        Assert.Equal(0, r.Value.CaptureDelaySeconds);
        Assert.True(r.Value.CloseToTray);
        Assert.Contains(r.Value.Hotkeys, h => h.Action == HotkeyActions.Region);
    }

    [Fact]
    public void Corrupt_file_falls_back_with_warning_and_backup()
    {
        using var t = new TempDir();
        var path = t.Of("settings.json");
        File.WriteAllText(path, "{ this is not json");
        var r = new SettingsStore(path).Load();
        Assert.NotNull(r.Warning);
        Assert.Equal(new AppSettings().JpegQuality, r.Value.JpegQuality);
        Assert.Contains(Directory.GetFiles(t.Path), f => f.EndsWith(".bak", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_hotkey_actions_are_dropped()
    {
        using var t = new TempDir();
        var path = t.Of("settings.json");
        new SettingsStore(path).Save(new AppSettings { Hotkeys = [new("Launch missiles", "Ctrl+M"), new(HotkeyActions.Region, "Ctrl+Alt+R")] });
        var s = new SettingsStore(path).Load().Value;
        Assert.DoesNotContain(s.Hotkeys, h => h.Action == "Launch missiles");
        Assert.Equal("Ctrl+Alt+R", s.GestureFor(HotkeyActions.Region));
    }
}

public class LayoutPresetTests
{
    [Fact]
    public void Builtins_exist_and_user_presets_roundtrip_without_assets()
    {
        using var t = new TempDir();
        var path = t.Of("presets.json");
        var s = new LayoutPresetStore(path);
        s.Load();
        Assert.Contains(s.All, p => p.Name == "Vertical spaced" && p.Layout.Gap == 12 && p.Layout.Padding == 16);
        s.SaveOrReplace(new LayoutPreset("Mine", LayoutOptions.Default with { Gap = 7 }, Rgba32.White));
        s.Rename("Mine", "Ours");
        var s2 = new LayoutPresetStore(path);
        s2.Load();
        var p = Assert.Single(s2.All, x => !x.BuiltIn);
        Assert.Equal("Ours", p.Name);
        Assert.Equal(7, p.Layout.Gap);
        Assert.DoesNotContain("asset", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
        Assert.True(s2.Delete("Ours"));
    }

    [Fact]
    public void Builtin_names_are_reserved_and_invalid_options_rejected()
    {
        using var t = new TempDir();
        var s = new LayoutPresetStore(t.Of("presets.json"));
        s.Load();
        Assert.Throws<ArgumentException>(() => s.SaveOrReplace(new LayoutPreset("Vertical compact", LayoutOptions.Default, Rgba32.White)));
        Assert.ThrowsAny<ArgumentException>(() => s.SaveOrReplace(new LayoutPreset("Bad", LayoutOptions.Default with { Gap = -1 }, Rgba32.White)));
    }

    [Fact]
    public void Corrupt_preset_file_is_ignored_with_message()
    {
        using var t = new TempDir();
        var path = t.Of("presets.json");
        File.WriteAllText(path, "[1,2,3");
        var s = new LayoutPresetStore(path);
        s.Load();
        Assert.NotNull(s.LastWarning);
        Assert.Equal(LayoutPresetStore.BuiltIns.Length, s.All.Count);
    }
}

public class CapturePresetTests
{
    [Fact]
    public void Output_path_never_overwrites()
    {
        var preset = new CapturePreset { OutputFolder = @"C:\Out", FileNameTemplate = "Shot {date}" };
        var now = new DateTime(2026, 9, 27, 10, 11, 12);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Out\Shot 2026-09-27.png", @"C:\Out\Shot 2026-09-27 (2).png" };
        var p = CapturePresetStore.ResolveOutputPath(preset, now, taken.Contains);
        Assert.Equal(@"C:\Out\Shot 2026-09-27 (3).png", p);
    }

    [Fact]
    public void Invalid_presets_are_dropped_on_load()
    {
        using var t = new TempDir();
        var path = t.Of("capture-presets.json");
        File.WriteAllText(path, "{\"version\":1,\"presets\":[{\"name\":\"ok\"},{\"name\":\"bad\",\"delaySeconds\":7},{\"name\":\"evil\",\"fileNameTemplate\":\"..\\\\x\"}]}");
        var s = new CapturePresetStore(path);
        s.Load();
        Assert.Equal(["ok"], s.Presets.Select(p => p.Name));
    }

    [Fact]
    public void Save_rejects_duplicates_and_relative_folders()
    {
        using var t = new TempDir();
        var s = new CapturePresetStore(t.Of("cp.json"));
        Assert.Throws<ArgumentException>(() => s.Save([new CapturePreset { Name = "a" }, new CapturePreset { Name = "A" }]));
        Assert.Throws<ArgumentException>(() => s.Save([new CapturePreset { Name = "a", OutputFolder = "relative" }]));
    }

    [Fact]
    public void Last_region_requires_same_topology()
    {
        var r = new LastRegion(new PixelRect(1, 2, 3, 4), "A");
        Assert.Equal(r, LastRegionStore.Resolve(r, "A"));
        Assert.Null(LastRegionStore.Resolve(r, "B"));
    }
}

public class HistoryRetentionTests
{
    private static async Task<ImageAsset> PutAsync(FileAssetStore store, string content)
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(content));
        return await store.PutAsync(ms, 10, 10);
    }

    [Fact]
    public void Policy_deletes_oldest_unpinned_first_and_skips_protected()
    {
        var t0 = DateTimeOffset.Now;
        CaptureEntry E(int i, bool pin = false) => new() { AssetId = new string((char)('a' + i), 64), CapturedAt = t0.AddMinutes(i), Pinned = pin, Bytes = 1 };
        var entries = new[] { E(0, pin: true), E(1), E(2), E(3), E(4) };
        var victims = new RetentionPolicy(MaxCount: 2).SelectForDeletion(entries, new HashSet<string> { entries[1].AssetId });
        Assert.Equal([entries[2].Id, entries[3].Id], victims.Select(v => v.Id));
    }

    [Fact]
    public async Task Delete_keeps_shared_and_protected_assets()
    {
        using var t = new TempDir();
        var assets = new FileAssetStore(t.Of("assets"));
        var h = new CaptureHistoryStore(t.Of("history"), assets);
        var a = await PutAsync(assets, "one");
        var b = await PutAsync(assets, "two");
        var e1 = h.Add(a, null);
        var e2 = h.Add(a, null);
        var e3 = h.Add(b, null);
        h.Delete([e1.Id]);
        Assert.True(assets.Contains(a.Id)); // still used by e2
        h.Delete([e3.Id], new HashSet<string> { b.Id });
        Assert.True(assets.Contains(b.Id)); // protected by open document
        h.Delete([e2.Id]);
        Assert.False(assets.Contains(a.Id));
    }

    [Fact]
    public async Task Damaged_index_is_rebuilt_from_metadata()
    {
        using var t = new TempDir();
        var assets = new FileAssetStore(t.Of("assets"));
        var h = new CaptureHistoryStore(t.Of("history"), assets);
        var a = await PutAsync(assets, "x");
        var e = h.Add(a, null);
        h.SetPinned(e.Id, true);
        File.WriteAllText(h.IndexPath, "garbage");
        var h2 = new CaptureHistoryStore(t.Of("history"), assets);
        h2.Load();
        var only = Assert.Single(h2.Entries);
        Assert.Equal(e.Id, only.Id);
        Assert.True(only.Pinned);
        Assert.NotNull(h2.LastWarning);
    }
}

public class RecoveryTests
{
    private static RecoverySnapshot Snap(Guid id, long rev, string name) =>
        new() { DocumentId = id, Revision = rev, Document = DocumentState.CreateEmpty(name) with { Id = id } };

    [Fact]
    public async Task Stale_revision_never_replaces_newer_snapshot()
    {
        using var t = new TempDir();
        var s = new RecoveryStore(t.Path);
        var id = Guid.NewGuid();
        Assert.True(await s.SaveAsync(Snap(id, 5, "new")));
        Assert.False(await s.SaveAsync(Snap(id, 3, "old")));
        Assert.Equal("new", Assert.Single(s.FindCandidates()).Snapshot.Document.Name);
    }

    [Fact]
    public async Task Corrupt_newest_falls_back_to_previous_revision()
    {
        using var t = new TempDir();
        var s = new RecoveryStore(t.Path);
        var id = Guid.NewGuid();
        await s.SaveAsync(Snap(id, 1, "one"));
        await s.SaveAsync(Snap(id, 2, "two"));
        File.WriteAllText(Path.Combine(t.Path, id.ToString("N"), 2L.ToString("D12") + ".json"), "{broken");
        Assert.Equal("one", Assert.Single(s.FindCandidates()).Snapshot.Document.Name);
    }

    [Fact]
    public async Task Discard_up_to_revision_keeps_newer()
    {
        using var t = new TempDir();
        var s = new RecoveryStore(t.Path);
        var id = Guid.NewGuid();
        await s.SaveAsync(Snap(id, 1, "one"));
        await s.SaveAsync(Snap(id, 2, "two"));
        s.Discard(id, upToRevision: 1);
        Assert.Equal("two", Assert.Single(s.FindCandidates()).Snapshot.Document.Name);
        s.Discard(id);
        Assert.Empty(s.FindCandidates());
    }

    [Fact]
    public async Task Scheduler_respects_quiet_period_and_saves_latest()
    {
        using var t = new TempDir();
        var store = new RecoveryStore(t.Path);
        var now = DateTimeOffset.Now;
        using var sched = new AutosaveScheduler(store, () => now);
        var doc = DocumentState.CreateEmpty("draft");
        sched.NotifyChanged(doc, null);
        await sched.Tick();
        Assert.Empty(store.FindCandidates()); // still inside the 2 s quiet period
        now += TimeSpan.FromSeconds(3);
        await sched.Tick();
        Assert.Equal("draft", Assert.Single(store.FindCandidates()).Snapshot.Document.Name);
        Assert.Equal(1, sched.LastSavedRevision);
    }
}

public class AssetRetentionTests
{
    [Fact]
    public async Task Collect_deletes_only_unreferenced_assets_past_grace_period()
    {
        using var t = new TempDir();
        var store = new FileAssetStore(t.Of("assets"));
        async Task<ImageAsset> Put(string s) { using var ms = new MemoryStream(Encoding.UTF8.GetBytes(s)); return await store.PutAsync(ms, 4, 4); }
        var kept = await Put("kept");
        var guarded = await Put("guarded");
        var orphan = await Put("orphan");
        var svc = new AssetRetentionService(store) { GracePeriod = TimeSpan.FromMinutes(10) };
        svc.AddOwner(() => [kept.Id]);

        Assert.Equal(0, svc.Collect(DateTime.UtcNow)); // everything is fresh
        using (svc.Protect([guarded.Id]))
        {
            Assert.Equal(1, svc.Collect(DateTime.UtcNow.AddHours(1)));
            Assert.True(store.Contains(guarded.Id));
        }
        Assert.True(store.Contains(kept.Id));
        Assert.False(store.Contains(orphan.Id));
    }

    [Fact]
    public void References_include_stamp_assets()
    {
        var stampAsset = new string('b', 64);
        var doc = DocumentState.CreateEmpty() with
        {
            Annotations = [new Core.Documents.Annotations.StampAnnotation { AssetId = stampAsset }],
        };
        Assert.Contains(stampAsset, AssetReferences.Of([doc]));
    }
}
