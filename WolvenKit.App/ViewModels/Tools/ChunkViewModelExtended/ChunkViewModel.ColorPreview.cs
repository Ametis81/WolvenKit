using System;
using System.Linq;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Splat;
using WolvenKit.App.Helpers;
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

        return FromChannels(color.R, color.G, color.B);
    }

    private static Color? FromChannels(CArrayFixedSize<CFloat> v) =>
        v.Count >= 3 ? FromChannels(v[0], v[1], v[2]) : null;

    private static Color FromChannels(float r, float g, float b) => Color.FromRgb(ToByte(r), ToByte(g), ToByte(b));

    private static byte ToByte(float value) =>
        float.IsFinite(value) ? (byte)Math.Clamp(Math.Round(value * 255.0), 0, 255) : (byte)0;
}
