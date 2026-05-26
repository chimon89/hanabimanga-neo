using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using hanabimanga.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace hanabimanga.Services
{
    /// <summary>
    /// 应用八种外观配色。强调色资源直接用 skill 给定的 hex 派生;其余主题资源按
    /// 「所选强调色与参考强调色的色相差」整体做色相旋转,实现强调色 + 背景退色。
    /// </summary>
    public sealed class ThemeColorService
    {
        public static ThemeColorService Instance { get; } = new();

        public const string DefaultId = "sakura";

        private static readonly HashSet<string> AccentKeys = new(StringComparer.Ordinal)
        {
            "SystemAccentColor",
            "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3",
            "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3",
            "AccentFillColorDefaultBrush", "AccentFillColorSecondaryBrush",
            "AccentFillColorTertiaryBrush", "AccentFillColorDisabledBrush",
            "AccentFillColorSelectedTextBackgroundBrush",
            "TextOnAccentFillColorPrimaryBrush", "TextOnAccentFillColorSecondaryBrush",
            "TextOnAccentFillColorDisabledBrush",
            "AccentTextFillColorPrimaryBrush", "AccentTextFillColorSecondaryBrush",
            "AccentTextFillColorTertiaryBrush", "AccentTextFillColorDisabledBrush",
            "NavigationViewItemBackgroundSelected", "NavigationViewItemBackgroundSelectedPointerOver",
            "NavigationViewSelectionIndicatorForeground",
            "TextControlBorderBrushFocused", "TextControlElevationBorderFocusedBrush",
            "AccentControlElevationBorderBrush",
            "AccentButtonBackground", "AccentButtonBackgroundPointerOver",
            "AccentButtonBackgroundPressed", "AccentButtonBackgroundDisabled",
            "AccentButtonForeground", "AccentButtonForegroundPointerOver",
            "AccentButtonForegroundPressed", "AccentButtonForegroundDisabled",
            "AccentButtonBorderBrush", "AccentButtonBorderBrushPointerOver",
            "AccentButtonBorderBrushPressed", "AccentButtonBorderBrushDisabled",
        };

        private static readonly HashSet<string> FrameworkAccentKeys = new(StringComparer.Ordinal)
        {
            "AccentButtonBackground", "AccentButtonBackgroundPointerOver",
            "AccentButtonBackgroundPressed", "AccentButtonBackgroundDisabled",
            "AccentButtonForeground", "AccentButtonForegroundPointerOver",
            "AccentButtonForegroundPressed", "AccentButtonForegroundDisabled",
            "AccentButtonBorderBrush", "AccentButtonBorderBrushPointerOver",
            "AccentButtonBorderBrushPressed", "AccentButtonBorderBrushDisabled",
        };

        private readonly Dictionary<string, Color> _solidSnapshot = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Color>> _gradientSnapshot = new(StringComparer.Ordinal);
        private bool _snapshotTaken;

        private ThemeColorService()
        {
        }

        public IReadOnlyList<ThemeColorOption> Options { get; } = new[]
        {
            new ThemeColorOption("sakura", "樱花粉", 0xFF9EAFu, 0xFFB4C2u),
            new ThemeColorOption("tangerine", "蜜橘橙", 0xFF8A50u, 0xFFA26Eu),
            new ThemeColorOption("ocean", "深海蓝", 0x4A90D9u, 0x58A6FFu),
            new ThemeColorOption("mint", "薄荷青", 0x4ECDC4u, 0x6EDDD6u),
            new ThemeColorOption("violet", "罗兰紫", 0x9B8DFFu, 0xB4A7FFu),
            new ThemeColorOption("matcha", "抹茶绿", 0x7CB342u, 0x9CCC65u),
            new ThemeColorOption("coral", "赤霞橘", 0xFF6B6Bu, 0xFF8A80u),
            new ThemeColorOption("whale", "鲸鱼蓝", 0x0066CCu, 0x3B9EFFu),
        };

        public ThemeColorOption Resolve(string? id)
            => Options.FirstOrDefault(option => string.Equals(option.Id, id, StringComparison.Ordinal))
               ?? Options.First(option => option.Id == DefaultId);

        public void Apply(string? colorId)
        {
            var resources = Application.Current?.Resources;
            var themeDicts = resources?.ThemeDictionaries;
            if (resources == null || themeDicts == null) return;

            EnsureSnapshot(themeDicts);

            var option = Resolve(colorId);
            ApplyToTheme(
                themeDicts,
                "Light",
                ToColor(option.LightArgb),
                isDark: false,
                rotateNonAccent: true,
                replaceColorResources: true);
            ApplyToTheme(
                themeDicts,
                "Dark",
                ToColor(option.DarkArgb),
                isDark: true,
                rotateNonAccent: true,
                replaceColorResources: true);
            ApplyToTheme(
                themeDicts,
                "Default",
                ToColor(option.DarkArgb),
                isDark: true,
                rotateNonAccent: false,
                replaceColorResources: true);
            ApplyToMergedDictionaries(resources.MergedDictionaries, option);

            RefreshThemeResources();
        }

        /// <summary>
        /// 强制整棵可视化树重新解析 ThemeResource。直接修改画刷 Color 对模板默认状态实时生效,
        /// 但 VisualState(聚焦/悬停)与 elevation 渐变需要一次主题重解析才会刷新。
        /// 在 RequestedTheme 与系统主题一致的前提下 Default↔显式 来回切,既触发重解析又不闪烁。
        /// </summary>
        private static void RefreshThemeResources()
        {
            if (App.MainWindow?.Content is not FrameworkElement root) return;

            var explicitTheme = root.ActualTheme == ElementTheme.Dark
                ? ElementTheme.Dark
                : ElementTheme.Light;
            root.RequestedTheme = explicitTheme;
            root.DispatcherQueue?.TryEnqueue(() => root.RequestedTheme = ElementTheme.Default);
        }

        private void EnsureSnapshot(IDictionary<object, object> themeDicts)
        {
            if (_snapshotTaken) return;

            foreach (var theme in new[] { "Light", "Dark" })
            {
                if (!themeDicts.TryGetValue(theme, out var obj) || obj is not ResourceDictionary dict)
                {
                    continue;
                }

                foreach (var entry in dict)
                {
                    if (entry.Key is not string key) continue;
                    switch (entry.Value)
                    {
                        case SolidColorBrush brush:
                            _solidSnapshot[$"{theme}|{key}"] = brush.Color;
                            break;
                        case LinearGradientBrush gradient:
                            _gradientSnapshot[$"{theme}|{key}"] =
                                gradient.GradientStops.Select(stop => stop.Color).ToList();
                            break;
                    }
                }
            }

            _snapshotTaken = true;
        }

        private void ApplyToMergedDictionaries(IEnumerable<ResourceDictionary> dictionaries, ThemeColorOption option)
        {
            foreach (var dictionary in dictionaries)
            {
                ApplyToFrameworkTheme(dictionary.ThemeDictionaries, "Light", ToColor(option.LightArgb), isDark: false);
                ApplyToFrameworkTheme(dictionary.ThemeDictionaries, "Dark", ToColor(option.DarkArgb), isDark: true);
                ApplyToFrameworkTheme(dictionary.ThemeDictionaries, "Default", ToColor(option.DarkArgb), isDark: true);
                ApplyToMergedDictionaries(dictionary.MergedDictionaries, option);
            }
        }

        private static void ApplyToFrameworkTheme(
            IDictionary<object, object> themeDicts,
            string theme,
            Color accent,
            bool isDark)
        {
            if (!themeDicts.TryGetValue(theme, out var obj) || obj is not ResourceDictionary dict)
            {
                return;
            }

            foreach (var entry in dict)
            {
                if (entry.Key is not string key || !FrameworkAccentKeys.Contains(key))
                {
                    continue;
                }

                if (entry.Value is SolidColorBrush brush)
                {
                    TrySetBrushColor(brush, DeriveAccentBrushColor(key, accent, isDark));
                }
            }
        }

        private void ApplyToTheme(
            IDictionary<object, object> themeDicts,
            string theme,
            Color accent,
            bool isDark,
            bool rotateNonAccent,
            bool replaceColorResources)
        {
            if (!themeDicts.TryGetValue(theme, out var obj) || obj is not ResourceDictionary dict)
            {
                return;
            }

            var referenceAccent = _solidSnapshot.TryGetValue($"{theme}|AccentFillColorDefaultBrush", out var refColor)
                ? refColor
                : accent;
            var hueDelta = Hue(accent) - Hue(referenceAccent);

            var colorReplacements = new List<(object Key, Color Value)>();
            foreach (var entry in dict)
            {
                if (entry.Key is not string key) continue;
                switch (entry.Value)
                {
                    case SolidColorBrush brush:
                        if (AccentKeys.Contains(key))
                        {
                            TrySetBrushColor(brush, DeriveAccentBrushColor(key, accent, isDark));
                        }
                        else if (rotateNonAccent && _solidSnapshot.TryGetValue($"{theme}|{key}", out var original))
                        {
                            TrySetBrushColor(brush, RotateHue(original, hueDelta));
                        }
                        break;
                    case LinearGradientBrush gradient:
                        if (rotateNonAccent && _gradientSnapshot.TryGetValue($"{theme}|{key}", out var stops))
                        {
                            for (var i = 0; i < gradient.GradientStops.Count && i < stops.Count; i++)
                            {
                                TrySetGradientStopColor(gradient.GradientStops[i], RotateHue(stops[i], hueDelta));
                            }
                        }
                        break;
                    case Color when replaceColorResources && AccentKeys.Contains(key):
                        colorReplacements.Add((entry.Key, DeriveAccentColorValue(key, accent, isDark)));
                        break;
                }
            }

            foreach (var (key, value) in colorReplacements)
            {
                try
                {
                    dict[key] = value;
                }
                catch (COMException)
                {
                    // XamlControlsResources can expose protected dictionaries; skip those entries.
                }
            }
        }

        private static void TrySetBrushColor(SolidColorBrush brush, Color color)
        {
            try
            {
                brush.Color = color;
            }
            catch (COMException)
            {
                // Some WinUI framework resources are immutable at runtime.
            }
        }

        private static void TrySetGradientStopColor(GradientStop stop, Color color)
        {
            try
            {
                stop.Color = color;
            }
            catch (COMException)
            {
                // Some WinUI framework resources are immutable at runtime.
            }
        }

        private static Color DeriveAccentBrushColor(string key, Color accent, bool isDark) => key switch
        {
            "AccentFillColorSecondaryBrush" => Darken(accent, 0.12),
            "AccentFillColorTertiaryBrush" => WithAlpha(accent, 0x40),
            "AccentFillColorDisabledBrush" => WithAlpha(accent, 0x66),
            "TextOnAccentFillColorPrimaryBrush" => Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
            "TextOnAccentFillColorSecondaryBrush" => Color.FromArgb(0xFF, 0xF8, 0xF6, 0xFF),
            "TextOnAccentFillColorDisabledBrush" => Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF),
            "NavigationViewItemBackgroundSelected" => WithAlpha(accent, 0x30),
            "NavigationViewItemBackgroundSelectedPointerOver" => WithAlpha(accent, 0x3D),
            "AccentButtonBackground" => accent,
            "AccentButtonBackgroundPointerOver" => isDark ? MixWhite(accent, 0.12) : MixWhite(accent, 0.16),
            "AccentButtonBackgroundPressed" => isDark ? MixBlack(accent, 0.10) : MixBlack(accent, 0.16),
            "AccentButtonBackgroundDisabled" => WithAlpha(accent, 0x66),
            "AccentButtonForeground" => Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
            "AccentButtonForegroundPointerOver" => Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
            "AccentButtonForegroundPressed" => Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
            "AccentButtonForegroundDisabled" => Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF),
            "AccentButtonBorderBrush" => accent,
            "AccentButtonBorderBrushPointerOver" => isDark ? MixWhite(accent, 0.12) : MixWhite(accent, 0.16),
            "AccentButtonBorderBrushPressed" => isDark ? MixBlack(accent, 0.10) : MixBlack(accent, 0.16),
            "AccentButtonBorderBrushDisabled" => WithAlpha(accent, 0x66),
            // 强调色文本需在背景上有足够对比:浅色主题压暗,深色主题提亮。
            "AccentTextFillColorPrimaryBrush" => isDark ? MixWhite(accent, 0.70) : MixBlack(accent, 0.42),
            "AccentTextFillColorSecondaryBrush" => isDark ? MixWhite(accent, 0.70) : MixBlack(accent, 0.58),
            "AccentTextFillColorTertiaryBrush" => isDark ? MixWhite(accent, 0.45) : MixBlack(accent, 0.22),
            "AccentTextFillColorDisabledBrush" => WithAlpha(
                isDark ? MixWhite(accent, 0.70) : MixBlack(accent, 0.42), 0x90),
            _ => accent,
        };

        private static Color DeriveAccentColorValue(string key, Color accent, bool isDark) => key switch
        {
            "SystemAccentColorLight1" => MixWhite(accent, 0.20),
            "SystemAccentColorLight2" => MixWhite(accent, 0.45),
            "SystemAccentColorLight3" => MixWhite(accent, 0.70),
            "SystemAccentColorDark1" => MixBlack(accent, 0.22),
            "SystemAccentColorDark2" => MixBlack(accent, 0.42),
            "SystemAccentColorDark3" => MixBlack(accent, 0.62),
            _ => DeriveAccentBrushColor(key, accent, isDark),
        };

        private static Color ToColor(uint argb) => Color.FromArgb(
            (byte)((argb >> 24) & 0xFF),
            (byte)((argb >> 16) & 0xFF),
            (byte)((argb >> 8) & 0xFF),
            (byte)(argb & 0xFF));

        private static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

        private static Color Darken(Color c, double fraction)
        {
            var factor = 1.0 - fraction;
            return Color.FromArgb(c.A,
                ClampByte(c.R * factor), ClampByte(c.G * factor), ClampByte(c.B * factor));
        }

        private static Color MixWhite(Color c, double t) => Color.FromArgb(c.A,
            ClampByte(c.R + (255 - c.R) * t),
            ClampByte(c.G + (255 - c.G) * t),
            ClampByte(c.B + (255 - c.B) * t));

        private static Color MixBlack(Color c, double t) => Color.FromArgb(c.A,
            ClampByte(c.R * (1 - t)), ClampByte(c.G * (1 - t)), ClampByte(c.B * (1 - t)));

        private static Color RotateHue(Color c, double deltaDegrees)
        {
            if (c.A == 0) return c;
            var (h, s, l) = RgbToHsl(c);
            if (s < 0.01) return c;
            return HslToRgb(h + deltaDegrees, s, l, c.A);
        }

        private static double Hue(Color c) => RgbToHsl(c).H;

        private static (double H, double S, double L) RgbToHsl(Color c)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            var l = (max + min) / 2.0;
            var delta = max - min;
            if (delta < 1e-9)
            {
                return (0, 0, l);
            }

            var s = l > 0.5 ? delta / (2.0 - max - min) : delta / (max + min);
            double h;
            if (max == r) h = (g - b) / delta + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / delta + 2;
            else h = (r - g) / delta + 4;
            return (h * 60.0, s, l);
        }

        private static Color HslToRgb(double h, double s, double l, byte a)
        {
            h = ((h % 360) + 360) % 360;
            var c = (1 - Math.Abs(2 * l - 1)) * s;
            var x = c * (1 - Math.Abs(h / 60.0 % 2 - 1));
            var m = l - c / 2;
            double r, g, b;
            if (h < 60) { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }
            return Color.FromArgb(a,
                ClampByte((r + m) * 255), ClampByte((g + m) * 255), ClampByte((b + m) * 255));
        }

        private static byte ClampByte(double value)
            => (byte)Math.Clamp(Math.Round(value), 0, 255);
    }
}
