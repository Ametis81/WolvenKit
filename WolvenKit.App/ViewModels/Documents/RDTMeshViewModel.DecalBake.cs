using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf.SharpDX;
using WolvenKit.App.Models;
using WolvenKit.App.Services;
using Material = WolvenKit.App.Models.Material;

namespace WolvenKit.App.ViewModels.Documents;

/// <summary>
/// Mesh decals the way the game draws them: a mesh_decal does not shade on its own, it is
/// blended into the surface under it (colour by DiffuseAlpha, normal by NormalAlpha,
/// roughness / metalness by RoughnessMetalnessAlpha, each times the decal's coverage) and
/// the surface is then lit as usual. Drawing the decal as its own mesh lights it differently
/// (patches of another brightness, halos at soft edges, normal-only decals invisible).
///
/// The preview finds the surface a decal lies on (decal vertices on surface vertices), draws
/// the decal's triangles into the surface's texture space (decal UV -> surface UV per
/// triangle), writes composite colour / normal / roughness-metalness maps for the surface
/// and hides the decal. Decals that do not lie on a baked surface (multilayer clothes,
/// another mesh) are drawn as before.
/// </summary>
public partial class RDTMeshViewModel
{
    private const double DecalPositionTolerance = 0.003;   // 3 mm: decals may float slightly
    private const double DecalMatchRatio = 0.9;            // share of decal triangles on the surface
    private const double DecalMaxUvPerimeter = 0.25;       // larger: the triangle crosses a UV seam

    private readonly ConcurrentDictionary<string, byte> _decalBakesRunning = new();
    private PBRMaterial? _hiddenDecalMaterial;

    // decal submesh -> (surface submesh -> lies on it): geometry only, so kept between applies.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<SubmeshComponent, Dictionary<SubmeshComponent, bool>> _decalOnSurface = new();

    private sealed record DecalLayer(
        string DecalName, LoadableModel DecalModel, SubmeshComponent Decal,
        LoadableModel SurfaceModel, SubmeshComponent Surface);

    private sealed class SurfaceGeometry
    {
        public Point3D[] Positions { get; init; } = null!;
        public double[] Uvs { get; init; } = null!;
        public int[] Indices { get; init; } = null!;
    }

    // ---- entry point -------------------------------------------------------------------------

    /// <summary>
    /// Called after the materials of an appearance are applied: composites the decals into
    /// the surfaces under them (from cache, or bakes in the background and applies again).
    /// </summary>
    private void ApplyDecalsToSurfaces(Appearance app)
    {
        try
        {
            var layers = FindDecalLayers(app);
            if (layers.Count == 0)
            {
                return;
            }

            foreach (var group in layers.GroupBy(l => l.Surface.MaterialName!))
            {
                var surfaceName = group.Key;
                var decals = group.ToList();
                var key = DecalCompositeKey(surfaceName, decals);
                var (cd, cn, crm) = DecalCompositePaths(surfaceName, key);

                if (File.Exists(cd))
                {
                    UseDecalComposite(app, surfaceName, decals, cd, cn, crm);
                    continue;
                }

                if (!_decalBakesRunning.TryAdd(key, 0))
                {
                    continue;
                }

                var context = SynchronizationContext.Current;
                var surfaceMaterial = app.Models.SelectMany(m => m.Meshes)
                    .First(sm => sm.MaterialName == surfaceName).Material as PBRMaterial;
                var job = PrepareDecalJob(surfaceName, surfaceMaterial, decals);

                _ = Task.Run(() =>
                {
                    try
                    {
                        BakeDecalComposite(job, cd, cn, crm);
                        Parent?.GetLoggerService()?.Debug(
                            $"[Decals] {job.Layers.Count} decal(s) baked into '{SubmeshComponent.StripMeshPrefix(surfaceName)}'");
                    }
                    catch (Exception ex)
                    {
                        Parent?.GetLoggerService()?.Warning($"[Decals] '{surfaceName}': {ex.Message}");
                    }
                    finally
                    {
                        _decalBakesRunning.TryRemove(key, out _);
                    }

                    if (context != null && !_disposed)
                    {
                        context.Post(_ =>
                        {
                            if (!_disposed && SelectedAppearance == app && File.Exists(cd))
                            {
                                UseDecalComposite(app, surfaceName, decals, cd, cn, crm);
                            }
                        }, null);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Parent?.GetLoggerService()?.Debug($"[Decals] {ex.Message}");
        }
    }

    // ---- which decal lies on which surface -----------------------------------------------------

    private List<DecalLayer> FindDecalLayers(Appearance app)
    {
        var all = app.Models.SelectMany(m => m.Meshes.Select(sm => (Model: m, Mesh: sm)))
            .Where(x => !string.IsNullOrEmpty(x.Mesh.MaterialName))
            .ToList();

        var surfaces = all.Where(x => IsDecalSurface(x.Mesh)).ToList();
        if (surfaces.Count == 0)
        {
            return new List<DecalLayer>();
        }

        var cells = new Dictionary<SubmeshComponent, Dictionary<(long, long, long), List<int>>?>();
        var geometries = new Dictionary<SubmeshComponent, SurfaceGeometry?>();
        SurfaceGeometry? Geo(LoadableModel m, SubmeshComponent sm)
        {
            if (!geometries.TryGetValue(sm, out var g))
            {
                g = ReadSurfaceGeometry(m, sm);
                geometries[sm] = g;
            }

            return g;
        }

        var result = new List<DecalLayer>();

        // One submesh per decal material (the lowest LOD): every LOD would blend it again.
        foreach (var decalGroup in all.Where(x => IsBakeableDecal(x.Mesh.MaterialName!))
                     .GroupBy(x => x.Mesh.MaterialName!))
        {
            var (decalModel, decal) = decalGroup.OrderBy(x => x.Mesh.LOD).First();
            var dg = Geo(decalModel, decal);
            if (dg == null || dg.Indices.Length < 3)
            {
                continue;
            }

            var known = _decalOnSurface.GetOrCreateValue(decal);
            var bestRatio = 0.0;
            var found = false;
            foreach (var (surfaceModel, surface) in surfaces.OrderBy(s => s.Mesh.LOD == decal.LOD ? 0 : 1))
            {
                if (known.TryGetValue(surface, out var lies))
                {
                    if (lies)
                    {
                        result.Add(new DecalLayer(decalGroup.Key, decalModel, decal, surfaceModel, surface));
                        found = true;
                        break;
                    }

                    continue;
                }

                var sg = Geo(surfaceModel, surface);
                if (sg == null)
                {
                    continue;
                }

                if (!cells.TryGetValue(surface, out var grid))
                {
                    grid = BuildPositionGrid(sg);
                    cells[surface] = grid;
                }

                var triangles = Math.Max(1, dg.Indices.Length / 3);
                var ratio = grid != null && MapDecalTriangles(dg, sg, grid, out var mapped) ? (double)mapped.Count / triangles : 0;
                bestRatio = Math.Max(bestRatio, ratio);
                var onSurface = ratio >= DecalMatchRatio;
                known[surface] = onSurface;
                if (onSurface)
                {
                    result.Add(new DecalLayer(decalGroup.Key, decalModel, decal, surfaceModel, surface));
                    Parent?.GetLoggerService()?.Debug(
                        $"[Decals] '{SubmeshComponent.StripMeshPrefix(decalGroup.Key)}' lies on '{SubmeshComponent.StripMeshPrefix(surface.MaterialName)}' ({ratio:P0} of its triangles)");
                    found = true;
                    break;
                }
            }

            if (!found && !known.ContainsKey(decal))
            {
                // Logged once per decal (the marker entry keeps it from repeating).
                known[decal] = false;
                Parent?.GetLoggerService()?.Debug(
                    $"[Decals] '{SubmeshComponent.StripMeshPrefix(decalGroup.Key)}' not on a baked surface (best match {bestRatio:P0} of its triangles, needs {DecalMatchRatio:P0}); drawn as its own mesh");
            }
        }

        return result;
    }

    /// <summary>A plain mesh_decal (not emissive, not the makeup / hair / wet variants).</summary>
    private bool IsBakeableDecal(string name)
    {
        if (!TryGetRawMaterial(name, out var raw) || raw == null)
        {
            return false;
        }

        if (!Has(ClassifyMaterialFor(name, raw), MaterialKind.Decal))
        {
            return false;
        }

        if (!GetTemplateFileName(raw).Equals("mesh_decal", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (raw.Values != null && raw.Values.TryGetValue("EmissiveEV", out var ev) && ReadFloat(ev) > 0.01f)
        {
            return false;
        }

        var (bd, _, _) = GetMaterialFilePathsFromCache(name);
        return File.Exists(bd) || DecalAlpha(raw, "NormalAlpha") > 0f;
    }

    /// <summary>An opaque, baked (PNG) surface a decal can be blended into.</summary>
    private bool IsDecalSurface(SubmeshComponent sm)
    {
        // Not sm.IsTransparent: skin albedo PNGs often carry an alpha channel, which makes the
        // submesh "transparent" for drawing although it is the opaque surface decals lie on.
        var name = sm.MaterialName!;
        if (_multilayerGpuData.ContainsKey(name) || sm.Material is not PBRMaterial)
        {
            return false;
        }

        var kind = ClassifyMaterialFor(name);
        if (Has(kind, MaterialKind.Decal) || Has(kind, MaterialKind.Makeup) || Has(kind, MaterialKind.Eyebrow) ||
            Has(kind, MaterialKind.Hair) || Has(kind, MaterialKind.Glass) || Has(kind, MaterialKind.Eye))
        {
            return false;
        }

        var (bd, _, _) = GetMaterialFilePathsFromCache(name);
        return File.Exists(bd);
    }

    private static float DecalAlpha(Material raw, string key) =>
        // mesh_decal.mt defaults are 0 for all three (Blender add-on MESH_DEFAULTS).
        raw.Values != null && raw.Values.TryGetValue(key, out var v) ? Math.Clamp(ReadFloat(v), 0f, 1f) : 0f;

    private static SurfaceGeometry? ReadSurfaceGeometry(LoadableModel model, SubmeshComponent sm)
    {
        if (sm.Geometry is not HelixToolkit.SharpDX.Core.MeshGeometry3D g ||
            g.Positions == null || g.TextureCoordinates == null || g.Indices == null ||
            g.Positions.Count == 0 || g.TextureCoordinates.Count != g.Positions.Count)
        {
            return null;
        }

        var matrix = model.Transform?.Value ?? Matrix3D.Identity;
        var positions = new Point3D[g.Positions.Count];
        var uvs = new double[g.Positions.Count * 2];
        for (var i = 0; i < positions.Length; i++)
        {
            var p = g.Positions[i];
            positions[i] = matrix.Transform(new Point3D(p.X, p.Y, p.Z));
            uvs[i * 2] = g.TextureCoordinates[i].X;
            uvs[i * 2 + 1] = g.TextureCoordinates[i].Y;
        }

        return new SurfaceGeometry { Positions = positions, Uvs = uvs, Indices = g.Indices.ToArray() };
    }

    private static (long, long, long) DecalCell(Point3D p) =>
        ((long)Math.Floor(p.X / DecalPositionTolerance),
         (long)Math.Floor(p.Y / DecalPositionTolerance),
         (long)Math.Floor(p.Z / DecalPositionTolerance));

    private static Dictionary<(long, long, long), List<int>>? BuildPositionGrid(SurfaceGeometry g)
    {
        var grid = new Dictionary<(long, long, long), List<int>>();
        for (var i = 0; i < g.Positions.Length; i++)
        {
            var key = DecalCell(g.Positions[i]);
            if (!grid.TryGetValue(key, out var list))
            {
                list = new List<int>(1);
                grid[key] = list;
            }

            list.Add(i);
        }

        return grid;
    }

    /// <summary>Surface vertices near a decal vertex (all of them: a UV seam has duplicates).</summary>
    private static List<int> NearSurfaceVertices(Point3D p, SurfaceGeometry surface, Dictionary<(long, long, long), List<int>> grid)
    {
        var result = new List<int>(2);
        var (cx, cy, cz) = DecalCell(p);
        var tol2 = DecalPositionTolerance * DecalPositionTolerance;
        for (var dx = -1; dx <= 1; dx++)
        for (var dy = -1; dy <= 1; dy++)
        for (var dz = -1; dz <= 1; dz++)
        {
            if (!grid.TryGetValue((cx + dx, cy + dy, cz + dz), out var list))
            {
                continue;
            }

            foreach (var i in list)
            {
                if ((surface.Positions[i] - p).LengthSquared <= tol2)
                {
                    result.Add(i);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// For each decal triangle: the surface UVs of its corners (the combination with the
    /// smallest UV perimeter, so a triangle does not jump across a seam).
    /// </summary>
    private static bool MapDecalTriangles(SurfaceGeometry decal, SurfaceGeometry surface,
        Dictionary<(long, long, long), List<int>> grid,
        out List<(int Triangle, double[] SurfaceUv)> mapped)
    {
        mapped = new List<(int, double[])>();
        var near = new Dictionary<int, List<int>>();
        List<int> Near(int v)
        {
            if (!near.TryGetValue(v, out var list))
            {
                list = NearSurfaceVertices(decal.Positions[v], surface, grid);
                near[v] = list;
            }

            return list;
        }

        var triangles = decal.Indices.Length / 3;
        for (var t = 0; t < triangles; t++)
        {
            var a = Near(decal.Indices[t * 3]);
            var b = Near(decal.Indices[t * 3 + 1]);
            var c = Near(decal.Indices[t * 3 + 2]);
            if (a.Count == 0 || b.Count == 0 || c.Count == 0)
            {
                continue;
            }

            double best = double.MaxValue;
            double[]? bestUv = null;
            foreach (var ia in a.Take(4))
            foreach (var ib in b.Take(4))
            foreach (var ic in c.Take(4))
            {
                double ua = surface.Uvs[ia * 2], va = surface.Uvs[ia * 2 + 1];
                double ub = surface.Uvs[ib * 2], vb = surface.Uvs[ib * 2 + 1];
                double uc = surface.Uvs[ic * 2], vc = surface.Uvs[ic * 2 + 1];
                var perimeter = Math.Abs(ua - ub) + Math.Abs(va - vb) + Math.Abs(ub - uc) +
                                Math.Abs(vb - vc) + Math.Abs(uc - ua) + Math.Abs(vc - va);
                if (perimeter < best)
                {
                    best = perimeter;
                    bestUv = new[] { ua, va, ub, vb, uc, vc };
                }
            }

            if (bestUv != null && best <= DecalMaxUvPerimeter)
            {
                mapped.Add((t, bestUv));
            }
        }

        return mapped.Count > 0;
    }

    // ---- composite cache ---------------------------------------------------------------------

    private string DecalCompositeKey(string surfaceName, List<DecalLayer> decals)
    {
        var sb = new System.Text.StringBuilder();
        void Stamp(string path)
        {
            sb.Append(path).Append('|');
            try
            {
                if (File.Exists(path))
                {
                    sb.Append(File.GetLastWriteTimeUtc(path).Ticks);
                }
            }
            catch
            {
                // missing
            }

            sb.Append(';');
        }

        var (sd, sn, srm) = GetMaterialFilePathsFromCache(surfaceName);
        Stamp(sd);
        Stamp(sn);
        Stamp(srm);
        foreach (var layer in decals.OrderBy(l => l.DecalName, StringComparer.Ordinal))
        {
            var (dd, dn, drm) = GetMaterialFilePathsFromCache(layer.DecalName);
            Stamp(dd);
            Stamp(dn);
            Stamp(drm);
            if (TryGetRawMaterial(layer.DecalName, out var raw) && raw != null)
            {
                sb.Append(DecalAlpha(raw, "DiffuseAlpha")).Append(',')
                  .Append(DecalAlpha(raw, "NormalAlpha")).Append(',')
                  .Append(DecalAlpha(raw, "RoughnessMetalnessAlpha")).Append(',');
                if (raw.Values?.TryGetValue("NormalsBlendingMode", out var nbm) == true)
                {
                    sb.Append(ReadFloat(nbm));
                }
            }

            if (layer.Decal.Geometry is HelixToolkit.SharpDX.Core.MeshGeometry3D g)
            {
                sb.Append('#').Append(g.Positions?.Count ?? 0).Append('/').Append(g.Indices?.Count ?? 0);
            }

            sb.Append(';');
        }

        var bytes = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
        return surfaceName + "|" + Convert.ToHexString(bytes)[..10];
    }

    private static (string D, string N, string RM) DecalCompositePaths(string surfaceName, string key)
    {
        var hash = key[(key.LastIndexOf('|') + 1)..];
        var dir = ISettingsManager.GetTemp_OBJPath();
        var stem = Path.Combine(dir, surfaceName + "__decals_" + hash);
        return (stem + "_d.png", stem + "_n.png", stem + "_rm.png");
    }

    // ---- applying ----------------------------------------------------------------------------

    private void UseDecalComposite(Appearance app, string surfaceName, List<DecalLayer> decals, string cd, string cn, string crm)
    {
        var meshes = app.Models.SelectMany(m => m.Meshes).ToList();
        var surface = meshes.FirstOrDefault(sm => sm.MaterialName == surfaceName);
        if (surface?.Material is not PBRMaterial original)
        {
            return;
        }

        var composite = original.CloneMaterial();
        composite.AlbedoMap = GetOrCreateTexture(cd);
        composite.RenderAlbedoMap = true;
        if (File.Exists(cn))
        {
            composite.NormalMap = GetOrCreateTexture(cn);
            composite.RenderNormalMap = true;
        }

        if (File.Exists(crm))
        {
            // Absolute values: the surface's factors are baked into the composite.
            composite.RoughnessMetallicMap = GetOrCreateTexture(crm);
            composite.RenderRoughnessMetallicMap = true;
            composite.RoughnessFactor = 1;
            composite.MetallicFactor = 1;
        }

        foreach (var sm in meshes.Where(sm => sm.MaterialName == surfaceName))
        {
            sm.Material = composite;
        }

        _hiddenDecalMaterial ??= new PBRMaterial
        {
            AlbedoColor = new SharpDX.Color4(0f, 0f, 0f, 0f),
            RenderAlbedoMap = false,
            RenderShadowMap = false,
        };

        // All LODs of the composited decals: the surface carries them now.
        var hidden = decals.Select(d => d.DecalName).ToHashSet(StringComparer.Ordinal);
        foreach (var sm in meshes.Where(sm => sm.MaterialName != null && hidden.Contains(sm.MaterialName)))
        {
            sm.Material = _hiddenDecalMaterial;
            sm.IsTransparent = true;
        }
    }

    // ---- baking ------------------------------------------------------------------------------

    private sealed class DecalJobLayer
    {
        public SurfaceGeometry Decal { get; init; } = null!;
        public List<(int Triangle, double[] SurfaceUv)> Triangles { get; init; } = null!;
        public string ColorPng { get; init; } = null!;
        public string NormalPng { get; init; } = null!;
        public string RmPng { get; init; } = null!;
        public float DiffuseAlpha { get; init; }
        public float NormalAlpha { get; init; }
        public float RmAlpha { get; init; }
        public bool Rnm { get; init; }
        public float ConstRoughness { get; init; }
        public float ConstMetalness { get; init; }
    }

    private sealed class DecalJob
    {
        public string SurfaceColor { get; init; } = null!;
        public string SurfaceNormal { get; init; } = null!;
        public string SurfaceRm { get; init; } = null!;
        public bool SurfaceUsesRmMap { get; init; }
        public float SurfaceRoughnessFactor { get; init; }
        public float SurfaceMetallicFactor { get; init; }
        public List<DecalJobLayer> Layers { get; init; } = null!;
    }

    private DecalJob PrepareDecalJob(string surfaceName, PBRMaterial? surfaceMaterial, List<DecalLayer> decals)
    {
        var (sd, sn, srm) = GetMaterialFilePathsFromCache(surfaceName);
        var layers = new List<DecalJobLayer>();

        foreach (var layer in decals)
        {
            var dg = ReadSurfaceGeometry(layer.DecalModel, layer.Decal);
            var sg = ReadSurfaceGeometry(layer.SurfaceModel, layer.Surface);
            if (dg == null || sg == null || !TryGetRawMaterial(layer.DecalName, out var raw) || raw == null)
            {
                continue;
            }

            var grid = BuildPositionGrid(sg);
            if (grid == null || !MapDecalTriangles(dg, sg, grid, out var mapped))
            {
                continue;
            }

            var (dd, dn, drm) = GetMaterialFilePathsFromCache(layer.DecalName);
            var (rough, metal) = GetDecalRoughMetal(layer.DecalName);
            layers.Add(new DecalJobLayer
            {
                Decal = dg,
                Triangles = mapped,
                ColorPng = dd,
                NormalPng = dn,
                RmPng = drm,
                DiffuseAlpha = DecalAlpha(raw, "DiffuseAlpha"),
                NormalAlpha = DecalAlpha(raw, "NormalAlpha"),
                RmAlpha = DecalAlpha(raw, "RoughnessMetalnessAlpha"),
                Rnm = raw.Values?.TryGetValue("NormalsBlendingMode", out var nbm) == true && ReadFloat(nbm) > 0.5f,
                ConstRoughness = rough,
                ConstMetalness = metal,
            });
        }

        return new DecalJob
        {
            SurfaceColor = sd,
            SurfaceNormal = sn,
            SurfaceRm = srm,
            SurfaceUsesRmMap = surfaceMaterial?.RenderRoughnessMetallicMap == true && File.Exists(srm),
            SurfaceRoughnessFactor = (float)(surfaceMaterial?.RoughnessFactor ?? 0.8),
            SurfaceMetallicFactor = (float)(surfaceMaterial?.MetallicFactor ?? 0),
            Layers = layers,
        };
    }

    /// <summary>A PNG as a 32-bit ARGB bitmap, without keeping the file open.</summary>
    private static Bitmap? LoadPng32(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = new MemoryStream(File.ReadAllBytes(path));
        using var src = new Bitmap(stream);
        var bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.DrawImage(src, 0, 0, src.Width, src.Height);
        }

        return bmp;
    }

    /// <summary>Pixels of one bitmap, BGRA, row-major (stride = width * 4).</summary>
    private sealed class Pixels
    {
        public int W;
        public int H;
        public byte[] Data = Array.Empty<byte>();

        public static Pixels? From(Bitmap? bmp)
        {
            if (bmp == null)
            {
                return null;
            }

            var p = new Pixels { W = bmp.Width, H = bmp.Height, Data = new byte[bmp.Width * bmp.Height * 4] };
            var data = bmp.LockBits(new Rectangle(0, 0, p.W, p.H), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (var y = 0; y < p.H; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, p.Data, y * p.W * 4, p.W * 4);
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }

            return p;
        }

        public Bitmap ToBitmap()
        {
            var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (var y = 0; y < H; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(Data, y * W * 4, data.Scan0 + y * data.Stride, W * 4);
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }

            return bmp;
        }

        /// <summary>Bilinear sample with wrap; channels 0..3 = B, G, R, A, in 0..1.</summary>
        public void Sample(double u, double v, Span<float> bgra)
        {
            var x = (u - Math.Floor(u)) * W - 0.5;
            var y = (v - Math.Floor(v)) * H - 0.5;
            var x0 = (int)Math.Floor(x);
            var y0 = (int)Math.Floor(y);
            var fx = (float)(x - x0);
            var fy = (float)(y - y0);
            int Wrap(int i, int n) => ((i % n) + n) % n;
            int ix0 = Wrap(x0, W), ix1 = Wrap(x0 + 1, W), iy0 = Wrap(y0, H), iy1 = Wrap(y0 + 1, H);
            for (var c = 0; c < 4; c++)
            {
                float a = Data[(iy0 * W + ix0) * 4 + c], b = Data[(iy0 * W + ix1) * 4 + c];
                float d = Data[(iy1 * W + ix0) * 4 + c], e = Data[(iy1 * W + ix1) * 4 + c];
                bgra[c] = ((a + (b - a) * fx) * (1 - fy) + (d + (e - d) * fx) * fy) / 255f;
            }
        }
    }

    private static void BakeDecalComposite(DecalJob job, string outColor, string outNormal, string outRm)
    {
        if (job.Layers.Count == 0)
        {
            return;
        }

        using var colorBmp = LoadPng32(job.SurfaceColor);
        var color = Pixels.From(colorBmp);
        if (color == null)
        {
            return;
        }

        var anyNormal = job.Layers.Any(l => l.NormalAlpha > 0f && File.Exists(l.NormalPng));
        var anyRm = job.Layers.Any(l => l.RmAlpha > 0f);

        // Normal target: the surface's map, or a flat one when only the decals have normals.
        Pixels? normal = null;
        if (anyNormal || File.Exists(job.SurfaceNormal))
        {
            using var nb = LoadPng32(job.SurfaceNormal);
            normal = Pixels.From(nb);
            if (normal == null && anyNormal)
            {
                normal = new Pixels { W = color.W, H = color.H, Data = new byte[color.W * color.H * 4] };
                for (var i = 0; i < normal.Data.Length; i += 4)
                {
                    normal.Data[i] = 255;       // z
                    normal.Data[i + 1] = 128;   // y
                    normal.Data[i + 2] = 128;   // x
                    normal.Data[i + 3] = 255;
                }
            }
        }

        // Roughness / metalness target with absolute values (the surface's factors applied).
        Pixels? rm = null;
        if (anyRm)
        {
            using var rb = job.SurfaceUsesRmMap ? LoadPng32(job.SurfaceRm) : null;
            rm = Pixels.From(rb) ?? new Pixels { W = color.W, H = color.H, Data = new byte[color.W * color.H * 4] };
            var fill = rb == null;
            for (var i = 0; i < rm.Data.Length; i += 4)
            {
                float r = fill ? 1f : rm.Data[i + 1] / 255f;
                float m = fill ? 1f : rm.Data[i] / 255f;
                rm.Data[i] = (byte)(Math.Clamp(m * job.SurfaceMetallicFactor, 0f, 1f) * 255f);
                rm.Data[i + 1] = (byte)(Math.Clamp(r * job.SurfaceRoughnessFactor, 0f, 1f) * 255f);
                rm.Data[i + 2] = 255;
                rm.Data[i + 3] = 255;
            }
        }

        foreach (var layer in job.Layers)
        {
            // No colour PNG (normal-only decal): full coverage, no colour.
            using var dcb = LoadPng32(layer.ColorPng);
            var dColor = Pixels.From(dcb);

            using var dnb = layer.NormalAlpha > 0f ? LoadPng32(layer.NormalPng) : null;
            var dNormal = Pixels.From(dnb);
            using var drb = layer.RmAlpha > 0f ? LoadPng32(layer.RmPng) : null;
            var dRm = Pixels.From(drb);

            foreach (var (tri, suv) in layer.Triangles)
            {
                var dIdx = layer.Decal.Indices;
                var duv = layer.Decal.Uvs;
                int i0 = dIdx[tri * 3], i1 = dIdx[tri * 3 + 1], i2 = dIdx[tri * 3 + 2];
                double[] d = { duv[i0 * 2], duv[i0 * 2 + 1], duv[i1 * 2], duv[i1 * 2 + 1], duv[i2 * 2], duv[i2 * 2 + 1] };

                // Decal tangent frame -> surface tangent frame: M = S * D^-1 (UV edge matrices),
                // made a rotation (/ sqrt|det|) for the normal's XY.
                var (m00, m01, m10, m11) = UvFrameRotation(d, suv);

                if (dColor is { } dC && layer.DiffuseAlpha > 0f)
                {
                    RasterizeInto(color, suv, d, (px, uv) =>
                    {
                        Span<float> s = stackalloc float[4];
                        dC.Sample(uv.U, uv.V, s);
                        var a = s[3] * layer.DiffuseAlpha;
                        if (a > 0f)
                        {
                            for (var c = 0; c < 3; c++)
                            {
                                var o = px + c;
                                color.Data[o] = (byte)Math.Clamp(color.Data[o] + (s[c] * 255f - color.Data[o]) * a, 0f, 255f);
                            }
                        }
                    });
                }

                if (normal is { } nTarget && dNormal is { } dN && layer.NormalAlpha > 0f)
                {
                    RasterizeInto(nTarget, suv, d, (px, uv) =>
                    {
                        var a = Coverage(dColor, uv) * layer.NormalAlpha;
                        if (a <= 0f)
                        {
                            return;
                        }

                        Span<float> n4 = stackalloc float[4];
                        dN.Sample(uv.U, uv.V, n4);
                        float dx = n4[2] * 2f - 1f, dy = n4[1] * 2f - 1f, dz = n4[0] * 2f - 1f;
                        float rx = m00 * dx + m01 * dy, ry = m10 * dx + m11 * dy;
                        float bx = nTarget.Data[px + 2] / 255f * 2f - 1f;
                        float by = nTarget.Data[px + 1] / 255f * 2f - 1f;
                        float bz = nTarget.Data[px] / 255f * 2f - 1f;

                        float ox, oy, oz;
                        if (layer.Rnm)
                        {
                            // Reoriented normal mapping: the decal's detail on top of the surface.
                            float tx = bx, ty = by, tz = bz + 1f;
                            float ux = -rx, uy = -ry, uz = dz;
                            var dot = tx * ux + ty * uy + tz * uz;
                            ox = tx * dot / tz - ux;
                            oy = ty * dot / tz - uy;
                            oz = tz * dot / tz - uz;
                        }
                        else
                        {
                            ox = rx;
                            oy = ry;
                            oz = dz;
                        }

                        ox = bx + (ox - bx) * a;
                        oy = by + (oy - by) * a;
                        oz = bz + (oz - bz) * a;
                        var len = MathF.Sqrt(ox * ox + oy * oy + oz * oz);
                        if (len < 1e-5f)
                        {
                            return;
                        }

                        nTarget.Data[px + 2] = (byte)((ox / len * 0.5f + 0.5f) * 255f);
                        nTarget.Data[px + 1] = (byte)((oy / len * 0.5f + 0.5f) * 255f);
                        nTarget.Data[px] = (byte)((oz / len * 0.5f + 0.5f) * 255f);
                    });
                }

                if (rm is { } rmTarget && layer.RmAlpha > 0f)
                {
                    RasterizeInto(rmTarget, suv, d, (px, uv) =>
                    {
                        var a = Coverage(dColor, uv) * layer.RmAlpha;
                        if (a <= 0f)
                        {
                            return;
                        }

                        float r = layer.ConstRoughness, m = layer.ConstMetalness;
                        if (dRm != null)
                        {
                            Span<float> r4 = stackalloc float[4];
                            dRm.Sample(uv.U, uv.V, r4);
                            r = r4[1];
                            m = r4[0];
                        }

                        rmTarget.Data[px + 1] = (byte)Math.Clamp(rmTarget.Data[px + 1] + (r * 255f - rmTarget.Data[px + 1]) * a, 0f, 255f);
                        rmTarget.Data[px] = (byte)Math.Clamp(rmTarget.Data[px] + (m * 255f - rmTarget.Data[px]) * a, 0f, 255f);
                    });
                }
            }
        }

        using (var outBmp = color.ToBitmap())
        {
            SavePngAtomic(outBmp, outColor);
        }

        if (normal != null)
        {
            using var outN = normal.ToBitmap();
            SavePngAtomic(outN, outNormal);
        }

        if (rm != null)
        {
            using var outRmBmp = rm.ToBitmap();
            SavePngAtomic(outRmBmp, outRm);
        }
    }

    /// <summary>Decal coverage at a decal UV: the colour PNG's alpha (1 without one).</summary>
    private static float Coverage(Pixels? color, (double U, double V) uv)
    {
        if (color == null)
        {
            return 1f;
        }

        Span<float> c4 = stackalloc float[4];
        color.Sample(uv.U, uv.V, c4);
        return c4[3];
    }

    /// <summary>
    /// Rotation taking a tangent-space XY of the decal to the surface: the matrix mapping decal
    /// UV edges onto surface UV edges, scaled to unit determinant.
    /// </summary>
    private static (float, float, float, float) UvFrameRotation(double[] d, double[] s)
    {
        double d00 = d[2] - d[0], d10 = d[3] - d[1], d01 = d[4] - d[0], d11 = d[5] - d[1];
        double s00 = s[2] - s[0], s10 = s[3] - s[1], s01 = s[4] - s[0], s11 = s[5] - s[1];
        var det = d00 * d11 - d01 * d10;
        if (Math.Abs(det) < 1e-12)
        {
            return (1, 0, 0, 1);
        }

        // D^-1
        double i00 = d11 / det, i01 = -d01 / det, i10 = -d10 / det, i11 = d00 / det;
        // M = S * D^-1
        var m00 = s00 * i00 + s01 * i10;
        var m01 = s00 * i01 + s01 * i11;
        var m10 = s10 * i00 + s11 * i10;
        var m11 = s10 * i01 + s11 * i11;
        var scale = Math.Sqrt(Math.Abs(m00 * m11 - m01 * m10));
        if (scale < 1e-12)
        {
            return (1, 0, 0, 1);
        }

        return ((float)(m00 / scale), (float)(m01 / scale), (float)(m10 / scale), (float)(m11 / scale));
    }

    /// <summary>
    /// Pixels of the target inside the triangle given in the target's UV space (wrapped),
    /// with the decal UV interpolated at each pixel centre.
    /// </summary>
    private static void RasterizeInto(Pixels target, double[] suv, double[] duv, Action<int, (double U, double V)> pixel)
    {
        double x0 = suv[0] * target.W, y0 = suv[1] * target.H;
        double x1 = suv[2] * target.W, y1 = suv[3] * target.H;
        double x2 = suv[4] * target.W, y2 = suv[5] * target.H;
        var area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
        if (Math.Abs(area) < 1e-9)
        {
            return;
        }

        var minX = (int)Math.Floor(Math.Min(x0, Math.Min(x1, x2)) - 1);
        var maxX = (int)Math.Ceiling(Math.Max(x0, Math.Max(x1, x2)) + 1);
        var minY = (int)Math.Floor(Math.Min(y0, Math.Min(y1, y2)) - 1);
        var maxY = (int)Math.Ceiling(Math.Max(y0, Math.Max(y1, y2)) + 1);
        if ((long)(maxX - minX) * (maxY - minY) > 4L * target.W * target.H)
        {
            return;
        }

        // Barycentric slack of about half a pixel (w changes by edge length / area per pixel).
        var longest = Math.Max(Math.Max(Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)),
                                        Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1))),
                               Math.Sqrt((x0 - x2) * (x0 - x2) + (y0 - y2) * (y0 - y2)));
        var edge = 0.5 * longest / Math.Abs(area);
        for (var py = minY; py <= maxY; py++)
        {
            for (var px = minX; px <= maxX; px++)
            {
                double cx = px + 0.5, cy = py + 0.5;
                var w0 = ((x1 - cx) * (y2 - cy) - (x2 - cx) * (y1 - cy)) / area;
                var w1 = ((x2 - cx) * (y0 - cy) - (x0 - cx) * (y2 - cy)) / area;
                var w2 = 1 - w0 - w1;
                if (w0 < -edge || w1 < -edge || w2 < -edge)
                {
                    continue;
                }

                var u = w0 * duv[0] + w1 * duv[2] + w2 * duv[4];
                var v = w0 * duv[1] + w1 * duv[3] + w2 * duv[5];
                var tx = ((px % target.W) + target.W) % target.W;
                var ty = ((py % target.H) + target.H) % target.H;
                pixel((ty * target.W + tx) * 4, (u, v));
            }
        }
    }
}
