using HammerTime.Mcp.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HammerTime.Mcp.Tests;

[TestClass]
public sealed class BrushCatalogTests
{
    [TestMethod]
    public void DefaultTypesMatchHammerTimeBrushToolList()
    {
        var names = BrushCatalog.DefaultTypes.Select(x => x.Name).ToArray();

        CollectionAssert.AreEqual(
            new[]
            {
                "Arch",
                "Block",
                "Tetrahedron",
                "Pyramid",
                "Wedge",
                "Cylinder",
                "Cone",
                "Pipe",
                "Sphere",
                "Torus",
                "Text"
            },
            names);
    }

    [TestMethod]
    public void ResolveTypeHandlesBarrelAndMisspelledBarrellAsCylinder()
    {
        Assert.AreEqual("Cylinder", BrushCatalog.ResolveTypeName("barrel"));
        Assert.AreEqual("Cylinder", BrushCatalog.ResolveTypeName("barrell"));
    }

    [TestMethod]
    public void ResolveTypeHandlesHyphenatedAndSpacedControlNames()
    {
        Assert.AreEqual("numberOfSides", BrushCatalog.NormalizeParameterName("Number of sides"));
        Assert.AreEqual("wallWidth", BrushCatalog.NormalizeParameterName("wall-width"));
        Assert.AreEqual("curvedRamp", BrushCatalog.NormalizeParameterName("Curved ramp"));
    }

    [TestMethod]
    public void ReservedBrushParametersUseNormalizedNames()
    {
        Assert.IsTrue(BrushCatalog.IsReservedParameter("brushType"));
        Assert.IsTrue(BrushCatalog.IsReservedParameter("Brush type"));
        Assert.IsTrue(BrushCatalog.IsReservedParameter("textureScale"));
        Assert.IsTrue(BrushCatalog.IsReservedParameter("Texture scale"));
        Assert.IsFalse(BrushCatalog.IsReservedParameter("numberOfSides"));
    }
}
