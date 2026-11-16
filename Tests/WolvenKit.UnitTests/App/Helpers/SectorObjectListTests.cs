using WolvenKit.App.Helpers;
using Xunit;
using static WolvenKit.RED4.Types.Enums;

namespace Wolvenkit.Test.App.Helpers;

/// <summary>The sector's object list: kinds of nodes (cooked or not), node ranges and flag categories.</summary>
public class SectorObjectListTests
{
    [Theory]
    [InlineData("worldCookedStaticMeshNode", "Static mesh")]
    [InlineData("worldStaticMeshNode", "Static mesh")]
    [InlineData("worldCookedStaticDecalNode", "Static decal")]
    [InlineData("worldCookedCollisionNode", "Collision")]
    [InlineData("worldAISpotNode", "AI spot")]
    [InlineData("worldGINode", "GI")]
    [InlineData("worldGISpaceNode", "GI space")]
    [InlineData("worldInstancedDestructibleMeshNode", "Instanced destructible mesh")]
    [InlineData("worldCompiledCommunityAreaNode_Streamable", "Compiled community area")]
    [InlineData("MinimapDataNode", "Minimap data")]
    public void GroupName_SameKindForCookedAndOldClasses(string className, string expected) =>
        Assert.Equal(expected, SectorObjectList.GroupName(className));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2338, 0)]
    [InlineData(2339, 1)]
    [InlineData(2340, 2)]
    [InlineData(2549, 10)]
    public void VariantOf_LastRangeStartingAtOrBefore(int entry, int expected) =>
        Assert.Equal(expected, SectorObjectList.VariantOf([0, 2339, 2340, 2366, 2367, 2371, 2373, 2410, 2519, 2520, 2521], entry));

    [Fact]
    public void Categories_OnlyTellingFlags_InOrder()
    {
        var flags = ENodeFlags.FLAG_IsInterior | ENodeFlags.FLAG_NoCollision | ENodeFlags.FLAG_Decoration |
                    ENodeFlags.FLAG_Culling_IsCullable | ENodeFlags.FLAG_AllowDistanceBoosting;
        Assert.Equal("Decoration, Interior, No collision", SectorObjectList.Categories(flags));
        Assert.Equal("", SectorObjectList.Categories(ENodeFlags.FLAG_None));
    }
}
