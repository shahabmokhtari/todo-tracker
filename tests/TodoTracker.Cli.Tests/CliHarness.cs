using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;

namespace TodoTracker.Cli.Tests;

public sealed record CliResult(int ExitCode, string Out, string Error)
{
    public JsonNode Json => JsonNode.Parse(Out) ?? throw new InvalidOperationException("no JSON output");
}

/// <summary>Runs <c>tt</c> in-process against a temp data folder (vault in <c>&lt;data&gt;/vault</c>).</summary>
public sealed class CliHarness : IDisposable
{
    public static readonly DateTimeOffset T0 = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);

    public CliHarness(bool history = false)
    {
        DataDirectory = Path.Combine(Path.GetTempPath(), "tt-cli-" + Guid.NewGuid().ToString("N"));
        History = history;
    }

    public string DataDirectory { get; }

    public string VaultDirectory => Path.Combine(DataDirectory, "vault");

    public FakeTimeProvider Time { get; } = new(T0);

    public bool History { get; }

    public string WorkingDirectory { get; set; } = Path.GetTempPath();

    public Task<CliResult> Run(params string[] args) => RunWithInput(string.Empty, args);

    public async Task<CliResult> RunWithInput(string input, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var reader = new StringReader(input);
        var context = new CliContext
        {
            Out = output,
            Error = error,
            In = reader,
            Time = Time,
            TimeZone = TimeZoneInfo.Utc,
            DataDirectory = DataDirectory,
            VaultPath = VaultDirectory,
            History = History,
            WorkingDirectory = WorkingDirectory,
        };
        var code = await CliApp.RunAsync(args, context);
        return new CliResult(code, output.ToString(), error.ToString());
    }

    /// <summary>Runs a command that must succeed and returns its JSON output.</summary>
    public async Task<JsonNode> Json(params string[] args)
    {
        var result = await Run([.. args, "--json"]);
        Assert.True(result.ExitCode == 0, $"tt {string.Join(' ', args)} failed ({result.ExitCode}): {result.Error}");
        return result.Json;
    }

    public async Task<string> Ok(params string[] args)
    {
        var result = await Run(args);
        Assert.True(result.ExitCode == 0, $"tt {string.Join(' ', args)} failed ({result.ExitCode}): {result.Error}");
        return result.Out;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(DataDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(DataDirectory, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(DataDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal static class JsonExtensions
{
    public static string Str(this JsonNode? node, string name) => node![name]!.GetValue<string>();

    public static Guid Id(this JsonNode? node) => Guid.Parse(node.Str("id"));

    public static List<string> Strings(this JsonNode? node) => node!.AsArray().Select(n => n!.GetValue<string>()).ToList();

    public static List<string> Titles(this JsonNode? node) => node!.AsArray().Select(n => n.Str("title")).ToList();
}
