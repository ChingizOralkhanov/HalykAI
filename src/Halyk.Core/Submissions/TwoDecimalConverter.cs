using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Halyk.Core.Submissions;

/// <summary>
/// Writes the graded number as a plain JSON number with exactly two decimals, and refuses to
/// read anything that is not a number. The case makes a wrong value type ungradable, so a
/// quoted "1234.50" has to fail here rather than round-trip and look healthy in the validator.
/// </summary>
public sealed class TwoDecimalConverter : JsonConverter<decimal?>
{
    public static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    public override decimal? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.Number => reader.GetDecimal(),
            _ => throw new JsonException($"'actual' must be a JSON number or null, found {reader.TokenType}"),
        };

    public override void Write(Utf8JsonWriter writer, decimal? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteRawValue(Round(value.Value).ToString("0.00", CultureInfo.InvariantCulture));
    }
}
