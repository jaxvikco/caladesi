using System.Diagnostics.CodeAnalysis;
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
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, WriterOptions))
        {
            write(writer);
        }

        return stream.ToArray();
    }

    public static JsonElement? Get(this JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null ? value : null;
    }

    public static JsonElement? Get(this JsonElement? element, string name)
    {
        return element?.Get(name);
    }

    [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code", Justification = "False positive: Get returns null for a missing property, so current can become null inside the loop.")]
    public static JsonElement? Path(this JsonElement element, params string[] names)
    {
        JsonElement? current = element;
        foreach (string name in names)
        {
            current = current?.Get(name);
            if (current == null)
            {
                return null;
            }
        }
        return current;
    }

    public static string? GetString(this JsonElement? element)
    {
        return element is { ValueKind: JsonValueKind.String } e ? e.GetString() : null;
    }

    public static int? GetInt(this JsonElement? element)
    {
        return element is { ValueKind: JsonValueKind.Number } e && e.TryGetInt32(out int n) ? n : null;
    }

    public static bool GetBool(this JsonElement? element, bool fallback = false)
    {
        return element is { ValueKind: JsonValueKind.True or JsonValueKind.False } e ? e.GetBoolean() : fallback;
    }

    public static IEnumerable<JsonElement> Items(this JsonElement? element)
    {
        if (element is { ValueKind: JsonValueKind.Array } e)
        {
            foreach (JsonElement item in e.EnumerateArray())
            {
                yield return item;
            }
        }
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
