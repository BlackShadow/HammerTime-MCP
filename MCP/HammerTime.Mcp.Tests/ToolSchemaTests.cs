using System.Collections.Generic;
using System.Linq;
using HammerTime.Mcp.Cli;
using HammerTime.Mcp.Shared;
using Newtonsoft.Json.Linq;
using Xunit;

namespace HammerTime.Mcp.Tests
{
    public class ToolSchemaTests
    {
        // Tools that legitimately accept no parameters. Every other tool must expose at
        // least one property so an LLM can call it without guessing.
        private static readonly HashSet<string> Parameterless = new HashSet<string>
        {
            "hammertime_status",
            "hammertime_skill",
            "hammertime_doctor",
            "documents_list",
            "selection_get",
            "editor_tools_list",
            "brush_types_list",
            "vertex_subtools_list",
            "overlay_clear",
            "compile_profiles_list"
        };

        private static Dictionary<string, JObject> SchemasByName()
        {
            return Program.ToolSchemasForTest().ToDictionary(x => x.Name, x => x.Schema);
        }

        [Fact]
        public void EveryCatalogTool_ResolvesToSchemaWithPropertiesObject()
        {
            var schemas = SchemasByName();
            foreach (var tool in McpToolCatalog.CreateAll())
            {
                Assert.True(schemas.ContainsKey(tool.Name), $"No schema exposed for catalog tool '{tool.Name}'.");
                var properties = schemas[tool.Name]["properties"] as JObject;
                Assert.True(properties != null, $"Tool '{tool.Name}' has no properties object.");
            }
        }

        [Fact]
        public void NonParameterlessTools_HaveAtLeastOneProperty()
        {
            foreach (var (name, schema) in Program.ToolSchemasForTest())
            {
                if (Parameterless.Contains(name)) continue;
                var properties = schema["properties"] as JObject;
                Assert.True(properties != null && properties.Count > 0, $"Tool '{name}' has zero properties but is not listed as parameterless.");
            }
        }

        [Fact]
        public void ParameterlessTools_HaveEmptyProperties()
        {
            var schemas = SchemasByName();
            foreach (var name in Parameterless)
            {
                Assert.True(schemas.ContainsKey(name), $"Expected tool '{name}' to exist.");
                var properties = schemas[name]["properties"] as JObject;
                Assert.NotNull(properties);
                Assert.Empty(properties);
            }
        }

        [Fact]
        public void BrushCreate_TypeEnum_ContainsCylinder()
        {
            var schemas = SchemasByName();
            var typeEnum = schemas["brush_create"]["properties"]["type"]["enum"] as JArray;
            Assert.NotNull(typeEnum);
            Assert.Contains("Cylinder", typeEnum.Select(x => x.Value<string>()));
        }

        [Fact]
        public void TextureReplace_Requires_FindAndReplace()
        {
            var schemas = SchemasByName();
            var required = schemas["texture_replace"]["required"] as JArray;
            Assert.NotNull(required);
            var names = required.Select(x => x.Value<string>()).ToList();
            Assert.Contains("find", names);
            Assert.Contains("replace", names);
        }

        [Fact]
        public void ViewportCameraSet_HasViewsEnumAndPositionVector()
        {
            var schemas = SchemasByName();
            var props = schemas["viewport_camera_set"]["properties"] as JObject;
            Assert.NotNull(props);

            var viewsEnum = props["views"]?["enum"] as JArray;
            Assert.NotNull(viewsEnum);
            var viewValues = viewsEnum.Select(x => x.Value<string>()).ToList();
            Assert.Contains("3d", viewValues);
            Assert.Contains("2d", viewValues);

            AssertIsVector(props["position"] as JObject, "viewport_camera_set.position");
        }

        [Fact]
        public void AuditTools_HaveNonEmptyPropertySchemas()
        {
            var schemas = SchemasByName();
            foreach (var name in new[] { "texture_audit", "map_design_audit" })
            {
                Assert.True(schemas.ContainsKey(name), $"Expected schema for '{name}'.");
                var properties = schemas[name]["properties"] as JObject;
                Assert.True(properties != null && properties.Count > 0, $"Tool '{name}' has no properties.");
            }
        }

        [Fact]
        public void TextureAlignFace_ModeEnum_ContainsWorld()
        {
            var schemas = SchemasByName();
            var modeEnum = schemas["texture_align_face"]["properties"]["mode"]["enum"] as JArray;
            Assert.NotNull(modeEnum);
            Assert.Contains("world", modeEnum.Select(x => x.Value<string>()));
        }

        [Fact]
        public void ViewportCapture_MethodEnum_ContainsGpu()
        {
            var schemas = SchemasByName();
            var methodEnum = schemas["viewport_capture"]["properties"]["method"]["enum"] as JArray;
            Assert.NotNull(methodEnum);
            Assert.Contains("gpu", methodEnum.Select(x => x.Value<string>()));
        }

        [Fact]
        public void ViewportCapture_JpegQuality_IsDocumented()
        {
            var schemas = SchemasByName();
            var jpegQuality = schemas["viewport_capture"]["properties"]["jpegQuality"] as JObject;
            Assert.NotNull(jpegQuality);
            Assert.False(string.IsNullOrWhiteSpace(jpegQuality.Value<string>("description")));
        }

        [Fact]
        public void ViewportCapture_HasInlineCameraObject()
        {
            var schemas = SchemasByName();
            var camera = schemas["viewport_capture"]["properties"]["camera"] as JObject;
            Assert.NotNull(camera);
            Assert.Equal("object", camera.Value<string>("type"));
            Assert.False(string.IsNullOrWhiteSpace(camera.Value<string>("description")));
        }

        [Fact]
        public void TextureAudit_HasPropTextureParams()
        {
            var schemas = SchemasByName();
            var props = schemas["texture_audit"]["properties"] as JObject;
            Assert.NotNull(props);

            var checkPropTextures = props["checkPropTextures"] as JObject;
            Assert.NotNull(checkPropTextures);
            Assert.Equal("boolean", checkPropTextures.Value<string>("type"));
            Assert.False(string.IsNullOrWhiteSpace(checkPropTextures.Value<string>("description")));

            var propMaxDimension = props["propMaxDimension"] as JObject;
            Assert.NotNull(propMaxDimension);
            Assert.Equal("number", propMaxDimension.Value<string>("type"));
            Assert.False(string.IsNullOrWhiteSpace(propMaxDimension.Value<string>("description")));
        }

        [Fact]
        public void EveryToolProperty_HasNonEmptyDescription()
        {
            foreach (var (toolName, schema) in Program.ToolSchemasForTest())
            {
                if (!(schema["properties"] is JObject properties)) continue;
                foreach (var prop in properties.Properties())
                {
                    var obj = prop.Value as JObject;
                    Assert.True(obj != null, $"{toolName}.{prop.Name} is not an object schema.");
                    var description = obj.Value<string>("description");
                    Assert.False(string.IsNullOrWhiteSpace(description),
                        $"{toolName}.{prop.Name} is missing a description.");
                }
            }
        }

        [Fact]
        public void VectorProperties_HaveXyzSubProperties()
        {
            var schemas = SchemasByName();

            // Spot-check brush_create min.
            var min = schemas["brush_create"]["properties"]["min"] as JObject;
            AssertIsVector(min, "brush_create.min");

            // Every object-typed property named like a vector must carry x/y/z.
            var vectorNames = new HashSet<string>
            {
                "min", "max", "origin", "position", "point", "point1", "point2", "point3",
                "direction", "axis", "center", "normal", "translation", "rotationDegrees",
                "scale", "pivot", "delta", "frontDirection", "uAxis", "vAxis"
            };
            foreach (var (toolName, schema) in Program.ToolSchemasForTest())
            {
                if (!(schema["properties"] is JObject properties)) continue;
                foreach (var prop in properties.Properties())
                {
                    if (!vectorNames.Contains(prop.Name)) continue;
                    if (!(prop.Value is JObject obj)) continue;
                    if (obj.Value<string>("type") != "object") continue;
                    AssertIsVector(obj, $"{toolName}.{prop.Name}");
                }
            }
        }

        private static void AssertIsVector(JObject schema, string label)
        {
            Assert.True(schema != null, $"{label} is not an object schema.");
            var props = schema["properties"] as JObject;
            Assert.True(props != null, $"{label} has no properties.");
            Assert.True(props["x"] is JObject, $"{label} is missing x.");
            Assert.True(props["y"] is JObject, $"{label} is missing y.");
            Assert.True(props["z"] is JObject, $"{label} is missing z.");
        }
    }
}
