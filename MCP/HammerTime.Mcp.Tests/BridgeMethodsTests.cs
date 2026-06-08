using HammerTime.Mcp.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HammerTime.Mcp.Tests;

[TestClass]
public sealed class BridgeMethodsTests
{
    [TestMethod]
    public void ToolAndVertexBridgeMethodsHaveStableNames()
    {
        Assert.AreEqual("editor.tools_list", BridgeMethods.EditorToolsList);
        Assert.AreEqual("editor.tool_activate", BridgeMethods.EditorToolActivate);
        Assert.AreEqual("vertex.subtools_list", BridgeMethods.VertexSubtoolsList);
        Assert.AreEqual("vertex.subtool_activate", BridgeMethods.VertexSubtoolActivate);
    }
}
