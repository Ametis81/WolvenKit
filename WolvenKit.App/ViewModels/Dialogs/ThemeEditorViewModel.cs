using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WolvenKit.App.Services.Themes;

namespace WolvenKit.App.ViewModels.Dialogs;

/// <summary>
/// One color row in the theme editor.
/// </summary>
public partial class ThemeColorEntryViewModel : ObservableObject
{
    public ThemeColorEntryViewModel(ThemeSchemaEntry entry, Color inherited, Color current)
    {
        Key = entry.Key;
        DisplayName = entry.DisplayName;
        Group = entry.Group;
        InheritedColor = inherited;
        Color = current;
    }

    public string Key { get; }

    public string DisplayName { get; }

    public string Group { get; }

    /// <summary>Value coming from the base theme (or the WolvenKit defaults).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverridden))]
    private Color _inheritedColor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverridden))]
    [NotifyPropertyChangedFor(nameof(Hex))]
    private Color _color;

    public bool IsOverridden => Color != InheritedColor;

    public string Hex
    {
        get => ThemeService.ToHex(Color);
        set
        {
            if (ThemeService.TryParseColor(value) is { } color)
            {
                Color = color;
            }
            else
            {
                OnPropertyChanged(); // revert the text box
            }
        }
    }

    [RelayCommand]
    private void Reset() => Color = InheritedColor;
}

/// <summary>
/// Edits a copy of a theme. Built-in themes are never modified: editing one creates a new user theme based on it.
/// </summary>
public partial class ThemeEditorViewModel : ObservableObject
{
    private static readonly Color s_defaultAccentForeground = Colors.White;

    private readonly IThemeService _themeService;

    private readonly bool _isInitializing;

    /// <summary>Name of the stored theme this editor overwrites; null for a new theme.</summary>
    private string? _storedName;

    private Color _inheritedAccent;
    private Color _inheritedAccentForeground;

    public ThemeEditorViewModel(IThemeService themeService, ThemeDefinition source)
    {
        _themeService = themeService;
        _isInitializing = true;

        IsNewTheme = source.IsBuiltIn;
        SourceName = source.Name;

        if (source.IsBuiltIn)
        {
            Name = themeService.GetUniqueName($"{source.Name} (copy)");
            BasedOn = source.Name;
        }
        else
        {
            _storedName = source.Name;
            Name = source.Name;
            BasedOn = source.BasedOn ?? IThemeService.DefaultThemeName;
            Author = source.Author;
            Description = source.Description;
        }

        var baseTheme = GetBaseTheme();
        _inheritedAccent = baseTheme is null ? IThemeService.DefaultAccent : themeService.ResolveAccent(baseTheme);
        _inheritedAccentForeground = (baseTheme is null ? null : themeService.ResolveAccentForeground(baseTheme)) ?? s_defaultAccentForeground;

        var ownAccent = source.IsBuiltIn ? null : ThemeService.TryParseColor(source.Accent);
        var ownAccentForeground = source.IsBuiltIn ? null : ThemeService.TryParseColor(source.AccentForeground);
        Accent = ownAccent ?? _inheritedAccent;
        AccentForeground = ownAccentForeground ?? _inheritedAccentForeground;

        foreach (var entry in themeService.Schema.Entries)
        {
            if (themeService.GetDefaultColor(entry.Key) is not { } defaultColor)
            {
                continue; // resource does not exist in this build
            }

            var inherited = baseTheme is null ? defaultColor : themeService.ResolveColor(baseTheme, entry.Key) ?? defaultColor;
            var own = !source.IsBuiltIn && source.Colors.TryGetValue(entry.Key, out var hex) ? ThemeService.TryParseColor(hex) : null;

            var vm = new ThemeColorEntryViewModel(entry, inherited, own ?? inherited);
            vm.PropertyChanged += OnEntryChanged;
            Entries.Add(vm);
        }

        BaseThemeNames = themeService.Themes
            .Where(t => !IsSelfOrDescendant(t))
            .Select(t => t.Name)
            .ToList();

        _isInitializing = false;
    }

    public event EventHandler? PaletteChanged;

    public ObservableCollection<ThemeColorEntryViewModel> Entries { get; } = [];

    public IReadOnlyList<string> BaseThemeNames { get; }

    /// <summary>true when the editor was opened on a built-in theme and will create a copy.</summary>
    public bool IsNewTheme { get; private set; }

    public string SourceName { get; }

    public string Title => IsNewTheme ? $"New theme based on \"{SourceName}\"" : $"Edit theme \"{_storedName}\"";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValidationError))]
    private string _name = "";

    [ObservableProperty]
    private string? _author;

    [ObservableProperty]
    private string? _description;

    [ObservableProperty]
    private string _basedOn = IThemeService.DefaultThemeName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAccentOverridden))]
    private Color _accent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAccentForegroundOverridden))]
    private Color _accentForeground;

    /// <summary>Also apply the edited palette to the whole application while editing.</summary>
    [ObservableProperty]
    private bool _previewInApp;

    [ObservableProperty]
    private string? _filterText;

    [ObservableProperty]
    private bool _showOnlyOverridden;

    [ObservableProperty]
    private bool _isDirty;

    public bool IsAccentOverridden => Accent != _inheritedAccent;

    public bool IsAccentForegroundOverridden => AccentForeground != _inheritedAccentForeground;

    public int OverriddenCount => Entries.Count(e => e.IsOverridden);

    public string? ValidationError
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Name))
            {
                return "Enter a theme name.";
            }
            var existing = _themeService.Find(Name.Trim());
            if (existing is not null && !string.Equals(existing.Name, _storedName, StringComparison.OrdinalIgnoreCase))
            {
                return existing.IsBuiltIn
                    ? $"\"{existing.Name}\" is a built-in theme name."
                    : $"A theme named \"{existing.Name}\" already exists.";
            }
            return null;
        }
    }

    public bool Matches(ThemeColorEntryViewModel entry)
    {
        if (ShowOnlyOverridden && !entry.IsOverridden)
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(FilterText))
        {
            return true;
        }
        var filter = FilterText.Trim();
        return entry.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
               || entry.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)
               || entry.Group.Contains(filter, StringComparison.OrdinalIgnoreCase)
               || entry.Hex.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The edited theme as it would be saved: only values that differ from the base theme.</summary>
    public ThemeDefinition BuildDefinition() => new()
    {
        Name = Name.Trim(),
        Author = string.IsNullOrWhiteSpace(Author) ? null : Author.Trim(),
        Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
        BasedOn = BasedOn,
        Accent = IsAccentOverridden ? ThemeService.ToHex(Accent) : null,
        AccentForeground = IsAccentForegroundOverridden ? ThemeService.ToHex(AccentForeground) : null,
        Colors = Entries.Where(e => e.IsOverridden)
            .ToDictionary(e => e.Key, e => ThemeService.ToHex(e.Color), StringComparer.Ordinal)
    };

    /// <summary>Saves the theme and returns the stored definition.</summary>
    public ThemeDefinition Save()
    {
        if (ValidationError is { } error)
        {
            throw new InvalidOperationException(error);
        }

        var saved = _themeService.Save(BuildDefinition(), _storedName);
        _storedName = saved.Name;
        IsNewTheme = false;
        IsDirty = false;
        OnPropertyChanged(nameof(Title));
        return saved;
    }

    [RelayCommand]
    private void ResetAll()
    {
        foreach (var entry in Entries)
        {
            entry.Color = entry.InheritedColor;
        }
        Accent = _inheritedAccent;
        AccentForeground = _inheritedAccentForeground;
    }

    [RelayCommand]
    private void ResetAccent() => Accent = _inheritedAccent;

    [RelayCommand]
    private void ResetAccentForeground() => AccentForeground = _inheritedAccentForeground;

    partial void OnBasedOnChanged(string value)
    {
        if (_isInitializing)
        {
            return;
        }

        var baseTheme = GetBaseTheme();

        var newAccent = baseTheme is null ? IThemeService.DefaultAccent : _themeService.ResolveAccent(baseTheme);
        var newAccentForeground = (baseTheme is null ? null : _themeService.ResolveAccentForeground(baseTheme)) ?? s_defaultAccentForeground;
        var accentWasOverridden = IsAccentOverridden;
        var accentForegroundWasOverridden = IsAccentForegroundOverridden;
        _inheritedAccent = newAccent;
        _inheritedAccentForeground = newAccentForeground;
        if (!accentWasOverridden)
        {
            Accent = newAccent;
        }
        if (!accentForegroundWasOverridden)
        {
            AccentForeground = newAccentForeground;
        }
        OnPropertyChanged(nameof(IsAccentOverridden));
        OnPropertyChanged(nameof(IsAccentForegroundOverridden));

        foreach (var entry in Entries)
        {
            var wasOverridden = entry.IsOverridden;
            var inherited = (baseTheme is null ? null : _themeService.ResolveColor(baseTheme, entry.Key))
                            ?? _themeService.GetDefaultColor(entry.Key)
                            ?? entry.InheritedColor;
            entry.InheritedColor = inherited;
            if (!wasOverridden)
            {
                entry.Color = inherited;
            }
        }

        RaisePaletteChanged();
    }

    partial void OnAccentChanged(Color value) => RaisePaletteChanged();

    partial void OnAccentForegroundChanged(Color value) => RaisePaletteChanged();

    partial void OnNameChanged(string value) => MarkDirty();

    partial void OnAuthorChanged(string? value) => MarkDirty();

    partial void OnDescriptionChanged(string? value) => MarkDirty();

    private void MarkDirty()
    {
        if (!_isInitializing)
        {
            IsDirty = true;
        }
    }

    private void OnEntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ThemeColorEntryViewModel.Color))
        {
            RaisePaletteChanged();
        }
    }

    private void RaisePaletteChanged()
    {
        if (_isInitializing)
        {
            return;
        }

        IsDirty = true;
        OnPropertyChanged(nameof(OverriddenCount));
        PaletteChanged?.Invoke(this, EventArgs.Empty);
    }

    private ThemeDefinition? GetBaseTheme() => _themeService.Find(BasedOn);

    /// <summary>A theme can't be based on itself or on a theme that inherits from it.</summary>
    private bool IsSelfOrDescendant(ThemeDefinition theme)
    {
        if (_storedName is null)
        {
            return false;
        }
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var t = theme; t is not null && visited.Add(t.Name); t = _themeService.Find(t.BasedOn))
        {
            if (string.Equals(t.Name, _storedName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
