using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relay.Core;

/// <summary>
/// JSON as the Mac app writes it (Swift's JSONEncoder): camelCase keys,
/// uppercase UUIDs, ISO-8601 dates to the second, nil optionals left out,
/// and unknown enum values read as the enum's first member.
/// </summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = Create(indented: true);
    static readonly JsonSerializerOptions Compact = Create(indented: false);

    static JsonSerializerOptions Create(bool indented)
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = indented,
            // Like the Mac's .withoutEscapingSlashes: URLs stay readable.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        };
        o.Converters.Add(new UpperGuidConverter());
        o.Converters.Add(new IsoDateConverter());
        o.Converters.Add(new LenientEnumConverterFactory());
        o.Converters.Add(new CollectionItemConverter());
        return o;
    }

    public static string Serialize<T>(T value, bool indented = true) =>
        JsonSerializer.Serialize(value, indented ? Options : Compact);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>A deep copy, for Duplicate, Save As and history snapshots (the Mac models are value types).</summary>
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Compact), Compact)!;

    /// <summary>Equal content, for "has unsaved edits".</summary>
    public static bool Same<T>(T a, T b) => Serialize(a, false) == Serialize(b, false);
}

sealed class UpperGuidConverter : JsonConverter<Guid>
{
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Guid.TryParse(reader.GetString(), out var g) ? g : Guid.NewGuid();

    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString("D").ToUpperInvariant());
}

sealed class IsoDateConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateTime.TryParse(reader.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d)
            ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : DateTime.UtcNow;

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));
}

/// <summary>camelCase names ("apiKey"); an unknown value reads as the first member, never an error.</summary>
sealed class LenientEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type t) => t.IsEnum;

    public override JsonConverter CreateConverter(Type t, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(t))!;

    sealed class Converter<T> : JsonConverter<T> where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String && Enum.TryParse<T>(reader.GetString(), ignoreCase: true, out var v) && Enum.IsDefined(v)
                ? v : default;

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
    }
}

/// <summary><c>{"folder": {…}}</c> or <c>{"request": {…}}</c>, as Swift encodes the enum.</summary>
sealed class CollectionItemConverter : JsonConverter<CollectionItem>
{
    public override CollectionItem? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        if (root.TryGetProperty("folder", out var folder)) return folder.Deserialize<Folder>(options);
        if (root.TryGetProperty("request", out var request)) return request.Deserialize<ApiRequest>(options);
        return null;
    }

    public override void Write(Utf8JsonWriter writer, CollectionItem value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case Folder f:
                writer.WritePropertyName("folder");
                JsonSerializer.Serialize(writer, f, options);
                break;
            case ApiRequest r:
                writer.WritePropertyName("request");
                JsonSerializer.Serialize(writer, r, options);
                break;
        }
        writer.WriteEndObject();
    }
}
