using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimpleScraper.Models;

// Unknown optional durations must not invalidate the enclosing movie or season.
public sealed class OptionalInt32Converter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.TokenType == JsonTokenType.Null ? 0 : reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var value) ? value : throw new JsonException("Invalid optional duration.");
    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
}
