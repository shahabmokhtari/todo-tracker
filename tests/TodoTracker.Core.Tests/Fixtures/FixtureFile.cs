using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TodoTracker.Core.Tests.Fixtures;

/// <summary>
/// Shared fixtures under tests/fixtures that this version of the C# core generates and the Swift and Kotlin ports
/// read. Each is worked out again on every run and must match the committed file exactly, so the file can't drift
/// from what the code does. Regenerate: <c>TT_WRITE_FIXTURES=1 dotnet test --filter FullyQualifiedName~Fixtures</c>.
/// </summary>
internal static partial class FixtureFile
{
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static bool Writing => Environment.GetEnvironmentVariable("TT_WRITE_FIXTURES") == "1";

    /// <summary>Writes the fixture (regenerating) or checks the committed one is exactly <paramref name="generated"/>.</summary>
    public static void Check(string relative, JsonNode generated)
    {
        var text = generated.ToJsonString(Pretty).ReplaceLineEndings("\n") + "\n";
        if (Writing)
        {
            var target = Path.Combine(SourceDirectory(), relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, text);
            return;
        }

        var committed = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", relative.Replace('/', Path.DirectorySeparatorChar))).ReplaceLineEndings("\n");
        Assert.True(committed == text, $"tests/fixtures/{relative} is not what this version generates. Regenerate it (TT_WRITE_FIXTURES=1) and review the diff.");
    }

    /// <summary>Reads a committed fixture (as the ports do).</summary>
    public static JsonNode Read(string relative) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", relative.Replace('/', Path.DirectorySeparatorChar))))!;

    /// <summary>
    /// Every GUID in <paramref name="json"/> replaced by 00000000-0000-4000-8000-00000000000N, numbered in order of
    /// first appearance: boards built with the real API (random ids) become the same on every run.
    /// </summary>
    public static string FixIds(string json, int start = 1)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return AnyGuid().Replace(json, m =>
        {
            if (!map.TryGetValue(m.Value, out var fixedId))
            {
                fixedId = $"00000000-0000-4000-8000-{start + map.Count:x12}";
                map[m.Value] = fixedId;
            }

            return fixedId;
        });
    }

    private static string SourceDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "fixtures");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("tests/fixtures");
    }

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex AnyGuid();
}
