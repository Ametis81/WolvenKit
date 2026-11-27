using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using YamlDotNet.RepresentationModel;

namespace WolvenKit.App.Helpers;

/// <summary>One entry of an ArchiveXL removal or mutation.</summary>
public sealed record XlNodeDeletion(
    int Index,
    string Type,
    SectorElementKind ElementKind,
    int ExpectedElements,
    IReadOnlyList<int> Elements,
    string? ParseProblem = null);

/// <summary>A sector of an ArchiveXL file (streaming / sectors[]) with its removals and mutations.</summary>
public sealed record XlSectorBlock(
    string Path,
    int? ExpectedNodes,
    IReadOnlyList<XlNodeDeletion> Deletions,
    IReadOnlyList<XlNodeDeletion> Mutations);

/// <summary>What a check of an .xl against a sector found.</summary>
public sealed class XlCheckResult
{
    /// <summary>Removals as ArchiveXL would apply them, by nodeData entry.</summary>
    public Dictionary<int, XlNodeDeletion> Valid { get; } = new();

    /// <summary>Problem per nodeData entry, for the rows.</summary>
    public Dictionary<int, string> RowProblems { get; } = new();

    /// <summary>Every problem, in file order, for the report.</summary>
    public List<string> Problems { get; } = [];

    /// <summary>
    /// ArchiveXL skips the whole sector when its nodeData count isn't expectedNodes (after a game patch, typically).
    /// </summary>
    public bool SectorRejected { get; set; }
}

/// <summary>
/// Reads the sector removals and mutations of an ArchiveXL file and checks them against a sector the way
/// ArchiveXL does (WorldStreaming/Config.cpp): nodeData count against expectedNodes, class against type,
/// and for single instances or actors their count against expectedInstances / expectedActors.
/// Removals: missing expected* or an invalid or out-of-range single index makes ArchiveXL drop the singles and
/// remove the whole node. Mutations: ArchiveXL skips an entry whose position, orientation or scale is malformed, and
/// skips a single instance / actor mutation that is invalid or out of range (the others still apply).
/// </summary>
public static class SectorXlCheck
{
    public static List<XlSectorBlock> Parse(string yaml)
    {
        var stream = new YamlStream();
        using (var reader = new StringReader(yaml))
        {
            stream.Load(reader);
        }

        var result = new List<XlSectorBlock>();
        foreach (var document in stream.Documents)
        {
            if (document.RootNode is not YamlMappingNode root ||
                Child(root, "streaming") is not YamlMappingNode streaming ||
                Child(streaming, "sectors") is not YamlSequenceNode sectors)
            {
                continue;
            }

            foreach (var sector in sectors.OfType<YamlMappingNode>())
            {
                if (Scalar(sector, "path") is not { Length: > 0 } path)
                {
                    continue;
                }

                var deletions = ReadEntries(sector, "nodeDeletions", isMutation: false);
                var mutations = ReadEntries(sector, "nodeMutations", isMutation: true);
                result.Add(new XlSectorBlock(path, Int(sector, "expectedNodes"), deletions, mutations));
            }
        }

        return result;
    }

    /// <summary>The block of a file for this sector: same depot path, else the same file name.</summary>
    public static XlSectorBlock? FindBlock(IEnumerable<XlSectorBlock> blocks, string sectorPath)
    {
        var list = blocks.ToList();
        var path = Normalize(sectorPath);
        return list.FirstOrDefault(b => Normalize(b.Path) == path) ??
               list.FirstOrDefault(b => string.Equals(FileName(b.Path), FileName(path), StringComparison.OrdinalIgnoreCase));
    }

    public static XlCheckResult Check(XlSectorBlock block, IReadOnlyList<SectorObject> objects)
    {
        var result = new XlCheckResult();
        if (block.ExpectedNodes != objects.Count)
        {
            result.SectorRejected = true;
            result.Problems.Add(string.Format(CultureInfo.InvariantCulture,
                "The sector has {0} nodeData entries, the file expects {1}: ArchiveXL skips the whole sector.",
                objects.Count, block.ExpectedNodes?.ToString(CultureInfo.InvariantCulture) ?? "none"));
        }

        foreach (var deletion in block.Deletions)
        {
            CheckEntry(result, deletion, objects, isMutation: false);
        }

        foreach (var mutation in block.Mutations)
        {
            CheckEntry(result, mutation, objects, isMutation: true);
        }

        return result;
    }

    // Mirrors ArchiveXL (WorldStreaming/Config.cpp, LoadYAML with ParseSubDeletions / ParseSubMutations).
    private static List<XlNodeDeletion> ReadEntries(YamlMappingNode sector, string key, bool isMutation)
    {
        var list = new List<XlNodeDeletion>();
        if (Child(sector, key) is not YamlSequenceNode nodes)
        {
            return list;
        }

        foreach (var node in nodes.OfType<YamlMappingNode>())
        {
            if (Int(node, "index") is not { } index || Scalar(node, "type") is not { Length: > 0 } type)
            {
                continue;
            }

            list.Add(isMutation ? ReadMutation(node, index, type) : ReadDeletion(node, index, type));
        }

        return list;
    }

    // ParseSubDeletions: missing or invalid expected*, or any invalid or out-of-range single index, clears the singles
    // and the node is removed as a whole (ExpectedElements = -1 marks that). Actors are read before instances.
    private static XlNodeDeletion ReadDeletion(YamlMappingNode node, int index, string type)
    {
        foreach (var (listKey, countKey, kind) in new[]
                 {
                     ("actorDeletions", "expectedActors", SectorElementKind.Actor),
                     ("instanceDeletions", "expectedInstances", SectorElementKind.Instance),
                 })
        {
            if (Child(node, listKey) is not YamlSequenceNode singles)
            {
                continue;
            }

            if (Int(node, countKey) is { } expected && expected > 0 &&
                DeletionElements(singles) is { } elements && elements.All(e => e >= 0 && e < expected))
            {
                return new XlNodeDeletion(index, type, kind, expected, elements);
            }

            return new XlNodeDeletion(index, type, SectorElementKind.None, -1, Array.Empty<int>());
        }

        return new XlNodeDeletion(index, type, SectorElementKind.None, 0, Array.Empty<int>());
    }

    // "[0, 2]" or "[[3, 1]]" (an actor and one of its shapes): the element index; null when one isn't valid
    // (ArchiveXL wants exactly two numbers in a pair).
    private static List<int>? DeletionElements(YamlSequenceNode list)
    {
        var elements = new List<int>();
        foreach (var item in list)
        {
            var text = item switch
            {
                YamlScalarNode scalar => scalar.Value,
                YamlSequenceNode { Children.Count: 2 } pair when pair.Children[1] is YamlScalarNode sub &&
                                                                 int.TryParse(sub.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                    => (pair.Children[0] as YamlScalarNode)?.Value,
                _ => null,
            };
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return null;
            }
            elements.Add(value);
        }
        return elements;
    }

    // LoadYAML / ParseSubMutations: a malformed position (3 or 4 numbers), orientation (4) or scale (3) skips the
    // whole entry. Singles are maps with an index and a transform (position: 4 numbers); without a valid expected*
    // there are none and the node is mutated as a whole; an invalid or out-of-range single is skipped on its own.
    private static XlNodeDeletion ReadMutation(YamlMappingNode node, int index, string type)
    {
        var malformed = new[] { ("position", new[] { 3, 4 }), ("orientation", new[] { 4 }), ("scale", new[] { 3 }) }
            .Where(t => Child(node, t.Item1) is { } value && !HasNumbers(value, t.Item2))
            .Select(t => t.Item1)
            .ToList();
        if (malformed.Count > 0)
        {
            return new XlNodeDeletion(index, type, SectorElementKind.None, 0, Array.Empty<int>(),
                $"malformed {string.Join(", ", malformed)}; ArchiveXL skips this mutation");
        }

        var kind = SectorElementKind.None;
        var expected = 0;
        var elements = new List<int>();
        var skipped = 0;
        foreach (var (listKey, countKey, elementKind) in new[]
                 {
                     ("actorMutations", "expectedActors", SectorElementKind.Actor),
                     ("instanceMutations", "expectedInstances", SectorElementKind.Instance),
                 })
        {
            if (Child(node, listKey) is not YamlSequenceNode singles ||
                Int(node, countKey) is not { } count || count <= 0)
            {
                continue;
            }

            kind = elementKind;
            expected = count;
            foreach (var single in singles)
            {
                if (single is YamlMappingNode map && Int(map, "index") is { } i && i >= 0 && i < count &&
                    IsValidSingleTransform(map))
                {
                    elements.Add(i);
                }
                else
                {
                    skipped++;
                }
            }
        }

        var problem = skipped > 0
            ? $"{skipped} single {(kind == SectorElementKind.Actor ? "actor" : "instance")} mutation(s) invalid or out of range; ArchiveXL skips them"
            : null;
        return new XlNodeDeletion(index, type, elements.Count > 0 ? kind : SectorElementKind.None, elements.Count > 0 ? expected : 0, elements, problem);
    }

    private static bool IsValidSingleTransform(YamlMappingNode map)
    {
        var any = false;
        foreach (var (name, sizes) in new[] { ("position", new[] { 4 }), ("orientation", new[] { 4 }), ("scale", new[] { 3 }) })
        {
            if (Child(map, name) is not { } value)
            {
                continue;
            }
            if (!HasNumbers(value, sizes))
            {
                return false;
            }
            any = true;
        }
        return any;
    }

    private static bool HasNumbers(YamlNode node, int[] sizes) =>
        node is YamlSequenceNode list && sizes.Contains(list.Children.Count) &&
        list.Children.All(c => c is YamlScalarNode { Value: { } v } &&
                               float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _));

    private static void CheckEntry(XlCheckResult result, XlNodeDeletion entry, IReadOnlyList<SectorObject> objects, bool isMutation)
    {
        var label = isMutation ? "mutation" : "removal";
        if (entry.Index < 0 || entry.Index >= objects.Count)
        {
            result.Problems.Add($"#{entry.Index} ({entry.Type}): no such nodeData entry ({label}).");
            return;
        }

        var o = objects[entry.Index];
        string? problem = null;

        if (!string.Equals(o.ClassName, entry.Type, StringComparison.Ordinal))
        {
            // checked first: with another class ArchiveXL leaves the node alone, whatever else the entry says
            problem = $"is {o.ClassName}, the file expects {entry.Type}";
        }
        else if (entry.ParseProblem is not null)
        {
            problem = entry.ParseProblem;
        }
        else if (entry.ExpectedElements < 0)
        {
            problem = "expected* missing or a single index is invalid or out of range; ArchiveXL drops the singles and removes the whole node";
        }
        else if (entry.ElementKind != SectorElementKind.None &&
                 (entry.ElementKind != o.ElementKind || entry.ExpectedElements != o.Elements.Count))
        {
            var what = entry.ElementKind == SectorElementKind.Actor ? "actors" : "instances";
            problem = $"has {o.Elements.Count} {what}, the file expects {entry.ExpectedElements}";
        }

        if (problem is not null)
        {
            result.RowProblems[entry.Index] = problem;
            result.Problems.Add($"#{entry.Index} {o.Name} ({label}): {problem}.");
        }

        // removals as ArchiveXL applies them: dropped singles mean the whole node
        if (!isMutation && (problem is null || entry.ExpectedElements < 0) &&
            string.Equals(o.ClassName, entry.Type, StringComparison.Ordinal))
        {
            result.Valid[entry.Index] = entry.ExpectedElements < 0
                ? entry with { ExpectedElements = 0, Elements = Array.Empty<int>() }
                : entry;
        }
    }

    private static YamlNode? Child(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var child) ? child : null;

    private static string? Scalar(YamlMappingNode node, string key) => (Child(node, key) as YamlScalarNode)?.Value;

    private static int? Int(YamlMappingNode node, string key) =>
        int.TryParse(Scalar(node, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static string Normalize(string path) => path.Trim().Replace('/', '\\').TrimStart('\\').ToUpperInvariant();

    private static string FileName(string path)
    {
        var slash = path.LastIndexOfAny(['\\', '/']);
        return slash >= 0 ? path[(slash + 1)..] : path;
    }
}
