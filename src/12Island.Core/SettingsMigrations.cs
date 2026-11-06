using System.Text.Json;
using System.Text.Json.Nodes;
using NodeValue = System.Text.Json.Nodes.JsonValue;

namespace TwelveIsland.Core;

// settings.json records AppVersion, the version of the TwelveIsland that last saved it. Each step is labelled with the
// application version that introduced a format change and converts files saved by earlier versions. Read runs, in
// order, every step newer than the file's version and not newer than the running application; files without
// AppVersion count as 0.0.0.
// Reading stays tolerant: missing members take their defaults and unknown members are ignored, so a release that only
// adds settings needs no step. Add a step when a setting is renamed, split, merged or changes meaning, label it with the
// version the branch builds (its newest releases/<version>.md), and test it with a file in the earlier format. Release
// notes files are added by the maintainer, not for a step; the pull request check refuses a step with any other label.
public static class SettingsMigrations
{
    private static readonly (Version Version, Action<JsonObject> Apply)[] Steps =
    [
        (new Version(0, 1, 0), To0_1_0),
        (new Version(1, 2, 3), To1_2_3),
    ];

    // Upgrades the settings object in place and reads it. A file saved by a newer version runs no step.
    public static DeckSettings Read(JsonObject root, Version running, out Version saved)
    {
        saved = VersionOf(root);
        foreach (var (version, apply) in Steps)
            if (version > saved && version <= running) apply(root);
        var settings = root.Deserialize<DeckSettings>() ?? new();
        settings.Normalize();
        return settings;
    }

    public static DeckSettings Read(string json, Version running, out Version saved) => Read(Parse(json), running, out saved);

    public static DeckSettings Read(string json) => Read(json, ApplicationVersion.Parse(ApplicationVersion.Current), out _);

    public static JsonObject Parse(string json) => JsonNode.Parse(json) as JsonObject ?? throw new JsonException("Settings must be a JSON object.");

    public static Version VersionOf(JsonObject settings) =>
        ApplicationVersion.Parse(settings["AppVersion"] is NodeValue value && value.TryGetValue(out string? text) ? text : null);

    // 0.1.0: the conversions that earlier builds made implicitly while reading.
    private static void To0_1_0(JsonObject root)
    {
        // One pair of switches used to apply to both the notch and the capsule.
        Split(root, "CompactSummary", "NotchSummary", "CapsuleSummary");
        Split(root, "CompactMusic", "NotchMusic", "CapsuleMusic");
        // Earlier builds were Chinese only. Language now defaults to English, so a file without it keeps Chinese.
        if (!root.ContainsKey("Language")) root["Language"] = Loc.Chinese;
        // Four on/off switches preceded the ordered application list; their order matches ProviderCatalog.BuiltIns.
        string[] switches = ["Claude", "Codex", "Antigravity", "Cursor"];
        if (root["Apps"] is not JsonArray && switches.Any(root.ContainsKey))
            root["Apps"] = new JsonArray(ProviderCatalog.BuiltIns.Select((id, i) =>
                JsonSerializer.SerializeToNode(new AppEntry { QuotaSource = id, UsageSource = id, Enabled = Flag(root, switches[i]) })).ToArray());
        foreach (var name in switches) root.Remove(name);
        if (root["Apps"] is not JsonArray apps) return;
        foreach (var app in apps.OfType<JsonObject>())
        {
            // A row once had a single source, saved as Id.
            if (app["Id"] is { } id && !app.ContainsKey("QuotaSource")) app["QuotaSource"] = id.DeepClone();
            app.Remove("Id");
            // Statistics sources 8 and 9 were a short-lived split of Claude.
            if (app["UsageSource"] is NodeValue usage && usage.TryGetValue(out int source) && source is 8 or 9)
                app["UsageSource"] = (int)ProviderId.Claude;
        }
    }

    // 1.2.3: the equalizer has its own switch. It showed with the music, so it starts as the music's switch was. Its color was
    // the cover's or the theme's (MusicCoverColor); it is now chosen as the clock's is.
    private static void To1_2_3(JsonObject root)
    {
        foreach (var style in new[] { "Notch", "Capsule" })
            if (!root.ContainsKey(style + "Equalizer")) root[style + "Equalizer"] = Flag(root, style + "Music");
        if (!root.ContainsKey("EqualizerColor")) root["EqualizerColor"] = (int)ClockColor.White;
        root.Remove("MusicCoverColor");
    }

    private static bool Flag(JsonObject root, string name) => root[name] is not NodeValue value || !value.TryGetValue(out bool flag) || flag;

    private static void Split(JsonObject root, string old, params string[] names)
    {
        if (root[old] is not { } value) return;
        foreach (var name in names)
            if (!root.ContainsKey(name)) root[name] = value.DeepClone();
        root.Remove(old);
    }
}
