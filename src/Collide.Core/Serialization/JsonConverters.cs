using System.Text.Json;
using System.Text.Json.Serialization;
using Collide.Units;

namespace Collide.Serialization;

/// <summary>Reads "40 mm" or {"value":40,"unit":"mm"}; writes {"value":…,"unit":"mm"}. A unit is always required.</summary>
public sealed class LengthJsonConverter : JsonConverter<Length>
{
    public override Length Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var text = reader.GetString();
            if (Length.TryParse(text, out var value, out var error)) return value;
            throw new JsonException(error);
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            var (number, unit) = ReadValueUnit(ref reader, "length");
            return Length.Parse($"{number.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} {unit}");
        }

        throw new JsonException("A length must be a string with a unit (e.g. \"40 mm\") or {\"value\":40,\"unit\":\"mm\"}. Bare numbers are not accepted.");
    }

    public override void Write(Utf8JsonWriter writer, Length value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("value", Math.Round(value.Millimetres, 6));
        writer.WriteString("unit", "mm");
        writer.WriteEndObject();
    }

    internal static (double Number, string Unit) ReadValueUnit(ref Utf8JsonReader reader, string kind)
    {
        double? number = null;
        string? unit = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString();
            reader.Read();
            if (string.Equals(name, "value", StringComparison.OrdinalIgnoreCase)) number = reader.GetDouble();
            else if (string.Equals(name, "unit", StringComparison.OrdinalIgnoreCase)) unit = reader.GetString();
            else reader.Skip();
        }

        if (number is null || string.IsNullOrWhiteSpace(unit)) throw new JsonException($"A {kind} object needs both 'value' and 'unit'.");
        return (number.Value, unit);
    }
}

public sealed class AngleJsonConverter : JsonConverter<Angle>
{
    public override Angle Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            if (Angle.TryParse(reader.GetString(), out var value, out var error)) return value;
            throw new JsonException(error);
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            var (number, unit) = LengthJsonConverter.ReadValueUnit(ref reader, "angle");
            return Angle.Parse($"{number.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} {unit}");
        }

        throw new JsonException("An angle must be a string with a unit (e.g. \"135 deg\").");
    }

    public override void Write(Utf8JsonWriter writer, Angle value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("value", Math.Round(value.Degrees, 6));
        writer.WriteString("unit", "deg");
        writer.WriteEndObject();
    }
}

public sealed class VolumeJsonConverter : JsonConverter<Volume>
{
    public override Volume Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var (number, unit) = LengthJsonConverter.ReadValueUnit(ref reader, "volume");
        return unit switch
        {
            "mm3" or "mm³" => Volume.FromCubicMillimetres(number),
            "m3" or "m³" => Volume.FromCubicMetres(number),
            _ => throw new JsonException($"Unknown volume unit '{unit}'."),
        };
    }

    public override void Write(Utf8JsonWriter writer, Volume value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("value", Math.Round(value.CubicMillimetres, 3));
        writer.WriteString("unit", "mm3");
        writer.WriteEndObject();
    }
}

public static class CollideJson
{
    /// <summary>Shared options: camelCase, string enums, unit-aware converters, comments allowed in configuration.</summary>
    public static JsonSerializerOptions Options { get; } = Create(indented: true);

    public static JsonSerializerOptions Create(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new LengthJsonConverter());
        options.Converters.Add(new AngleJsonConverter());
        options.Converters.Add(new VolumeJsonConverter());
        return options;
    }
}
