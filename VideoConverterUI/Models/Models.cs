using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoConverterUI.Models;

public sealed class FlexibleNullableIntConverter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        if (reader.TokenType == JsonTokenType.Number)
        {
            if (reader.TryGetInt32(out int i))
                return i;
            if (reader.TryGetInt64(out long l))
                return (int)l;
            if (reader.TryGetDouble(out double d))
                return double.IsNaN(d) || double.IsInfinity(d) ? null : (int)Math.Round(d);
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var str = reader.GetString();
            if (string.IsNullOrWhiteSpace(str))
                return null;
            if (int.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out int i))
                return i;
            if (double.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out double d))
                return double.IsNaN(d) || double.IsInfinity(d) ? null : (int)Math.Round(d);
        }

        return null;
    }

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
            writer.WriteNumberValue(value.Value);
        else
            writer.WriteNullValue();
    }
}

public sealed class FlexibleDoubleConverter : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return 0.0;

        if (reader.TokenType == JsonTokenType.Number)
        {
            if (reader.TryGetDouble(out double d))
                return d;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var str = reader.GetString();
            if (double.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out double d))
                return d;
        }

        return 0.0;
    }

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value);
    }
}

public sealed class MetadataResponse
{
    [JsonPropertyName("title")]        public string Title        { get; set; } = "";
    [JsonPropertyName("description")]  public string Description  { get; set; } = "";
    [JsonPropertyName("tags")]         public List<string> Tags   { get; set; } = [];
    [JsonPropertyName("thumbnail")]    public string Thumbnail    { get; set; } = "";
    [JsonPropertyName("duration")]
    [JsonConverter(typeof(FlexibleDoubleConverter))]
    public double Duration     { get; set; }
    [JsonPropertyName("uploader")]     public string Uploader     { get; set; } = "";
    [JsonPropertyName("artist")]       public string? Artist      { get; set; }
    [JsonPropertyName("album")]        public string? Album       { get; set; }
    [JsonPropertyName("is_music")]     public bool IsMusic        { get; set; }
    [JsonPropertyName("release_year")] public string? ReleaseYear { get; set; }
    [JsonPropertyName("track_count")]
    [JsonConverter(typeof(FlexibleNullableIntConverter))]
    public int? TrackCount     { get; set; }
}

public sealed class JobCreateResponse
{
    [JsonPropertyName("job_id")] public string JobId { get; set; } = "";
}

public sealed class JobStatus
{
    [JsonPropertyName("id")]       public string Id       { get; set; } = "";
    [JsonPropertyName("status")]   public string Status   { get; set; } = "";
    [JsonPropertyName("progress")]
    [JsonConverter(typeof(FlexibleDoubleConverter))]
    public double Progress { get; set; }
    [JsonPropertyName("message")]  public string Message  { get; set; } = "";
    [JsonPropertyName("error")]    public string? Error   { get; set; }
    [JsonPropertyName("eta")]
    [JsonConverter(typeof(FlexibleNullableIntConverter))]
    public int?    Eta     { get; set; }
    [JsonPropertyName("speed")]    public string? Speed   { get; set; }
    [JsonPropertyName("result")]   public Dictionary<string, object>? Result { get; set; }
}

public sealed class PlaylistEntry
{
    [JsonPropertyName("id")]        public string Id        { get; set; } = "";
    [JsonPropertyName("title")]     public string Title     { get; set; } = "";
    [JsonPropertyName("url")]       public string Url       { get; set; } = "";
    [JsonPropertyName("duration")]
    [JsonConverter(typeof(FlexibleDoubleConverter))]
    public double Duration { get; set; }
    [JsonPropertyName("uploader")]  public string Uploader  { get; set; } = "";
    [JsonPropertyName("thumbnail")] public string Thumbnail { get; set; } = "";
}

public sealed class PlaylistInfo
{
    [JsonPropertyName("title")]   public string Title               { get; set; } = "";
    [JsonPropertyName("count")]   public int Count                  { get; set; }
    [JsonPropertyName("entries")] public List<PlaylistEntry> Entries { get; set; } = [];
}
