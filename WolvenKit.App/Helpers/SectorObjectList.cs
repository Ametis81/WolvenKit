using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using WolvenKit.RED4.Archive.Buffer;
using WolvenKit.RED4.Types;
using static WolvenKit.RED4.Types.Enums;

namespace WolvenKit.App.Helpers;

/// <summary>
/// One object of a sector: one nodeData entry, with the node (and, for cooked nodes, the inner node) it places.
/// </summary>
public sealed class SectorObject
{
    /// <summary>Index in nodeData.</summary>
    public required int NodeDataIndex { get; init; }

    /// <summary>Index in nodes.</summary>
    public required int NodeIndex { get; init; }

    /// <summary>Inner node of a cooked node (its instanceIndex), -1 for other nodes.</summary>
    public required int InnerIndex { get; init; }

    /// <summary>The node's class, as .xl files and ArchiveXL name it.</summary>
    public required string ClassName { get; init; }

    /// <summary>The kind of object, the same for a cooked class and the class it replaces ("Static mesh").</summary>
    public required string Group { get; init; }

    /// <summary>File name of the object's resource, else its debug name, else its kind.</summary>
    public required string Name { get; init; }

    public string? Resource { get; init; }

    public string? Appearance { get; init; }

    /// <summary>"12 instances", "8 actors"...</summary>
    public string? Detail { get; init; }

    /// <summary>Node range (variantIndices) the entry is in.</summary>
    public required int Variant { get; init; }

    public required string VariantName { get; init; }

    /// <summary>Order of the node ranges: the persistent one first, then the variants.</summary>
    public int VariantOrder => VariantName == "Persistent" ? -1 : Variant;

    /// <summary>The node isn't visible in game (isVisibleInGame = false).</summary>
    public bool IsHidden { get; init; }

    public bool IsCooked { get; init; }

    public ENodeFlags Flags { get; init; }

    /// <summary>"Building, No collision": the flags that tell what an object is.</summary>
    public string Categories { get; init; } = "";

    /// <summary>How many entries place the same node (or inner node): 1 for most.</summary>
    public int SharedCount { get; set; } = 1;

    public string? DebugName { get; init; }

    public float X { get; init; }

    public float Y { get; init; }

    public float Z { get; init; }

    /// <summary>"#157 · node 1057[12]"</summary>
    public string Where => InnerIndex >= 0
        ? $"#{NodeDataIndex} · node {NodeIndex}[{InnerIndex}]"
        : $"#{NodeDataIndex} · node {NodeIndex}";

    public string Position => string.Format(CultureInfo.InvariantCulture, "{0:0.##}, {1:0.##}, {2:0.##}", X, Y, Z);

    public string SharedText => SharedCount > 1 ? $"×{SharedCount}" : "";

    /// <summary>What a removal in an .xl file needs: the class, the indices and where the object is.</summary>
    public string Summary
    {
        get
        {
            var text = new StringBuilder();
            text.Append(CultureInfo.InvariantCulture, $"{ClassName} node {NodeIndex}");
            if (InnerIndex >= 0)
            {
                text.Append(CultureInfo.InvariantCulture, $" instanceIndex {InnerIndex}");
            }

            text.Append(CultureInfo.InvariantCulture, $" nodeData {NodeDataIndex} ({VariantName})");
            if (Resource is { Length: > 0 })
            {
                text.Append(CultureInfo.InvariantCulture, $" {Resource}");
            }

            if (Appearance is { Length: > 0 })
            {
                text.Append(CultureInfo.InvariantCulture, $" [{Appearance}]");
            }

            text.Append(CultureInfo.InvariantCulture, $" at {Position}");
            return text.ToString();
        }
    }
}

/// <summary>
/// The objects of a sector, one per nodeData entry. Since game 2.5 most objects of a sector are grouped by type into
/// cooked nodes (worldCookedStaticMeshNode, worldCookedStaticDecalNode, worldCookedCollisionNode...), whose inner
/// nodes each entry picks with its instanceIndex; this lists them again one by one, as the nodes did before.
/// </summary>
public static class SectorObjectList
{
    private const uint NoInstance = uint.MaxValue;

    /// <summary>Flags shown as categories, in this order.</summary>
    private static readonly (ENodeFlags Flag, string Name)[] s_categoryFlags =
    [
        (ENodeFlags.FLAG_Building, "Building"),
        (ENodeFlags.FLAG_Decoration, "Decoration"),
        (ENodeFlags.FLAG_Road, "Road"),
        (ENodeFlags.FLAG_Terrain, "Terrain"),
        (ENodeFlags.FLAG_VisualFluff, "Fluff"),
        (ENodeFlags.FLAG_Quest, "Quest"),
        (ENodeFlags.FLAG_IsInterior, "Interior"),
        (ENodeFlags.FLAG_NoCollision, "No collision"),
    ];

    /// <summary>The category names, for a filter.</summary>
    public static IReadOnlyList<string> CategoryNames { get; } = s_categoryFlags.Select(c => c.Name).ToList();

    /// <summary>Resource properties of nodes, most telling first; other references are used if none is set.</summary>
    private static readonly string[] s_resourceProperties =
    [
        "mesh", "entityTemplate", "material", "particleSystem", "effect", "foliageResource", "audioName", "data",
    ];

    private static readonly string[] s_innerResourceProperties = ["Mesh", "Material", "Data"];

    private static readonly string[] s_classPrefixes = ["worldCooked", "world", "game"];

    private static readonly string[] s_classSuffixes = ["_Streamable"];

    private static readonly ConcurrentDictionary<(Type, string), PropertyInfo?> s_properties = new();

    public static List<SectorObject> Build(worldStreamingSector sector)
    {
        var result = new List<SectorObject>();
        if (sector.NodeData.Data is not worldNodeDataBuffer entries)
        {
            return result;
        }

        var ranges = sector.VariantIndices.Select(i => (int)i).ToList();
        var persistent = (int)sector.PersisentNodeIndex;
        var innerCache = new Dictionary<int, IList?>();

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var nodeIndex = (int)(ulong)entry.NodeIdx;
            var node = nodeIndex >= 0 && nodeIndex < sector.Nodes.Count ? sector.Nodes[nodeIndex]?.Chunk : null;
            var className = node?.GetType().Name ?? "missing node";
            var variant = VariantOf(ranges, i);
            var flags = (ENodeFlags)entry.NodeFlags;

            // only cooked nodes (and instanced meshes, cooked too) pick an inner node; occluders have an instanceIndex of 0
            var inner = -1;
            object? innerNode = null;
            if (node is worldCookedNode && (uint)entry.InstanceIndex != NoInstance)
            {
                if (!innerCache.TryGetValue(nodeIndex, out var list))
                {
                    innerCache[nodeIndex] = list = InnerNodes(node);
                }

                inner = (int)(uint)entry.InstanceIndex;
                innerNode = list is not null && inner >= 0 && inner < list.Count ? list[inner] : null;
            }

            var (resource, appearance, detail) = innerNode is not null
                ? DescribeInner(node!, innerNode, inner)
                : DescribeNode(node);

            var debugName = node?.DebugName.GetResolvedText();
            if (debugName is "None" or "")
            {
                debugName = null;
            }

            var group = GroupName(className);
            result.Add(new SectorObject
            {
                NodeDataIndex = i,
                NodeIndex = nodeIndex,
                InnerIndex = inner,
                ClassName = className,
                Group = group,
                Name = ShortName(resource) ?? debugName ?? group,
                Resource = resource,
                Appearance = appearance,
                Detail = detail,
                Variant = variant,
                VariantName = variant == persistent || ranges.Count <= 1 ? "Persistent" : $"Variant {variant}",
                IsHidden = node is not null && !node.IsVisibleInGame,
                IsCooked = node is worldCookedNode,
                Flags = flags,
                Categories = Categories(flags),
                DebugName = debugName,
                X = entry.Transform.Position.X,
                Y = entry.Transform.Position.Y,
                Z = entry.Transform.Position.Z,
            });
        }

        // a node, or an inner node, placed by several entries: one object with several placements, as before 2.5
        foreach (var shared in result.GroupBy(o => (o.NodeIndex, o.InnerIndex)).Where(g => g.Count() > 1))
        {
            var count = shared.Count();
            foreach (var o in shared)
            {
                o.SharedCount = count;
            }
        }

        return result;
    }

    /// <summary>The node range an entry is in: the last range starting at or before it.</summary>
    internal static int VariantOf(IReadOnlyList<int> rangeStarts, int entryIndex)
    {
        var variant = 0;
        for (var r = 0; r < rangeStarts.Count; r++)
        {
            if (rangeStarts[r] <= entryIndex)
            {
                variant = r;
            }
        }

        return variant;
    }

    /// <summary>
    /// "Static mesh" for worldCookedStaticMeshNode and worldStaticMeshNode, "AI spot" for worldAISpotNode,
    /// "Compiled community area" for worldCompiledCommunityAreaNode_Streamable.
    /// </summary>
    internal static string GroupName(string className)
    {
        var name = className;
        foreach (var suffix in s_classSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal))
            {
                name = name[..^suffix.Length];
            }
        }

        foreach (var prefix in s_classPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal) && name.Length > prefix.Length && char.IsUpper(name[prefix.Length]))
            {
                name = name[prefix.Length..];
                break;
            }
        }

        if (name.EndsWith("Node", StringComparison.Ordinal) && name.Length > 4)
        {
            name = name[..^4];
        }

        // split camel case, keeping acronyms ("AISpot" -> "AI spot", "GI" -> "GI")
        var words = new List<string>();
        var start = 0;
        for (var i = 1; i < name.Length; i++)
        {
            var upper = char.IsUpper(name[i]);
            var startsWord = upper && (!char.IsUpper(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1])));
            if (startsWord || name[i] == '_')
            {
                words.Add(name[start..i].Trim('_'));
                start = i;
            }
        }

        words.Add(name[start..].Trim('_'));
        words.RemoveAll(w => w.Length == 0);

        var text = string.Join(' ', words.Select((w, i) =>
            i == 0 || w.All(char.IsUpper) ? w : char.ToLowerInvariant(w[0]) + w[1..]));
        return text.Length > 0 ? text : className;
    }

    internal static string Categories(ENodeFlags flags) =>
        string.Join(", ", s_categoryFlags.Where(c => flags.HasFlag(c.Flag)).Select(c => c.Name));

    private static string? ShortName(string? resource)
    {
        if (string.IsNullOrEmpty(resource))
        {
            return null;
        }

        var slash = resource.LastIndexOfAny(['\\', '/']);
        return slash >= 0 ? resource[(slash + 1)..] : resource;
    }

    /// <summary>The inner nodes of a cooked node: InnerNodes, or CollisionNodeInfos for collision.</summary>
    private static IList? InnerNodes(RedBaseClass node)
    {
        if (node is not worldCookedNode { Buffer.Data: { } data })
        {
            return null;
        }

        return (Property(data.GetType(), "InnerNodes") ?? Property(data.GetType(), "CollisionNodeInfos"))?.GetValue(data) as IList;
    }

    private static (string? Resource, string? Appearance, string? Detail) DescribeInner(RedBaseClass node, object inner, int index)
    {
        var type = inner.GetType();
        var resource = s_innerResourceProperties
            .Select(name => Property(type, name)?.GetValue(inner))
            .OfType<IRedRef>()
            .Select(ResourceText)
            .FirstOrDefault(text => text is not null);
        var appearance = Property(type, "MeshAppearance")?.GetValue(inner) is CName appearanceName ? AppearanceText(appearanceName) : null;

        string? detail = null;
        if (Property(type, "NumActors")?.GetValue(inner) is CUInt16 actors)
        {
            detail = $"{(ushort)actors} actors";
        }
        else if (node is worldInstancedMeshNode { Buffer.Data: worldInstancedMeshNodeBuffer buffer } && index < buffer.TransformRanges.Count)
        {
            detail = $"{(ushort)buffer.TransformRanges[index].TransformCount} instances";
        }

        return (resource, appearance, detail);
    }

    private static (string? Resource, string? Appearance, string? Detail) DescribeNode(RedBaseClass? node)
    {
        if (node is null)
        {
            return (null, null, null);
        }

        string? resource = null;
        foreach (var name in s_resourceProperties)
        {
            if (TryGetProperty(node, name) is IRedRef reference && ResourceText(reference) is { } text)
            {
                resource = text;
                break;
            }
        }

        resource ??= node.GetPropertyNames()
            .Select(name => TryGetProperty(node, name))
            .OfType<IRedRef>()
            .Select(ResourceText)
            .FirstOrDefault(text => text is not null);

        var appearance = (TryGetProperty(node, "meshAppearance") ?? TryGetProperty(node, "appearanceName")) is CName appearanceName
            ? AppearanceText(appearanceName)
            : null;

        return (resource, appearance, null);
    }

    private static IRedType? TryGetProperty(RedBaseClass node, string name)
    {
        try
        {
            return node.GetProperty(name);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ResourceText(IRedRef reference) =>
        reference.DepotPath.GetResolvedText() is { Length: > 0 } path ? path :
        (ulong)reference.DepotPath != 0 ? ((ulong)reference.DepotPath).ToString(CultureInfo.InvariantCulture) : null;

    private static string? AppearanceText(CName name) =>
        name.GetResolvedText() is { Length: > 0 } text && text != "None" && text != "default" ? text : null;

    private static PropertyInfo? Property(Type type, string name) =>
        s_properties.GetOrAdd((type, name), key => key.Item1.GetProperty(key.Item2, BindingFlags.Public | BindingFlags.Instance));
}
