using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WolvenKit.App.Helpers;
using WolvenKit.RED4.Types;

namespace WolvenKit.App.ViewModels.Documents;

/// <summary>
/// The "Objects" tab of a sector: every object of the sector on its own row (one per nodeData entry), grouped by
/// variant and by kind, with the class, the indices, the flags and whether it's hidden. Cooked nodes (game 2.5) are
/// unpacked, so an object inside one is found by name like any other.
/// </summary>
public partial class RDTSectorObjectsViewModel : RedDocumentTabViewModel
{
    public const string AllCategories = "All categories";

    private readonly worldStreamingSector _sector;

    public RDTSectorObjectsViewModel(worldStreamingSector sector, RedDocumentViewModel file) : base(file, "Objects")
    {
        _sector = sector;
        Categories = [AllCategories, .. SectorObjectList.CategoryNames];
    }

    public override ERedDocumentItemType DocumentItemType => ERedDocumentItemType.W2rcBuffer;

    /// <summary>Asks the document to show this node in the data tab.</summary>
    public event EventHandler<int>? NodeOpenRequested;

    /// <summary>Raised when the rows to show change, so the view refreshes its filter.</summary>
    public event EventHandler? FilterChanged;

    [ObservableProperty] private IReadOnlyList<SectorObject> _objects = [];

    [ObservableProperty] private string _summary = "";

    [ObservableProperty] private string _filterText = "";

    [ObservableProperty] private bool _onlyCooked;

    [ObservableProperty] private bool _onlyHidden;

    [ObservableProperty] private string _selectedCategory = AllCategories;

    [ObservableProperty] private SectorObject? _selectedObject;

    public IReadOnlyList<string> Categories { get; }

    partial void OnFilterTextChanged(string value) => FilterChanged?.Invoke(this, EventArgs.Empty);

    partial void OnOnlyCookedChanged(bool value) => FilterChanged?.Invoke(this, EventArgs.Empty);

    partial void OnOnlyHiddenChanged(bool value) => FilterChanged?.Invoke(this, EventArgs.Empty);

    partial void OnSelectedCategoryChanged(string value) => FilterChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Reads the sector again each time the tab is shown: edits made in the data tab show up.</summary>
    public override void OnSelected()
    {
        base.OnSelected();
        Refresh();
    }

    [RelayCommand]
    private void Refresh()
    {
        var objects = SectorObjectList.Build(_sector);
        Objects = objects;

        var cooked = objects.Count(o => o.IsCooked);
        var variants = objects.Select(o => o.Variant).Distinct().Count();
        var hidden = objects.Count(o => o.IsHidden);
        Summary = string.Format(CultureInfo.CurrentCulture,
            "{0} nodes → {1} objects · {2} in cooked nodes · {3} node range(s){4}",
            _sector.Nodes.Count, objects.Count, cooked, variants, hidden > 0 ? $" · {hidden} hidden" : "");
        FilterChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Whether a row passes the search and the filters.</summary>
    public bool Matches(SectorObject item)
    {
        if (OnlyCooked && !item.IsCooked)
        {
            return false;
        }

        if (OnlyHidden && !item.IsHidden)
        {
            return false;
        }

        if (SelectedCategory != AllCategories && !item.Categories.Contains(SelectedCategory, StringComparison.Ordinal))
        {
            return false;
        }

        var text = FilterText.Trim();
        if (text.Length == 0)
        {
            return true;
        }

        // every word somewhere in the row: "hedge 1057", "decal variant 7"
        return text.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(word =>
            Contains(item.Name, word) || Contains(item.Resource, word) || Contains(item.Appearance, word) ||
            Contains(item.ClassName, word) || Contains(item.Group, word) || Contains(item.DebugName, word) ||
            Contains(item.VariantName, word) || Contains(item.Categories, word) || Contains(item.Where, word));
    }

    private static bool Contains(string? text, string word) =>
        text?.Contains(word, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Shows the object's node in the data tab.</summary>
    [RelayCommand]
    private void OpenInData(SectorObject? item)
    {
        if ((item ?? SelectedObject) is { } target)
        {
            NodeOpenRequested?.Invoke(this, target.NodeIndex);
        }
    }

    /// <summary>Class, indices, resource and position: what an .xl removal or a bug report needs.</summary>
    [RelayCommand]
    private void CopySummary(SectorObject? item)
    {
        if ((item ?? SelectedObject) is { } target)
        {
            Clipboard.SetText(target.Summary);
        }
    }
}
