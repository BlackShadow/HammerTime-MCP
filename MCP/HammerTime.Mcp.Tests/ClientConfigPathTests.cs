using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Reflection;

namespace HammerTime.Mcp.Tests;

[TestClass]
public sealed class ClientConfigPathTests
{
    [TestMethod]
    public void KimiClientUsesKimiDirectory()
    {
        var clientPath = GetClientPathMethod();
        var projectDir = Path.Combine(Path.GetTempPath(), "hammertime-mcp-path-test");

        var userPath = (string)(clientPath.Invoke(null, new object[] { "kimi-code", "user", projectDir })
            ?? throw new AssertFailedException("ClientPath returned null for Kimi user path."));
        var projectPath = (string)(clientPath.Invoke(null, new object[] { "kimi-code", "project", projectDir })
            ?? throw new AssertFailedException("ClientPath returned null for Kimi project path."));

        StringAssert.EndsWith(userPath, Path.Combine(".kimi", "mcp.json"));
        StringAssert.EndsWith(projectPath, Path.Combine(".kimi", "mcp.json"));
        StringAssert.DoesNotMatch(userPath, new System.Text.RegularExpressions.Regex(@"[\\/]\\.kimi-code[\\/]"));
        StringAssert.DoesNotMatch(projectPath, new System.Text.RegularExpressions.Regex(@"[\\/]\\.kimi-code[\\/]"));
    }

    private static MethodInfo GetClientPathMethod()
    {
        var installerType = Assembly.Load("hammertime-mcp").GetType("HammerTime.Mcp.Cli.Program+Installer")
            ?? throw new AssertFailedException("Could not find CLI Installer type.");

        return installerType.GetMethod("ClientPath", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new AssertFailedException("Could not find Installer.ClientPath.");
    }
}
