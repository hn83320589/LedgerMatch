using System.Text.Json;
using System.Text.Json.Serialization;

namespace LedgerMatch.Core.Persistence;

/// <summary>JSON settings used for audit before/after images and stored configuration.</summary>
public static class LedgerJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    public static string Serialize(object value) => JsonSerializer.Serialize(value, value.GetType(), Options);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new MoneyJsonConverter());
        options.Converters.Add(new YearMonthJsonConverter());
        return options;
    }

    private sealed class MoneyJsonConverter : JsonConverter<Money>
    {
        public override Money Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Money.FromDecimal(decimal.Parse(reader.GetString()!, System.Globalization.CultureInfo.InvariantCulture));

        public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToPlainString());
    }

    private sealed class YearMonthJsonConverter : JsonConverter<YearMonth>
    {
        public override YearMonth Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            YearMonth.Parse(reader.GetString()!);

        public override void Write(Utf8JsonWriter writer, YearMonth value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
}
