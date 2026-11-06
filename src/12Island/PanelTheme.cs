using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using TwelveIsland.Core;

namespace TwelveIsland;

// The panel theme in use (see PanelThemes in the core library). Every panel and pop-up takes its colors from here: XAML through
// the "Panel*" brushes it keeps in a dictionary merged into the application's resources, code through UI's color properties.
// Generation counts the changes, so panels can tell that what they built is out of date.
internal static class PanelTheme
{
    public static string Folder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "12Island", "themes");
    public static PanelPalette Current { get; private set; } = PanelThemes.Resolve(PanelThemes.BuiltIn(PanelThemes.Dark)!);
    // The theme on screen. While the chosen theme file has an error, this is still its last good version (see Use).
    public static PanelThemeInfo ShownTheme { get; private set; } = PanelThemes.BuiltIn(PanelThemes.Dark)!;
    public static bool KeptLastGood { get; private set; }
    // Whether the theme on screen shows its night look (PanelThemeFile.Night).
    public static bool Night { get; private set; }
    // Whether Windows' high contrast is shown: the system's colors on the whole panel, whatever theme is chosen
    // (PanelThemes.HighContrast).
    public static bool HighContrast { get; private set; }
    // When the theme on screen changes between its day and night looks next, or null for a theme without a night look.
    public static DateTime? NextDaylightChange(DeckSettings settings) => PanelThemes.NextDaylightChange(ShownTheme.File, DateTime.Now, settings.ThemeDaylight);
    public static int Generation { get; private set; }
    // Raised just before another palette becomes current; true when a theme trades its day and night looks.
    public static event Action<bool>? Changing;
    // The themes found in the folder at the last reload.
    public static IReadOnlyList<PanelThemeInfo> UserThemes { get; private set; } = [];

    private static string _signature = "";
    private static bool _loaded;
    private static string? _chosen;
    // Changes made on the theme page and not yet saved, by theme id. They take effect only once saved: until then neither the
    // panel nor the theme cards show them, and they last as long as TwelveIsland runs. Each draft's palette, which the theme page
    // shows, is kept until the draft or the theme's file changes.
    private static readonly Dictionary<string, PanelThemeDraft> _drafts = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, (PanelThemeFile File, int Version, PanelPalette Palette)> _draftPalettes = new(StringComparer.Ordinal);
    // Every token's text form and the default app colors as drawn, made once per theme: they are read hundreds of times per render.
    private static Dictionary<string, string> _hex = Hexes(Current);
    private static readonly Dictionary<ProviderId, string> _appColors = [];
    private static string? _warningColor, _criticalColor;
    // Colors chosen outside the theme, as drawn on it. Every new cover color adds an entry, so a resident app empties them now and then.
    private static readonly Dictionary<(string, double), string> _readable = [];
    private static readonly Dictionary<(Color, double), Color> _readableColors = [];

    // Reads the folder again: at start, and from the settings page when it is built, on request and when a file changes.
    // A folder that cannot be read leaves only the built-in themes; it must never keep TwelveIsland from starting.
    public static void Reload()
    {
        try { UserThemes = PanelThemes.LoadFolder(Folder); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { UserThemes = []; }
        _loaded = true;
    }

    // The theme an id stands for now, "system" following the Windows app theme.
    public static PanelThemeInfo Find(string id) => PanelThemes.Find(id, PanelThemes.NormalizeId(id) == PanelThemes.SystemId && SettingsPalette.IsDark(SettingsTheme.System), UserThemes);

    // Resolves the theme the settings choose and, when it differs from the one in use, makes it current. Returns whether it changed.
    // Theme files change only through a reload, so the same choice resolves to the same palette unless Windows has switched
    // between light and dark (systemChanged) while "system" is chosen.
    public static bool Use(DeckSettings settings, bool reload = false, bool systemChanged = false)
    {
        var chosen = PanelThemes.NormalizeId(settings.PanelTheme);
        // The hour reaching the other look, or new hours for it, counts as a change too.
        bool high = SystemParameters.HighContrast;
        if (!reload && !systemChanged && _loaded && chosen == _chosen && PanelThemes.IsNight(ShownTheme.File, DateTime.Now, settings.ThemeDaylight) == Night && high == HighContrast) return false;
        if (reload || !_loaded) Reload();
        _chosen = chosen;
        var theme = Find(chosen);
        // A theme file that is being saved can be locked or half written for a moment. While the chosen file theme is listed
        // but unusable, the last good version stays on screen instead of the dark fallback; the settings page shows the error.
        bool keepLastGood = theme.Id != chosen && !ShownTheme.BuiltIn && ShownTheme.Id == chosen
            && UserThemes.Any(t => !t.Usable && string.Equals(t.Path, ShownTheme.Path, StringComparison.OrdinalIgnoreCase));
        KeptLastGood = keepLastGood;
        if (keepLastGood) return false;
        // A theme with a night look shows it by the local clock (PanelThemes.IsNight).
        bool night = PanelThemes.IsNight(theme.File, DateTime.Now, settings.ThemeDaylight);
        // The same theme and look as now (the common case for "system") needs no new palette.
        // Under high contrast the system's colors may have changed with the same theme chosen, so its palette is always made anew.
        if (!reload && !high && !HighContrast && ReferenceEquals(theme, ShownTheme) && night == Night && _signature.Length > 0) return false;
        ShownTheme = theme; Night = night; HighContrast = high;
        var palette = high ? SystemPalette() : PanelThemes.Resolve(theme, night);
        var signature = Signature(theme, palette);
        if (signature == _signature) return false;
        // Panels on screen take a picture of their old look now, before any brush changes (MainWindow.ThemeFade).
        Changing?.Invoke(Current.Id == palette.Id && Current.IsNight != palette.IsNight);
        _signature = signature; Current = palette; Generation++;
        _hex = Hexes(palette); _appColors.Clear(); _warningColor = _criticalColor = null;
        _readable.Clear(); _readableColors.Clear();
        Publish(Application.Current?.Resources);
        return true;
    }

    private static PanelPalette SystemPalette()
    {
        static ThemeColor C(Color color) => new(color.A, color.R, color.G, color.B);
        return PanelThemes.HighContrast(C(SystemColors.WindowColor), C(SystemColors.WindowTextColor), C(SystemColors.GrayTextColor),
            C(SystemColors.HighlightColor), C(SystemColors.HighlightTextColor));
    }

    // Everything a palette draws, by value: a file theme is built anew for every reading, so an edit shows as a new signature.
    private static string Signature(PanelThemeInfo theme, PanelPalette palette)
    {
        var e = palette.Effects;
        var clock = palette.Clock;
        return string.Join("|", theme.Id, palette.IsNight, string.Join(",", palette.Colors.Values), string.Join(",", palette.Apps.Select(pair => pair.Key + pair.Value)),
            string.Join(",", e.SurfaceGradient), string.Join(",", e.RimGradient), FormattableString.Invariant($"{e.RimWidth}/{e.ShadowBlur}/{e.ShadowDepth}/{e.ShadowOpacity}/{e.Scanlines}/{e.Pixel}/{e.Texture}") + DecorKey(e.Decor),
            $"{clock?.Style}/{clock?.Color}/{clock?.Custom}/{string.Join(",", clock?.Gradient ?? [])}",
            string.Join(",", ClockColors.Swatches.Select(palette.ClockPreset)), string.Join(";", palette.ClockGradients.Select(g => g.Name + g.NameEn + string.Join(",", g.Stops))),
            string.Join(",", palette.Swatches));
    }
    private static string DecorKey(PanelDecor? decor) => decor is null ? "" : FormattableString.Invariant(
        $"/{decor.Horizon?.At}/{decor.Horizon?.Sea}/{decor.Horizon?.Line}/{decor.Caustics}/{decor.Palm}");
    private static Dictionary<string, string> Hexes(PanelPalette palette) => palette.Colors.ToDictionary(pair => pair.Key, pair => pair.Value.ToString());

    // A theme as the theme page edits it: its file with the unsaved changes made to it, if any.
    public static PanelPalette PaletteOf(PanelThemeInfo theme)
    {
        if (DraftOf(theme.Id) is not { } draft) return PanelThemes.Resolve(theme);
        if (_draftPalettes.TryGetValue(theme.Id, out var kept) && ReferenceEquals(kept.File, theme.File) && kept.Version == draft.Version) return kept.Palette;
        var palette = PanelThemes.Build(theme.Id, PanelThemes.ApplyDraft(theme.File, draft));
        _draftPalettes[theme.Id] = (theme.File, draft.Version, palette);
        return palette;
    }
    public static PanelThemeDraft? DraftOf(string id) => _drafts.TryGetValue(id, out var draft) && !draft.IsEmpty ? draft : null;
    // Changes the theme's draft, starting one if there is none; a draft left without changes is let go.
    public static void Edit(PanelThemeInfo theme, Action<PanelThemeDraft> edit)
    {
        if (!_drafts.TryGetValue(theme.Id, out var draft)) _drafts[theme.Id] = draft = new PanelThemeDraft(theme.Id);
        edit(draft);
        if (draft.IsEmpty) Discard(theme.Id);
    }
    // Drops the theme's unsaved changes: they were saved, given up, or the theme was deleted.
    public static void Discard(string id)
    {
        _drafts.Remove(id); _draftPalettes.Remove(id);
    }

    public static string Hex(string key) => _hex[key];
    public static Color ColorOf(string key) => ToColor(Current[key]);
    public static Color ToColor(ThemeColor color) => Color.FromArgb(color.A, color.R, color.G, color.B);

    // XAML styles refer to these keys with DynamicResource, so a change of theme repaints them in place.
    private static readonly (string Resource, string Token)[] Resources =
    [
        ("PanelSurface", "surface"), ("PanelPrimary", "textPrimary"), ("PanelSecondary", "textSecondary"), ("PanelTertiary", "textTertiary"),
        ("PanelAccent", "accent"), ("PanelTrack", "track"), ("PanelRingTrack", "ringTrack"), ("PanelFill", "fill"), ("PanelFillSelected", "fillSelected"),
        ("PanelHover", "hover"), ("PanelPressed", "pressed"), ("PanelPopup", "popup"), ("PanelPopupBorder", "popupBorder"), ("PanelScrollThumb", "scrollThumb"),
        ("PanelSwitchFill", "switchFill"), ("PanelSeekRail", "seekRail"), ("PanelToolTip", "tooltip"), ("PanelToolTipBorder", "tooltipBorder"),
        ("PanelBubble", "bubble"), ("PanelBubbleBorder", "bubbleBorder")
    ];
    // The brushes go into one dictionary that replaces the previous one among the merged dictionaries (App.xaml holds the first),
    // so a change of theme invalidates the windows' resource references once instead of once per brush.
    private static ResourceDictionary? _published;
    private static void Publish(ResourceDictionary? resources)
    {
        if (resources is null) return;
        var next = new ResourceDictionary();
        foreach (var (resource, token) in Resources) next[resource] = UI.Brush(Hex(token));
        next["PanelScrollThumbRest"] = Current.ThumbRestOpacity;
        // Corners in the panel's XAML (App.xaml has the defaults): square in a pixel theme.
        for (int r = 1; r <= 16; r++) next["PanelR" + r] = new CornerRadius(Corner(r));
        next["SegmentCorner"] = new CornerRadius(Corner(5));
        var merged = resources.MergedDictionaries;
        int at = _published is not null ? merged.IndexOf(_published) : -1;
        if (at < 0) for (int i = 0; i < merged.Count && at < 0; i++) if (merged[i].Contains("PanelSurface")) at = i;
        if (at >= 0) merged[at] = next; else merged.Insert(0, next);
        _published = next;
    }

    // A color chosen outside the theme (an application's color, a clock color, the cover's color), as the panel draws it:
    // unchanged where it reads on the surface, otherwise moved just far enough. Results are kept until the theme changes.
    public static string Readable(string color, double minimum = PanelThemes.GraphicContrast)
    {
        if (_readable.TryGetValue((color, minimum), out var known)) return known;
        if (_readable.Count > 256) _readable.Clear();
        return _readable[(color, minimum)] = ThemeColor.TryParse(color, out var parsed) ? Current.Readable(parsed, minimum).ToString() : color;
    }
    public static Color Readable(Color color, double minimum = PanelThemes.GraphicContrast)
    {
        if (_readableColors.TryGetValue((color, minimum), out var known)) return known;
        if (_readableColors.Count > 256) _readableColors.Clear();
        return _readableColors[(color, minimum)] = ToColor(Current.Readable(new ThemeColor(color.A, color.R, color.G, color.B), minimum));
    }

    // An application column's colors: the color chosen for it, else the theme's default for its source, as the panel draws them.
    // Under high contrast every application shows the system's colors (PanelThemes.HighContrast), whatever was chosen for it.
    public static string AppTheme(AppEntry entry) => entry.ThemeColor.Length > 0 && !HighContrast ? Readable(entry.ThemeColor) : DefaultAppColor(entry.QuotaSource);
    // The warning and critical colors also color the quota's number, so a chosen one is held to the text minimum like the
    // theme's (PanelThemes.LevelContrast).
    public static string AppWarning(AppEntry entry) => entry.WarningColor.Length > 0 && !HighContrast ? Readable(entry.WarningColor, PanelThemes.LevelContrast) : _warningColor ??= Readable(WarningDefault, PanelThemes.LevelContrast);
    public static string AppCritical(AppEntry entry) => entry.CriticalColor.Length > 0 && !HighContrast ? Readable(entry.CriticalColor, PanelThemes.LevelContrast) : _criticalColor ??= Readable(CriticalDefault, PanelThemes.LevelContrast);
    // A quota's color below the warning level: the off-peak color, when one is chosen, while the row is in one of its off-peak periods.
    public static string QuotaTheme(AppEntry entry) => entry.OffPeakColor.Length > 0 && !HighContrast && entry.IsOffPeak(DateTime.Now) ? Readable(entry.OffPeakColor) : AppTheme(entry);
    // At the warning and critical levels, an off-peak color chosen for that level replaces the usual one.
    public static string QuotaColor(AppEntry entry, double usedPercent)
    {
        if (usedPercent < AppPresets.WarningPercent) return QuotaTheme(entry);
        bool critical = usedPercent >= AppPresets.CriticalPercent;
        string offPeak = critical ? entry.OffPeakCriticalColor : entry.OffPeakWarningColor;
        if (offPeak.Length > 0 && !HighContrast && entry.IsOffPeak(DateTime.Now)) return Readable(offPeak, PanelThemes.LevelContrast);
        return critical ? AppCritical(entry) : AppWarning(entry);
    }
    public static string DefaultAppColor(ProviderId id) => _appColors.TryGetValue(id, out var color) ? color : _appColors[id] = Current.AppColor(id).ToString();
    // The theme's defaults as written, for the settings swatches and the color picker.
    public static string AppDefault(ProviderId id) => Current.AppDefault(id)?.ToString() ?? AppPresets.ThemeColor(id);
    public static string WarningDefault => Current.WarningDefault?.ToString() ?? AppPresets.WarningColor;
    public static string CriticalDefault => Current.CriticalDefault?.ToString() ?? AppPresets.CriticalColor;

    // Gives an island the theme's layers over its base color: the gradient, the motifs, the scan lines or texture, the rim and a pixel
    // theme's steps and bevel. The base color is the caller's, as the panel animates it between the surface and the indicator.
    public static void Dress(IslandBorder island, PanelPalette? palette = null, double dpiScale = 1)
    {
        palette ??= Current;
        island.Gradient = SurfaceGradient(palette); island.Pattern = Texture(palette, dpiScale) ?? Scanlines(palette, dpiScale);
        island.Decor = IslandDecor.For(palette);
        island.RimBrush = RimBrush(palette); island.RimWidth = palette.Effects.RimWidth;
        island.Pixel = palette.Effects.Pixel; island.BevelBrush = BevelBrush(palette);
    }

    // The text of a window of the panel's own (the date picker, the app filter): unsmoothed in a pixel theme, as on the island.
    public static void Text(FrameworkElement root)
    {
        if (Current.Effects.Pixel > 0) TextOptions.SetTextRenderingMode(root, TextRenderingMode.Aliased);
        else root.ClearValue(TextOptions.TextRenderingModeProperty);
    }
    // A menu or tooltip takes the text rendering of what it opens from (App.OnStartup): unsmoothed over a pixel theme's panel,
    // smoothed over the settings, which follow Windows rather than the panel theme, and over the tray.
    public static void Text(FrameworkElement popup, DependencyObject? from)
    {
        if (from is not null && TextOptions.GetTextRenderingMode(from) == TextRenderingMode.Aliased) TextOptions.SetTextRenderingMode(popup, TextRenderingMode.Aliased);
        else popup.ClearValue(TextOptions.TextRenderingModeProperty);
    }

    // A corner of the panel's own drawing as the theme draws it: square in a pixel theme, where the island's steps are the only
    // curve, and as given otherwise.
    public static double Corner(double radius) => Current.Effects.Pixel > 0 ? 0 : radius;

    // A pixel theme's bevel: the rim's last gradient color shades the edges that face down and right (IslandBorder.Pixel).
    public static Brush? BevelBrush(PanelPalette? palette = null)
    {
        var effects = (palette ?? Current).Effects;
        if (effects.Pixel <= 0 || effects.RimGradient.Count < 2) return null;
        var brush = new SolidColorBrush(ToColor(effects.RimGradient[^1]));
        brush.Freeze();
        return brush;
    }
    // The shadow under the collapsed surface, or the glow of a theme that gives it no depth.
    public static void Dress(DropShadowEffect shadow, PanelPalette? palette = null)
    {
        palette ??= Current;
        var effects = palette.Effects;
        shadow.Color = ToColor(palette["shadow"]); shadow.BlurRadius = effects.ShadowBlur; shadow.ShadowDepth = effects.ShadowDepth; shadow.Opacity = effects.ShadowOpacity;
    }

    // The surface's look beyond its base color: a gradient over the whole surface and scan lines tiled across it.
    public static Brush? SurfaceGradient(PanelPalette? palette = null)
    {
        var stops = (palette ?? Current).Effects.SurfaceGradient;
        if (stops.Count < 2) return null;
        var brush = Spread(stops, new Point(0, 1));
        brush.Freeze();
        return brush;
    }
    public static Brush? Scanlines(PanelPalette? palette = null, double dpiScale = 1)
    {
        palette ??= Current;
        double opacity = palette.Effects.Scanlines;
        if (opacity <= 0) return null;
        // A line one device pixel (or more, at high scales) thick about every 3 DIP, in whole device pixels and absolute units,
        // so the lines stay sharp and evenly spaced at any scale and panel size.
        if (!(dpiScale > 0) || !double.IsFinite(dpiScale)) dpiScale = 1;
        double pitch = Math.Max(2, Math.Round(3 * dpiScale)) / dpiScale, thickness = Math.Max(1, Math.Round(dpiScale)) / dpiScale;
        var line = ToColor(palette["textPrimary"]); line.A = (byte)Math.Round(255 * opacity);
        var tile = new DrawingGroup();
        tile.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, pitch, pitch))));
        tile.Children.Add(new GeometryDrawing(new SolidColorBrush(line), null, new RectangleGeometry(new Rect(0, 0, pitch, thickness))));
        var brush = new DrawingBrush(tile) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, pitch, pitch), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.None };
        // The tile never changes, so WPF may keep it realized instead of drawing it again for every frame.
        RenderOptions.SetCachingHint(brush, CachingHint.Cache);
        brush.Freeze();
        return brush;
    }
    // A pixel theme's grain: an 8 x 8 tile of squares the pixel size (in whole device pixels), each a little lighter or darker
    // than the surface or left plain, in a fixed scatter so the tile does not read as a grid. Cached like the scan lines.
    public static Brush? Texture(PanelPalette? palette = null, double dpiScale = 1)
    {
        palette ??= Current;
        var effects = palette.Effects;
        if (effects.Texture <= 0 || effects.Pixel <= 0) return null;
        double cell = PixelStairs.Step(effects.Pixel, dpiScale), size = 8 * cell;
        var light = ToColor(palette["textPrimary"]);
        var tile = new DrawingGroup();
        tile.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, size, size))));
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                // A fixed hash per square: a third plain, the rest lighter or darker in up to the texture's opacity (the lightest
                // and darkest are held to the contrast rules, PanelThemes.TextureStops).
                uint h = (uint)(x * 73856093 ^ y * 19349663) * 2654435761u;
                int kind = (int)(h >> 29);
                if (kind < 3) continue;
                byte alpha = (byte)Math.Round(255 * effects.Texture * ((h >> 20 & 3) + 1) / 4);
                var color = kind < 6 ? Color.FromArgb(alpha, light.R, light.G, light.B) : Color.FromArgb(alpha, 0, 0, 0);
                tile.Children.Add(new GeometryDrawing(new SolidColorBrush(color), null, new RectangleGeometry(new Rect(x * cell, y * cell, cell, cell))));
            }
        var brush = new DrawingBrush(tile) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, size, size), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.None };
        RenderOptions.SetCachingHint(brush, CachingHint.Cache);
        brush.Freeze();
        return brush;
    }
    public static Brush RimBrush(PanelPalette? palette = null)
    {
        palette ??= Current;
        var effects = palette.Effects;
        // A pixel theme's bevel lights its upper and left edges in the gradient's first color and shades the others in the last.
        Brush brush = effects.Pixel > 0 && effects.RimGradient.Count >= 2 ? new SolidColorBrush(ToColor(effects.RimGradient[0]))
            : effects.RimGradient.Count >= 2 ? Spread(effects.RimGradient, new Point(1, 0)) : new SolidColorBrush(ToColor(palette["rim"]));
        brush.Freeze();
        return brush;
    }
    // Colors spread evenly from the element's top left toward the given end point.
    private static LinearGradientBrush Spread(IReadOnlyList<ThemeColor> stops, Point end)
        => new(new GradientStopCollection(stops.Select((stop, i) => new GradientStop(ToColor(stop), (double)i / (stops.Count - 1)))), new Point(0, 0), end);
    // The text marquees draw their edge fades in the surface color; over a gradient or scan lines that color would show as a band,
    // so there they fade the text itself instead (MusicMarquee.InkFade).
    public static Color? MarqueeBackdrop => Current.HasOverlay ? null : ColorOf("surface");
}

// Windows' own accent color: the equalizer's default follows the system instead of a fixed swatch. Read on each use, so a
// change to the system theme shows up on the next drawing. WPF's glass color is the usual source; DWM's colorization value
// is the fallback for setups where it is not given, and the skin's own text color is the last resort (high contrast, or a
// system with no accent color).
internal static class SystemInk
{
    public static Color Ink
    {
        get
        {
            // DWM's colorization value is the accent color Windows itself uses (AARRGGBB). It is the first choice because
            // WPF's glass color can be a near-white glass tint on some setups rather than the accent.
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
                if (key?.GetValue("ColorizationColor") is int raw)
                {
                    uint value = unchecked((uint)raw);
                    byte r = (byte)(value >> 16), g = (byte)(value >> 8), b = (byte)value;
                    if (r + g + b > 60) return Color.FromRgb(r, g, b);
                }
            }
            catch { }
            try
            {
                var glass = SystemParameters.WindowGlassColor;
                if (glass.A > 0 && glass.R + glass.G + glass.B > 60) return glass;
            }
            catch { }
            return ClockFace.Parse(PanelTheme.Hex("textPrimary"));
        }
    }

    public static Brush Fill
    {
        get { var brush = new SolidColorBrush(Ink); brush.Freeze(); return brush; }
    }
}
