using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Splat;
using WolvenKit.App.Helpers;
using WolvenKit.App.Interaction;
using WolvenKit.RED4.Types;

// ReSharper disable once CheckNamespace
namespace WolvenKit.App.ViewModels.Shell;

/// <summary>
/// Colour swatches of multilayer colorScale overrides in the file editor: an entry of a template's
/// overrides.colorScale (and its v), and a layer of a setup and its colorScale, whose colour comes from the
/// layer's template. The values are shown as they are stored (0..1 per channel), like the colorScale dropdown
/// and the palette of the Blender add-on.
/// </summary>
public partial class ChunkViewModel
{
    /// <summary>Colour shown as a swatch next to the value; null for rows that don't stand for one.</summary>
    [ObservableProperty] private Color? _previewColor;

    /// <summary>Called from <see cref="CalculateValue"/> and when a channel of an override colour changes.</summary>
    public void CalculatePreviewColor()
    {
        PreviewColor = GetPreviewColor();

        // the layer's colorScale row shows the same colour; it also depends on the layer's material
        if (ResolvedData is Multilayer_Layer && _propertiesLoaded &&
            Properties.FirstOrDefault(p => p.Name == "colorScale") is { } colorScale)
        {
            colorScale.PreviewColor = PreviewColor;
        }
    }

    private Color? GetPreviewColor() => ResolvedData switch
    {
        Multilayer_LayerTemplateOverridesColor entry => FromChannels(entry.V),
        IRedArray when Name == "v" && ParentData is Multilayer_LayerTemplateOverridesColor entry => FromChannels(entry.V),
        Multilayer_Layer layer => GetLayerColor(layer),
        CName when Name == "colorScale" && ParentData is Multilayer_Layer layer => GetLayerColor(layer),
        _ => null,
    };

    /// <summary>The colour of the layer's colorScale in the layer's template; null if it isn't found there.</summary>
    private static Color? GetLayerColor(Multilayer_Layer layer)
    {
        if (layer.Material.DepotPath.GetResolvedText() is not { Length: > 0 } template ||
            layer.ColorScale.GetResolvedText() is not { Length: > 0 } name ||
            Locator.Current.GetService<DocumentTools>() is not { } documentTools ||
            !documentTools.TryGetMultilayerColor(template, name, out var color))
        {
            return null;
        }

        return MultilayerPalette.ToColor(color.R, color.G, color.B);
    }

    private static Color? FromChannels(CArrayFixedSize<CFloat> v) =>
        v.Count >= 3 ? MultilayerPalette.ToColor(v[0], v[1], v[2]) : null;

    #region custom templates

    /// <summary>The layer of a multilayer setup this node is, or is a property of.</summary>
    private ChunkViewModel? MultilayerLayerNode =>
        ResolvedData is Multilayer_Layer ? this : ParentData is Multilayer_Layer ? Parent : null;

    private static string? GetTemplatePath(ChunkViewModel? layerNode) =>
        layerNode?.ResolvedData is Multilayer_Layer layer && layer.Material.DepotPath.GetResolvedText() is { Length: > 0 } path
            ? path
            : null;

    /// <summary>
    /// Whether the layer's template can be copied into the project: it has one, and it isn't one of the
    /// project's own already (a file of the project under a path the game doesn't use).
    /// </summary>
    public bool CanCopyMultilayerTemplate() =>
        !IsReadOnly &&
        GetTemplatePath(MultilayerLayerNode) is { } path &&
        Locator.Current.GetService<DocumentTools>() is { } tools &&
        !tools.IsCustomMultilayerTemplate(path);

    /// <summary>
    /// Copies the layer's template into the project under a path of its own and points the layer to the copy, so its
    /// colours can be changed without changing the game's template. Other layers of the setup with the same template
    /// can follow.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCopyMultilayerTemplate))]
    private async Task CopyMultilayerTemplateToProject()
    {
        const string title = "Copy template to project";
        if (MultilayerLayerNode is not { } layerNode ||
            GetTemplatePath(layerNode) is not { } source ||
            Locator.Current.GetService<DocumentTools>() is not { } tools)
        {
            return;
        }

        var target = await Interactions.ShowInputBoxAsync($"{title}: path of the copy", tools.GetCustomMultilayerTemplatePath(source));
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        if (tools.CopyMultilayerTemplateToProject(source, target, out var error) is not { } copy)
        {
            Interactions.ShowMessageBox(error, title, WMessageBoxButtons.Ok, WMessageBoxImage.Error);
            return;
        }

        SetLayerTemplate(layerNode, copy);

        if (layerNode.Parent is { } layersNode)
        {
            layersNode.CalculateProperties();
            var others = layersNode.Properties
                .Where(node => node != layerNode && string.Equals(GetTemplatePath(node), source, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (others.Count > 0 &&
                Interactions.ShowMessageBox(
                    $"{others.Count} more layer(s) of this setup use {source}.\nPoint them to {copy} too?",
                    title, WMessageBoxButtons.YesNo) == WMessageBoxResult.Yes)
            {
                foreach (var other in others)
                {
                    SetLayerTemplate(other, copy);
                }
            }
        }
    }

    // as if the path was typed into the layer's material: the file gets dirty, the tree and the swatches follow
    private static void SetLayerTemplate(ChunkViewModel layerNode, string templatePath)
    {
        layerNode.CalculateProperties();
        if (layerNode.Properties.FirstOrDefault(node => node.Name == "material") is { } materialNode)
        {
            materialNode.Data = new CResourceReference<Multilayer_LayerTemplate>(templatePath);
        }

        layerNode.CalculatePreviewColor();
    }

    /// <summary>The overrides.colorScale array of a multilayer template this node is, or is an entry of.</summary>
    private ChunkViewModel? OverrideColorsNode =>
        ResolvedData is CArray<Multilayer_LayerTemplateOverridesColor> && ParentData is Multilayer_LayerTemplateOverrides ? this :
        ResolvedData is Multilayer_LayerTemplateOverridesColor && Parent?.ResolvedData is CArray<Multilayer_LayerTemplateOverridesColor> ? Parent :
        null;

    public bool CanAddMultilayerColor() => !IsReadOnly && OverrideColorsNode is not null;

    /// <summary>
    /// Adds a colour to the template's colorScale overrides. Run on an entry, the new colour starts as a variant of
    /// it (same group, same values); the colour picker of its v sets the values. Asks before changing a template
    /// that isn't the project's own, since that would change the game's template.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAddMultilayerColor))]
    private async Task AddMultilayerColor()
    {
        const string title = "Add colour";
        if (OverrideColorsNode is not { ResolvedData: CArray<Multilayer_LayerTemplateOverridesColor> colors } colorsNode)
        {
            return;
        }

        var templatePath = Tab?.Parent.RelativePath ?? "";
        if (Locator.Current.GetService<DocumentTools>() is { } tools && !tools.IsCustomMultilayerTemplate(templatePath) &&
            Interactions.ShowMessageBox(
                $"{templatePath} is not a template of this project under a path of its own.\n\n" +
                "Changing a template the game uses changes every material that uses it. Copy it to the project first " +
                "(\"Copy template to project\" on a layer of a multilayer setup), then add colours to the copy.\n\nAdd the colour anyway?",
                title, WMessageBoxButtons.YesNo, WMessageBoxImage.Warning) != WMessageBoxResult.Yes)
        {
            return;
        }

        var basis = ResolvedData as Multilayer_LayerTemplateOverridesColor;
        var names = colors.Select(c => c.N.GetResolvedText() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = await Interactions.ShowInputBoxAsync($"{title}: name (group_variant)", SuggestColorName(basis, names));
        name = name.Trim();
        if (name.Length == 0)
        {
            return;
        }

        if (names.Contains(name))
        {
            Interactions.ShowMessageBox($"The template already has a colour named {name}.", title, WMessageBoxButtons.Ok, WMessageBoxImage.Error);
            return;
        }

        var entry = new Multilayer_LayerTemplateOverridesColor { N = name };
        for (var i = 0; i < 3; i++)
        {
            entry.V[i] = basis is { } b && b.V.Count > i ? b.V[i] : (CFloat)0.5f;
        }

        if (!colorsNode.InsertChild(colors.Count, entry))
        {
            return;
        }

        colorsNode.IsExpanded = true;
        if (colorsNode.Properties.LastOrDefault() is { } added)
        {
            added.IsExpanded = true;
            Tab?.ScrollToNode(added);
        }
    }

    // group_null starts a group; variants of a group get the next free number
    internal static string SuggestColorName(Multilayer_LayerTemplateOverridesColor? basis, ISet<string> taken)
    {
        var group = basis?.N.GetResolvedText() is { Length: > 0 } basisName ? basisName.Split('_', 2)[0] : "custom";
        var name = $"{group}_null";
        for (var i = 1; taken.Contains(name); i++)
        {
            name = $"{group}_{i:00}";
        }

        return name;
    }

    #endregion
}
