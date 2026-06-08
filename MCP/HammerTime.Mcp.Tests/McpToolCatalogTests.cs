using HammerTime.Mcp.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System.Reflection;

namespace HammerTime.Mcp.Tests;

[TestClass]
public sealed class McpToolCatalogTests
{
    [TestMethod]
    public void CatalogContainsRequestedNextBatchTools()
    {
        var names = McpToolCatalog.CreateAll().Select(x => x.Name).ToArray();

        CollectionAssert.IsSubsetOf(
            new[]
            {
                "hammertime_doctor",
                "textures_list",
                "texture_search",
                "texture_apply",
                "texture_replace",
                "texture_align_face",
                "vertex_snapshot",
                "vertex_move",
                "vertex_split_face",
                "vertex_triangulate",
                "vertex_face_edit",
                "face_list",
                "face_select",
                "face_texture_set",
                "face_delete",
                "clip_preview",
                "clip_apply",
                "clip_split",
                "prefabs_list",
                "prefab_create",
                "fgd_entities_list",
                "entity_schema",
                "entity_create_from_schema",
                "compile_profiles_list",
                "compile_run",
                "compile_log_tail",
                "undo",
                "redo",
                "history_list",
                "map_validate",
                "map_fix_all_safe",
                "selection_filter",
                "selection_grow",
                "selection_by_bounds",
                "cordon_get",
                "cordon_set",
                "cordon_enable",
                "brush_create_from_planes",
                "object_export_maptext",
                "texture_copy_from_face",
                "object_import_maptext",
                "entity_tie_brushes",
                "entity_untie_brushes",
                "viewport_clear_marks"
            },
            names);
    }

    [TestMethod]
    public void CatalogMapsToolsToBridgeMethods()
    {
        var byName = McpToolCatalog.CreateAll().ToDictionary(x => x.Name);

        Assert.AreEqual(BridgeMethods.TexturesList, byName["textures_list"].BridgeMethod);
        Assert.AreEqual(BridgeMethods.VertexMove, byName["vertex_move"].BridgeMethod);
        Assert.AreEqual(BridgeMethods.CompileRun, byName["compile_run"].BridgeMethod);
        Assert.AreEqual(BridgeMethods.CordonSet, byName["cordon_set"].BridgeMethod);
        Assert.AreEqual(BridgeMethods.EntityTieBrushes, byName["entity_tie_brushes"].BridgeMethod);
        Assert.AreEqual(BridgeMethods.EntityUntieBrushes, byName["entity_untie_brushes"].BridgeMethod);
        Assert.AreEqual(BridgeMethods.ViewportClearMarks, byName["viewport_clear_marks"].BridgeMethod);
    }

    [TestMethod]
    public void CliToolArraySchemasDeclareItems()
    {
        var cliAssembly = Assembly.Load("hammertime-mcp");
        var toolDefinitionType = cliAssembly.GetType("HammerTime.Mcp.Cli.Program+ToolDefinition")
            ?? throw new AssertFailedException("Could not find CLI ToolDefinition type.");
        var createAll = toolDefinitionType.GetMethod("CreateAll", BindingFlags.Public | BindingFlags.Static)
            ?? throw new AssertFailedException("Could not find ToolDefinition.CreateAll.");
        var tools = (System.Collections.IEnumerable)(createAll.Invoke(null, null)
            ?? throw new AssertFailedException("ToolDefinition.CreateAll returned null."));

        foreach (var tool in tools)
        {
            var name = (string)(toolDefinitionType.GetProperty("Name")?.GetValue(tool)
                ?? throw new AssertFailedException("Tool name missing."));
            var schema = (JObject)(toolDefinitionType.GetProperty("InputSchema")?.GetValue(tool)
                ?? throw new AssertFailedException($"Tool {name} schema missing."));
            AssertArraySchemasDeclareItems(schema, name, "inputSchema");
        }
    }

    private static void AssertArraySchemasDeclareItems(JToken token, string toolName, string path)
    {
        if (token is JObject obj)
        {
            if (obj["type"]?.Type == JTokenType.String && obj.Value<string>("type") == "array" && obj["items"] == null)
            {
                Assert.Fail($"Tool {toolName} has array schema without items at {path}.");
            }

            foreach (var property in obj.Properties())
            {
                AssertArraySchemasDeclareItems(property.Value, toolName, $"{path}.{property.Name}");
            }
        }
        else if (token is JArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                AssertArraySchemasDeclareItems(array[i], toolName, $"{path}[{i}]");
            }
        }
    }
}
