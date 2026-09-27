using System.Text.Json;
using System.Text.Json.Serialization;

namespace SnagItOpen.Core.Geometry;

public sealed class Rgba32JsonConverter : JsonConverter<Rgba32>
{
    public override Rgba32 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString();
        return Rgba32.TryParse(s, out var c) ? c : throw new JsonException($"Invalid color '{s}'.");
    }

    public override void Write(Utf8JsonWriter writer, Rgba32 value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToHex());
}
