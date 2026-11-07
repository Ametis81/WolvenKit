using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace WolvenKit.App.Services.Themes;

/// <summary>
/// Live recoloring of brushes that are already in use.
/// <para>
/// Most of the UI (Syncfusion control templates, styles in App.Styles.xaml, views) gets its brushes through
/// <c>StaticResource</c>, i.e. it holds a reference to one SolidColorBrush instance. Putting another brush under
/// the same key in a new dictionary does not reach those elements, but changing <c>Color</c> of the shared
/// instance does — immediately and without a restart. Only frozen brushes can't be changed this way.
/// </para>
/// </summary>
public sealed partial class ThemeService
{
    private sealed class OriginalColor
    {
        public Color Value { get; init; }
    }

    private const string SyncfusionGroup = "Surfaces";

    /// <summary>Color of every patched brush before any theme touched it.</summary>
    private readonly ConditionalWeakTable<SolidColorBrush, OriginalColor> _originalColors = new();

    /// <summary>Dictionaries whose own keys were already scanned.</summary>
    private readonly ConditionalWeakTable<ResourceDictionary, object> _scannedDictionaries = new();

    /// <summary>Resource key → mutable brush instances found under that key.</summary>
    private readonly Dictionary<string, List<WeakReference<SolidColorBrush>>> _liveBrushes = new(StringComparer.Ordinal);

    /// <summary>Keys that were found only as frozen brushes (can't be recolored live).</summary>
    private readonly HashSet<string> _frozenKeys = new(StringComparer.Ordinal);

    /// <summary>Keys to look for in resource dictionaries.</summary>
    private readonly HashSet<string> _patchKeys = new(StringComparer.Ordinal);

    private ThemeDefinition? _patchedTheme;
    private DispatcherTimer? _rescanTimer;
    private bool _summaryLogged;

    private void InitializeLivePatching()
    {
        foreach (var entry in Schema.Entries)
        {
            _patchKeys.Add(entry.Key);
            foreach (var derived in entry.DerivedBrushes ?? [])
            {
                _patchKeys.Add(derived);
            }
        }
        foreach (var (key, _) in s_mahAppsAccentKeys)
        {
            _patchKeys.Add(key);
        }

        // Syncfusion merges the dictionaries of a control type into the application resources the first time
        // such a control is created, so new brush instances keep appearing. Re-scan when new views load.
        _rescanTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
        _rescanTimer.Tick += (_, _) =>
        {
            _rescanTimer.Stop();
            RescanAndPatch();
        };

        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnElementLoaded));
        EventManager.RegisterClassHandler(typeof(UserControl), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnElementLoaded));
    }

    private void OnElementLoaded(object sender, RoutedEventArgs e)
    {
        if (_rescanTimer is null)
        {
            return;
        }
        _rescanTimer.Stop();
        _rescanTimer.Start();
    }

    private void RescanAndPatch()
    {
        var found = ScanAll();
        if (found > 0 && _patchedTheme is not null)
        {
            PatchLiveBrushes(_patchedTheme);
        }

        if (!_summaryLogged && Application.Current?.MainWindow is { IsLoaded: true })
        {
            _summaryLogged = true;
            LogPatchSummary();
        }
    }

    private void LogPatchSummary()
    {
        var instances = _liveBrushes.Values.Sum(l => l.Count);
        _loggerService.Info($"Themes: {instances} live brushes under {_liveBrushes.Count} keys can be recolored without restart.");

        var frozenOnly = _frozenKeys.Where(k => !_liveBrushes.ContainsKey(k)).OrderBy(k => k).ToList();
        if (frozenOnly.Count > 0)
        {
            _loggerService.Info($"Themes: {frozenOnly.Count} keys only exist as frozen brushes (applied on restart only): {string.Join(", ", frozenOnly.Take(30))}{(frozenOnly.Count > 30 ? ", …" : "")}");
        }
    }

    /// <summary>Walks application and window resources; returns the number of newly found brush instances.</summary>
    private int ScanAll()
    {
        var app = Application.Current;
        if (app is null)
        {
            return 0;
        }

        var found = ScanDictionary(app.Resources, false);
        foreach (Window window in app.Windows)
        {
            found += ScanDictionary(window.Resources, false);
        }
        return found;
    }

    private int ScanDictionary(ResourceDictionary dictionary, bool insideSyncfusionBrushes)
    {
        if (ReferenceEquals(dictionary, _appliedDictionary))
        {
            return 0;
        }

        var isSyncfusionBrushes = insideSyncfusionBrushes || IsSyncfusionBrushesProvider(dictionary);
        var found = 0;

        // Own keys of a dictionary don't change after loading, merged dictionaries do (Syncfusion adds them lazily).
        if (!_scannedDictionaries.TryGetValue(dictionary, out _))
        {
            _scannedDictionaries.Add(dictionary, new object());

            if (isSyncfusionBrushes)
            {
                found += DiscoverSyncfusionBrushes(dictionary);
            }

            foreach (var key in _patchKeys)
            {
                if (!dictionary.Contains(key))
                {
                    continue;
                }
                object? value;
                try
                {
                    value = dictionary[key];
                }
                catch (Exception)
                {
                    continue; // broken resource, see CaptureDefaults
                }
                if (value is SolidColorBrush brush && Register(key, brush))
                {
                    found++;
                }
            }
        }

        foreach (var merged in dictionary.MergedDictionaries.ToList())
        {
            found += ScanDictionary(merged, isSyncfusionBrushes);
        }

        return found;
    }

    /// <summary>
    /// Syncfusion's "Brushes" dictionary of the theme. Its keys are not known at compile time,
    /// so they are added to the schema (group "Surfaces") when found.
    /// </summary>
    private static bool IsSyncfusionBrushesProvider(ResourceDictionary dictionary)
    {
        var type = dictionary.GetType();
        if (type.Name != "ResourceDictionaryProvider")
        {
            return false;
        }
        return type.GetProperty("DictionaryName")?.GetValue(dictionary) is string name
               && string.Equals(name, "Brushes", StringComparison.OrdinalIgnoreCase);
    }

    private int DiscoverSyncfusionBrushes(ResourceDictionary dictionary)
    {
        var found = 0;
        foreach (var key in dictionary.Keys.OfType<string>().ToList())
        {
            object? value;
            try
            {
                value = dictionary[key];
            }
            catch (Exception)
            {
                continue;
            }
            if (value is not SolidColorBrush brush)
            {
                continue;
            }

            if (_patchKeys.Add(key))
            {
                var original = _originalColors.TryGetValue(brush, out var o) ? o.Value : brush.Color;
                _defaults.TryAdd(key, original);
                _isBrush.TryAdd(key, true);
                if (Schema.Find(key) is null)
                {
                    Schema.AddEntry(new ThemeSchemaEntry
                    {
                        Key = key,
                        Group = SyncfusionGroup,
                        DisplayName = Humanize(key),
                        Kind = "Brush"
                    });
                }
            }

            if (Register(key, brush))
            {
                found++;
            }
        }
        return found;
    }

    /// <summary>Remembers a brush instance under a key. Returns true if it is new and mutable.</summary>
    private bool Register(string key, SolidColorBrush brush)
    {
        if (brush.IsFrozen)
        {
            _frozenKeys.Add(key);
            return false;
        }

        if (!_liveBrushes.TryGetValue(key, out var list))
        {
            list = [];
            _liveBrushes[key] = list;
        }
        else if (list.Any(r => r.TryGetTarget(out var b) && ReferenceEquals(b, brush)))
        {
            return false;
        }

        if (!_originalColors.TryGetValue(brush, out _))
        {
            _originalColors.Add(brush, new OriginalColor { Value = brush.Color });
        }
        list.Add(new WeakReference<SolidColorBrush>(brush));
        return true;
    }

    /// <summary>Brush key → target color for a theme (derived brushes follow their color key).</summary>
    private Dictionary<string, Color> BuildBrushColorMap(ThemeDefinition theme)
    {
        var overrides = ResolveOverrides(theme);
        var map = new Dictionary<string, Color>(overrides, StringComparer.Ordinal);

        foreach (var (key, color) in overrides)
        {
            foreach (var derived in Schema.Find(key)?.DerivedBrushes ?? [])
            {
                map.TryAdd(derived, color);
            }
        }

        var accent = ResolveAccent(theme);
        if (accent != IThemeService.DefaultAccent)
        {
            foreach (var (key, alpha) in s_mahAppsAccentKeys)
            {
                map[key] = Color.FromArgb(alpha, accent.R, accent.G, accent.B);
            }
        }

        return map;
    }

    /// <summary>Sets every known live brush to the theme color, or back to its original color.</summary>
    private void PatchLiveBrushes(ThemeDefinition theme)
    {
        _patchedTheme = theme;
        var map = BuildBrushColorMap(theme);

        foreach (var (key, list) in _liveBrushes)
        {
            var hasTarget = map.TryGetValue(key, out var target);
            list.RemoveAll(r => !r.TryGetTarget(out _));

            foreach (var reference in list)
            {
                if (!reference.TryGetTarget(out var brush) || brush.IsFrozen)
                {
                    continue;
                }
                var color = hasTarget
                    ? target
                    : _originalColors.TryGetValue(brush, out var original) ? original.Value : brush.Color;
                if (brush.Color != color)
                {
                    brush.Color = color;
                }
            }
        }
    }

    /// <summary>true if the key is fully handled by recoloring live brush instances.</summary>
    private bool IsLiveOnly(string key) => _liveBrushes.ContainsKey(key) && !_frozenKeys.Contains(key);

    private static string Humanize(string key) =>
        Regex.Replace(key.Replace('_', ' '), "(?<=[a-z0-9])(?=[A-Z])", " ").Trim();
}
