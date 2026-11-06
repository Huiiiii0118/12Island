using System.Text.Json;
using System.Text.Json.Serialization;

namespace TwelveIsland.Core;

// Release notes in one or more interface languages. update.json carries either plain text, shown in every language,
// or an object keyed by language ("zh-CN", "en-US", …) that the publish script splits out of releases/<version>.md.
// A reader shows its own language, then the same language of another region, then English, then the first one given,
// so notes in a language an older version does not know never leave it without text.
[JsonConverter(typeof(ReleaseNotesConverter))]
public sealed class ReleaseNotes : IEquatable<ReleaseNotes>
{
    public const string English = "en-US";
    private readonly (string Language, string Text)[] _entries;

    public ReleaseNotes(IEnumerable<(string Language, string Text)> entries) =>
        _entries = entries.Where(e => !string.IsNullOrWhiteSpace(e.Text)).Select(e => (e.Language.Trim(), e.Text.Trim())).ToArray();

    public static ReleaseNotes Of(string? text) => new([("", text ?? "")]);
    // Plain text is notes for every language.
    public static implicit operator ReleaseNotes(string text) => Of(text);
    public IReadOnlyList<(string Language, string Text)> Entries => _entries;

    public string For(string language)
    {
        if (_entries.Length == 0) return "";
        static string Primary(string tag) => tag.Split('-')[0];
        return Find(e => e.Language.Equals(language, StringComparison.OrdinalIgnoreCase))
            ?? Find(e => e.Language.Length > 0 && Primary(e.Language).Equals(Primary(language), StringComparison.OrdinalIgnoreCase))
            ?? Find(e => e.Language.Equals(English, StringComparison.OrdinalIgnoreCase))
            ?? _entries[0].Text;
        string? Find(Func<(string Language, string Text), bool> match) => _entries.FirstOrDefault(match).Text;
    }

    public bool Equals(ReleaseNotes? other) => other is not null && _entries.SequenceEqual(other._entries);
    public override bool Equals(object? obj) => Equals(obj as ReleaseNotes);
    public override int GetHashCode() => _entries.Aggregate(0, (hash, e) => HashCode.Combine(hash, e.Language, e.Text));
    public static bool operator ==(ReleaseNotes? left, ReleaseNotes? right) => left?.Equals(right) ?? right is null;
    public static bool operator !=(ReleaseNotes? left, ReleaseNotes? right) => !(left == right);
}

internal sealed class ReleaseNotesConverter : JsonConverter<ReleaseNotes>
{
    public override ReleaseNotes Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return ReleaseNotes.Of("");
        if (reader.TokenType == JsonTokenType.String) return ReleaseNotes.Of(reader.GetString());
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Release notes must be text or an object of texts.");
        var entries = new List<(string, string)>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string language = reader.GetString() ?? "";
            reader.Read();
            if (reader.TokenType != JsonTokenType.String) throw new JsonException("Release notes must be text.");
            entries.Add((language, reader.GetString() ?? ""));
        }
        return new ReleaseNotes(entries);
    }

    public override void Write(Utf8JsonWriter writer, ReleaseNotes value, JsonSerializerOptions options)
    {
        if (value.Entries is [{ Language: "" } only]) { writer.WriteStringValue(only.Text); return; }
        writer.WriteStartObject();
        foreach (var (language, text) in value.Entries) writer.WriteString(language, text);
        writer.WriteEndObject();
    }
}
