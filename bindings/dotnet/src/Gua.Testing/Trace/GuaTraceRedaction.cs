using System.Text;
using System.Text.Json;

namespace Gua.Testing;

// Runs before session buffers, queue, content hashes and disk serialization.
internal sealed class GuaTraceRedaction
{
    private readonly string[] _secrets;
    internal GuaTraceRedaction(IReadOnlyList<string> secrets) =>
        _secrets = secrets.Where(s => !string.IsNullOrEmpty(s)).OrderByDescending(s => s.Length).ToArray();
    internal string Text(string text)
    {
        foreach (var secret in _secrets) text = text.Replace(secret, "[redacted]");
        return text;
    }
    internal byte[] Json(JsonElement value, bool sensitive = false)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            if (sensitive) writer.WriteStringValue("[redacted]");
            else Write(writer, value);
        }
        return stream.ToArray();
    }
    private void Write(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                // Whole marked object is masked: payload field names need not be known to Trace.
                if ((value.TryGetProperty("sensitive", out var s) && s.ValueKind == JsonValueKind.True) ||
                    (value.TryGetProperty("mask", out var m) && m.ValueKind == JsonValueKind.True))
                {
                    writer.WriteStartObject(); writer.WriteBoolean("redacted", true); writer.WriteEndObject();
                    break;
                }
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(Text(property.Name));
                    Write(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) Write(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String: writer.WriteStringValue(Text(value.GetString()!)); break;
            case JsonValueKind.Undefined: writer.WriteNullValue(); break;
            default: value.WriteTo(writer); break;
        }
    }
}
