using System.Text.Encodings.Web;
using System.Text.Json;
using Caladesi.Compiler.Text;

namespace Caladesi.LanguageServer.Protocol;

internal static class Json
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        SkipValidation = false,
    };

    public static byte[] Build(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions)) write(writer);
        return stream.ToArray();
    }

    public static JsonElement? Get(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;

    public static JsonElement? Get(this JsonElement? element, string name) => element?.Get(name);

    public static JsonElement? Path(this JsonElement element, params string[] names)
    {
        JsonElement? current = element;
        foreach (var name in names)
        {
            current = current?.Get(name);
            if (current == null) return null;
        }
        return current;
    }

    public static string? GetString(this JsonElement? element) => element is { ValueKind: JsonValueKind.String } e ? e.GetString() : null;

    public static int? GetInt(this JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Number } e && e.TryGetInt32(out var n) ? n : null;

    public static bool GetBool(this JsonElement? element, bool fallback = false) =>
        element is { ValueKind: JsonValueKind.True or JsonValueKind.False } e ? e.GetBoolean() : fallback;

    public static IEnumerable<JsonElement> Items(this JsonElement? element)
    {
        if (element is { ValueKind: JsonValueKind.Array } e)
            foreach (var item in e.EnumerateArray()) yield return item;
    }

    public static void WritePosition(this Utf8JsonWriter w, LinePosition p)
    {
        w.WriteStartObject();
        w.WriteNumber("line", p.Line);
        w.WriteNumber("character", p.Character);
        w.WriteEndObject();
    }

    public static void WriteRange(this Utf8JsonWriter w, LinePositionSpan range)
    {
        w.WriteStartObject();
        w.WritePropertyName("start");
        w.WritePosition(range.Start);
        w.WritePropertyName("end");
        w.WritePosition(range.End);
        w.WriteEndObject();
    }

    public static void WriteRange(this Utf8JsonWriter w, string name, LinePositionSpan range)
    {
        w.WritePropertyName(name);
        w.WriteRange(range);
    }
}
