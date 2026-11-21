using System.Linq;
using WolvenKit.RED4.Types;

namespace WolvenKit.App.ViewModels.Documents;

/// <summary>
/// A .mesh preview is built from the appearances and material entries when the tab first opens.
/// Edits that change them (Un-Dynamify Materials, added or renamed appearances, other chunk
/// materials) rebuild it when the preview is shown again; live update only covers material values.
/// </summary>
public partial class RDTMeshViewModel
{
    private string? _meshStructureOnLeave;

    /// <summary>Appearance names, chunk materials and material entries: what the preview's appearances are built from.</summary>
    private static string MeshStructure(CMesh mesh) =>
        string.Join("|", mesh.Appearances.Select(a => a?.Chunk).OfType<meshMeshAppearance>()
            .Select(a => a.Name.GetResolvedText() + ":" + string.Join(",", a.ChunkMaterials.Select(c => c.GetResolvedText())))) +
        "#" + string.Join(",", mesh.MaterialEntries.Select(e => $"{e.Name.GetResolvedText()}/{e.Index}/{e.IsLocalInstance}"));

    /// <summary>Called when the preview tab is left (after the generated stand-ins are rolled back).</summary>
    private void RememberMeshStructure() =>
        _meshStructureOnLeave = _data is CMesh mesh && IsRendered ? MeshStructure(mesh) : null;

    /// <summary>Called when the preview tab is shown again: rebuilds the appearances if the mesh changed meanwhile.</summary>
    private void RebuildMeshIfChanged()
    {
        if (_data is not CMesh mesh || _meshStructureOnLeave is not { } before)
        {
            return;
        }

        _meshStructureOnLeave = null;
        if (MeshStructure(mesh) == before)
        {
            return;
        }

        var selected = SelectedAppearance?.Name;
        Appearances = new();
        IsRendered = false;
        RenderMesh();
        OnPropertyChanged(nameof(Appearances));

        if (selected != null && Appearances.FirstOrDefault(a => a.Name == selected) is { } same)
        {
            SelectedAppearance = same;
        }
    }
}
