using System.Text.Json;
using System.Text.Json.Serialization;

namespace PosSync.Core.Clock;

/// <summary>Writes an <see cref="HlcTimestamp"/> as its packed 64-bit form: compact and sortable.</summary>
public sealed class HlcJsonConverter : JsonConverter<HlcTimestamp>
{
    public override HlcTimestamp Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        HlcTimestamp.Unpack(reader.GetInt64());

    public override void Write(Utf8JsonWriter writer, HlcTimestamp value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value.Pack());
}
