using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnagItOpen.Core;
using SnagItOpen.Core.Documents;
using SnagItOpen.Imaging.Effects;

namespace SnagItOpen.Imaging.Rendering;

/// <summary>
/// Bounded LRU cache of decoded, frozen BGRA32 bitmaps (thread-safe; frozen bitmaps are usable from
/// any thread). Leaving the cache never deletes the asset on disk; a later request reloads it.
/// Also caches effect-processed variants and thumbnails.
/// </summary>
public sealed class BitmapAssetCache
{
    private sealed class Entry(string key, BitmapSource bmp, long bytes)
    {
        public string Key { get; } = key;
        public BitmapSource Bitmap { get; } = bmp;
        public long Bytes { get; } = bytes;
    }

    private readonly IAssetStore _store;
    private readonly long _maxBytes;
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _map = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _lru = new();
    private long _bytes;

    public BitmapAssetCache(IAssetStore store, long maxBytes = Limits.DecodedCacheBytes)
    {
        _store = store;
        _maxBytes = Math.Max(1, maxBytes);
    }

    public IAssetStore Store => _store;
    public long Bytes { get { lock (_gate) return _bytes; } }
    public int Count { get { lock (_gate) return _map.Count; } }
    public int Loads { get; private set; }

    /// <summary>Full-resolution frozen bitmap for an asset. Throws AssetNotFoundException when missing.</summary>
    public BitmapSource Get(string assetId) => GetOrAdd("a:" + assetId, () => Decode(assetId));

    /// <summary>Asset bitmap with the layer's blur/pixelate effects applied (full source resolution).</summary>
    public BitmapSource GetProcessed(ImageLayer layer)
    {
        if (layer.Effects is not { Length: > 0 }) return Get(layer.AssetId);
        var key = "e:" + layer.AssetId + "|" + string.Join(";", layer.Effects.Select(e => $"{e.Kind}{e.Region}{e.Strength}"));
        return GetOrAdd(key, () => ObscureProcessor.Apply(GetPixels(layer.AssetId), layer.Effects).ToBitmap());
    }

    /// <summary>Copy of asset pixels (straight BGRA).</summary>
    public PixelBuffer GetPixels(string assetId) => PixelBuffer.FromBitmap(Get(assetId));

    /// <summary>Downscaled thumbnail (longest side ≤ <paramref name="maxSide"/>).</summary>
    public BitmapSource GetThumbnail(string assetId, int maxSide = 256) => GetOrAdd($"t{maxSide}:" + assetId, () =>
    {
        var src = Get(assetId);
        double s = Math.Min(1.0, (double)maxSide / Math.Max(src.PixelWidth, src.PixelHeight));
        if (s >= 1) return src;
        var t = new TransformedBitmap(src, new ScaleTransform(s, s));
        var wb = new WriteableBitmap(t);
        wb.Freeze();
        return wb;
    });

    public bool IsCached(string assetId) { lock (_gate) return _map.ContainsKey("a:" + assetId); }

    public void Evict(string assetId)
    {
        lock (_gate)
        {
            foreach (var k in _map.Keys.Where(k => k.EndsWith(assetId, StringComparison.Ordinal) || k.Contains(":" + assetId + "|", StringComparison.Ordinal)).ToList())
                Remove(k);
        }
    }

    public void Clear()
    {
        lock (_gate) { _map.Clear(); _lru.Clear(); _bytes = 0; }
    }

    private BitmapSource GetOrAdd(string key, Func<BitmapSource> factory)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Bitmap;
            }
        }
        var bmp = factory();
        if (!bmp.IsFrozen) bmp.Freeze();
        long bytes = (long)bmp.PixelWidth * bmp.PixelHeight * 4;
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing)) return existing.Value.Bitmap;
            var n = _lru.AddFirst(new Entry(key, bmp, bytes));
            _map[key] = n;
            _bytes += bytes;
            while (_bytes > _maxBytes && _lru.Count > 1) Remove(_lru.Last!.Value.Key);
        }
        return bmp;
    }

    private void Remove(string key)
    {
        if (!_map.Remove(key, out var node)) return;
        _lru.Remove(node);
        _bytes -= node.Value.Bytes;
    }

    private BitmapSource Decode(string assetId)
    {
        using var s = _store.OpenReadAsync(assetId).GetAwaiter().GetResult();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        ms.Position = 0;
        var dec = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        BitmapSource f = dec.Frames[0];
        if (f.Format != PixelFormats.Bgra32) f = new FormatConvertedBitmap(f, PixelFormats.Bgra32, null, 0);
        // Normalize to 96 DPI so 1 source pixel == 1 document pixel when drawn.
        var px = PixelBuffer.FromBitmap(f);
        Loads++;
        return px.ToBitmap();
    }
}
