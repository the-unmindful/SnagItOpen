using System.IO;
using System.IO.Compression;
using System.Text;
using SnagItOpen.Core;
using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Documents.Annotations;
using SnagItOpen.Core.Editing;
using SnagItOpen.Core.Geometry;
using SnagItOpen.Storage.Assets;
using SnagItOpen.Storage.Projects;

namespace SnagItOpen.Windows.Tests;

public class ProjectRoundTripTests
{
    private static async Task<DocumentState> RichDocAsync(Sandbox s)
    {
        var doc = await s.DocAsync(LayoutOptions.Default with { Mode = LayoutMode.Free }, Fixtures.Red100x50(), Fixtures.Checker(), Fixtures.Coordinates(20, 20));
        var ids = doc.Images.Select(i => i.Id).ToArray();
        doc = DocumentOps.Crop(doc, ids[0], new PixelRect(10, 5, 80, 40));
        doc = DocumentOps.SetBounds(doc, ids[1], new PixelRect(-20, -10, 16, 16));
        doc = DocumentOps.Rotate(doc, [ids[2]], 1);
        doc = DocumentOps.ChangeZOrder(doc, [ids[0]], DocumentOps.ZMove.ToFront);
        doc = DocumentOps.AddAnnotation(doc, new ArrowAnnotation { Start = new(1, 2), End = new(30, 40), ImageLayerId = ids[0] });
        doc = DocumentOps.AddAnnotation(doc, new CalloutAnnotation { Bounds = new RectD(5, 5, 60, 30), Tail = new(100, 90), Text = "Hi\nthere" });
        doc = DocumentOps.AddAnnotation(doc, new FreehandAnnotation { Points = [new(0, 0), new(3, 4), new(9, 1)] });
        doc = DocumentOps.AddAnnotation(doc, new StepAnnotation { Bounds = new RectD(0, 0, 24, 24), Number = 3 });
        doc = DocumentOps.AddAnnotation(doc, new RedactionAnnotation { Bounds = new RectD(1, 1, 5, 5) });
        doc = DocumentOps.AddAnnotation(doc, new MagnifierAnnotation { Bounds = new RectD(120, 0, 40, 40), SourceRegion = new RectD(0, 0, 10, 10) });
        doc = DocumentOps.AddEffect(doc, ids[2], new ImageEffect(Guid.NewGuid(), ImageEffectKind.Blur, new PixelRect(0, 0, 5, 5), 3));
        doc = DocumentOps.SetEdge(doc, [ids[1]], new EdgeStyle { BorderWidth = 2, TornSides = TornSides.Bottom, TornSeed = 42 });
        return doc;
    }

    [Fact]
    public async Task Roundtrip_preserves_everything_after_deleting_sources()
    {
        using var s = new Sandbox();
        var doc = await RichDocAsync(s);
        var store = new ProjectStore(s.Store);
        var path = s.PathOf("p.sio");
        var saved = await store.SaveAsync(doc, path, null);
        Assert.True(saved.IsSuccess, saved.Message);

        // Open into a fresh asset store, as on another machine.
        var other = new FileAssetStore(s.PathOf("other-assets"));
        var loaded = await new ProjectStore(other).LoadAsync(path);
        Assert.True(loaded.IsSuccess, loaded.Message);
        var d = loaded.Value!;
        Assert.Equal(doc.Images, d.Images, ImageEq.Instance);
        Assert.Equal(doc.LayoutOrder, d.LayoutOrder);
        Assert.Equal(doc.ExportArea, d.ExportArea);
        Assert.Equal(doc.Annotations.Length, d.Annotations.Length);
        for (int i = 0; i < doc.Annotations.Length; i++)
        {
            Assert.Equal(doc.Annotations[i].GetType(), d.Annotations[i].GetType());
            Assert.Equal(doc.Annotations[i].Bounds, d.Annotations[i].Bounds);
        }
        Assert.Equal("Hi\nthere", ((CalloutAnnotation)d.Annotations[1]).Text);
        Assert.Equal(3, ((FreehandAnnotation)d.Annotations[2]).Points.Length);
        foreach (var a in d.Assets) Assert.True(other.Contains(a.Id));
    }

    private sealed class ImageEq : IEqualityComparer<ImageLayer>
    {
        public static readonly ImageEq Instance = new();
        public bool Equals(ImageLayer? a, ImageLayer? b) => a is not null && b is not null &&
            a.Id == b.Id && a.AssetId == b.AssetId && a.SourceCrop == b.SourceCrop && a.Bounds == b.Bounds &&
            a.QuarterTurns == b.QuarterTurns && a.FlipHorizontal == b.FlipHorizontal && a.Visible == b.Visible &&
            a.Effects.SequenceEqual(b.Effects) && a.Edge == b.Edge;
        public int GetHashCode(ImageLayer o) => o.Id.GetHashCode();
    }

    private static async Task<string> WriteZipAsync(Sandbox s, Action<ZipArchive> build)
    {
        var p = s.PathOf(Guid.NewGuid().ToString("N") + ".sio");
        await using var fs = File.Create(p);
        using (var z = new ZipArchive(fs, ZipArchiveMode.Create)) build(z);
        return p;
    }

    private static void Entry(ZipArchive z, string name, string text)
    {
        using var w = new StreamWriter(z.CreateEntry(name).Open(), Encoding.UTF8);
        w.Write(text);
    }

    [Fact]
    public async Task Malformed_json_is_invalid_project()
    {
        using var s = new Sandbox();
        var p = await WriteZipAsync(s, z => Entry(z, "manifest.json", "{ not json"));
        var r = await new ProjectStore(s.Store).LoadAsync(p);
        Assert.Equal(ErrorCode.InvalidProject, r.Error);
    }

    [Fact]
    public async Task Future_schema_is_unsupported()
    {
        using var s = new Sandbox();
        var p = await WriteZipAsync(s, z => Entry(z, "manifest.json", "{\"format\":\"snagitopen-project\",\"schemaVersion\":99}"));
        var r = await new ProjectStore(s.Store).LoadAsync(p);
        Assert.True(r.Error == ErrorCode.UnsupportedSchema, r.Message);
    }

    [Fact]
    public async Task Unknown_annotation_type_is_unsupported_not_dropped()
    {
        using var s = new Sandbox();
        var json = "{\"format\":\"snagitopen-project\",\"schemaVersion\":1,\"document\":{\"annotations\":[{\"type\":\"hologram\"}]}}";
        var p = await WriteZipAsync(s, z => Entry(z, "manifest.json", json));
        var r = await new ProjectStore(s.Store).LoadAsync(p);
        Assert.False(r.IsSuccess);
        Assert.True(r.Error == ErrorCode.UnsupportedSchema, r.Message);
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("C:/evil.txt")]
    [InlineData("assets/notahash.png")]
    public async Task Unexpected_or_traversal_entries_rejected(string name)
    {
        using var s = new Sandbox();
        var p = await WriteZipAsync(s, z => { Entry(z, "manifest.json", "{}"); Entry(z, name, "x"); });
        var r = await new ProjectStore(s.Store).LoadAsync(p);
        Assert.Equal(ErrorCode.InvalidProject, r.Error);
    }

    [Fact]
    public async Task Duplicate_entry_rejected()
    {
        using var s = new Sandbox();
        var p = await WriteZipAsync(s, z => { Entry(z, "manifest.json", "{}"); Entry(z, "manifest.json", "{}"); });
        var r = await new ProjectStore(s.Store).LoadAsync(p);
        Assert.Equal(ErrorCode.InvalidProject, r.Error);
    }

    [Fact]
    public async Task Missing_asset_rejected()
    {
        using var s = new Sandbox();
        var doc = await s.DocAsync(LayoutOptions.Default, Fixtures.Red100x50());
        var path = s.PathOf("m.sio");
        Assert.True((await new ProjectStore(s.Store).SaveAsync(doc, path, null)).IsSuccess);
        // Strip the asset entry.
        using (var z = ZipFile.Open(path, ZipArchiveMode.Update))
            z.Entries.First(e => e.FullName.StartsWith("assets/")).Delete();
        var r = await new ProjectStore(new FileAssetStore(s.PathOf("empty"))).LoadAsync(path);
        Assert.Equal(ErrorCode.InvalidProject, r.Error);
    }

    [Fact]
    public async Task Tampered_asset_hash_rejected()
    {
        using var s = new Sandbox();
        var doc = await s.DocAsync(LayoutOptions.Default, Fixtures.Red100x50());
        var path = s.PathOf("t.sio");
        Assert.True((await new ProjectStore(s.Store).SaveAsync(doc, path, null)).IsSuccess);
        var other = await s.Dispatcher.InvokeAsync(Fixtures.Blue80x30().EncodePng);
        using (var z = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var e = z.Entries.First(x => x.FullName.StartsWith("assets/"));
            var name = e.FullName;
            e.Delete();
            using var w = z.CreateEntry(name).Open();
            w.Write(other);
        }
        var r = await new ProjectStore(new FileAssetStore(s.PathOf("empty2"))).LoadAsync(path);
        Assert.Equal(ErrorCode.InvalidProject, r.Error);
    }

    [Fact]
    public async Task Failed_save_keeps_existing_file()
    {
        using var s = new Sandbox();
        var doc = await s.DocAsync(LayoutOptions.Default, Fixtures.Red100x50());
        var path = s.PathOf("keep.sio");
        await File.WriteAllBytesAsync(path, [9, 9, 9]);
        // Make the asset unavailable so save fails mid-way.
        s.Store.Delete(doc.Assets[0].Id);
        var r = await new ProjectStore(s.Store).SaveAsync(doc, path, null);
        Assert.False(r.IsSuccess);
        Assert.Equal([9, 9, 9], await File.ReadAllBytesAsync(path));
        Assert.Single(Directory.GetFiles(s.Root, "*.sio*"));
    }
}
