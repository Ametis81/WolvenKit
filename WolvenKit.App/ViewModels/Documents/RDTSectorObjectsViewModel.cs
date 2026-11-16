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

    public const string AllTargetGroups = "All groups";

    private readonly worldStreamingSector _sector;

    public RDTSectorObjectsViewModel(worldStreamingSector sector, RedDocumentViewModel file) : base(file, "Objects")
    {
        _sector = sector;
        Categories = [AllCategories, .. SectorObjectList.CategoryNames];
        TargetGroups = [AllTargetGroups, .. SectorTargetGroups.Names];
    }

    public override ERedDocumentItemType DocumentItemType => ERedDocumentItemType.W2rcBuffer;

    /// <summary>Asks the document to show this node in the data tab.</summary>
    public event EventHandler<int>? NodeOpenRequested;

    /// <summary>Asks the document to show this object (node index, nodeData index) in the sector preview.</summary>
    public event EventHandler<(int NodeIndex, int NodeDataIndex)>? PreviewOpenRequested;

    /// <summary>Raised when the rows to show change, so the view refreshes its filter.</summary>
    public event EventHandler? FilterChanged;

    [ObservableProperty] private IReadOnlyList<SectorObject> _objects = [];

    [ObservableProperty] private string _summary = "";

    [ObservableProperty] private string _filterText = "";

    [ObservableProperty] private bool _onlyCooked;

    [ObservableProperty] private bool _onlyHidden;

    [ObservableProperty] private string _selectedCategory = AllCategories;

    /// <summary>RedHotTools' target group (Mesh, Collision, Area...).</summary>
    [ObservableProperty] private string _selectedTargetGroup = AllTargetGroups;

    [ObservableProperty] private SectorObject? _selectedObject;

    public IReadOnlyList<string> Categories { get; }

    public IReadOnlyList<string> TargetGroups { get; }

    /// <summary>While searching, the groups open by themselves.</summary>
    public bool IsFiltering => FilterText.Trim().Length > 0;

    // an object picked in the preview, selected the next time the list is built
    private int? _pendingNodeDataIndex;

    partial void OnFilterTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsFiltering));
        FilterChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnOnlyCookedChanged(bool value) => FilterChanged?.Invoke(this, EventArgs.Empty);

    partial void OnOnlyHiddenChanged(bool value) => FilterChanged?.Invoke(this, EventArgs.Empty);

    partial void OnSelectedCategoryChanged(string value) => FilterChanged?.Invoke(this, EventArgs.Empty);

    partial void OnSelectedTargetGroupChanged(string value) => FilterChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Reads the sector again each time the tab is shown: edits made in the data tab show up.</summary>
    public override void OnSelected()
    {
        base.OnSelected();
        Refresh();
    }

    [RelayCommand]
    private void Refresh()
    {
        // ticked instances / actors survive a refresh (the list is read again each time the tab is shown)
        var ticked = Objects
            .SelectMany(o => o.Elements.Where(e => e.IsChecked).Select(e => (o.NodeDataIndex, e.Index)))
            .ToHashSet();

        var objects = SectorObjectList.Build(_sector);
        if (ticked.Count > 0)
        {
            foreach (var o in objects)
            {
                foreach (var e in o.Elements)
                {
                    e.IsChecked = ticked.Contains((o.NodeDataIndex, e.Index));
                }
            }
        }

        Objects = objects;

        var cooked = objects.Count(o => o.IsCooked);
        var variants = objects.Select(o => o.Variant).Distinct().Count();
        var hidden = objects.Count(o => o.IsHidden);
        if (_pendingNodeDataIndex is { } pending)
        {
            _pendingNodeDataIndex = null;
            if (objects.FirstOrDefault(o => o.NodeDataIndex == pending) is { } picked)
            {
                // shows the one row, its groups open
                FilterText = $"#{picked.NodeDataIndex}";
                SelectedObject = picked;
            }
        }

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

        if (SelectedTargetGroup != AllTargetGroups && item.TargetGroup != SelectedTargetGroup)
        {
            return false;
        }

        var text = FilterText.Trim();
        if (text.Length == 0)
        {
            return true;
        }

        // every word somewhere in the row: "hedge 1057", "decal variant 7"; "#157" is exactly nodeData entry 157
        return text.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(word =>
            word.Length > 1 && word[0] == '#' && int.TryParse(word.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var entry)
                ? item.NodeDataIndex == entry
                : Contains(item.Name, word) || Contains(item.Resource, word) || Contains(item.Appearance, word) ||
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

    /// <summary>The object picked in the sector preview: selected when this tab is shown.</summary>
    public void SelectWhenShown(int nodeDataIndex) => _pendingNodeDataIndex = nodeDataIndex;

    /// <summary>Shows the object in the sector preview.</summary>
    [RelayCommand]
    private void ShowInPreview(SectorObject? item)
    {
        if ((item ?? SelectedObject) is { } target)
        {
            PreviewOpenRequested?.Invoke(this, (target.NodeIndex, target.NodeDataIndex));
        }
    }

    /// <summary>
    /// An ArchiveXL removal (see <see cref="SectorObjectList.BuildXlRemoval"/>) of the selected rows and of every row
    /// with instances or actors ticked.
    /// </summary>
    public void CopyXlRemoval(IEnumerable<SectorObject> selected)
    {
        var items = selected.Union(Objects.Where(o => o.Elements.Any(e => e.IsChecked))).ToList();
        if (items.Count == 0)
        {
            return;
        }

        Clipboard.SetText(SectorObjectList.BuildXlRemoval(RelativeFilePath, Objects.Count, items));
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
