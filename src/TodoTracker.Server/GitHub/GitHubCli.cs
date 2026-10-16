using System.Diagnostics;
using System.Text.RegularExpressions;

namespace TodoTracker.Server.GitHub;

/// <summary>The GitHub CLI (<c>gh</c>): the sign-in Todo Tracker uses, and making repositories.</summary>
public interface IGitHubCli
{
    /// <summary>A token: <c>GH_TOKEN</c>, <c>GITHUB_TOKEN</c>, or the one <c>gh</c> is signed in with (null: none).</summary>
    Task<string?> TokenAsync(CancellationToken cancellationToken);

    /// <summary>Whether <c>gh</c> is installed (a token from the environment works without it).</summary>
    bool Installed { get; }

    /// <summary>Makes a private repository with <c>gh repo create</c>; returns its "owner/name".</summary>
    Task<string> CreateRepoAsync(string name, string description, CancellationToken cancellationToken);
}

/// <summary>
/// Runs <c>gh</c> on Windows, macOS and Linux. Apps started from the Dock, Finder or a desktop launcher get a short
/// PATH (no Homebrew, no ~/.local/bin), so the usual install places are looked in too.
/// </summary>
public sealed partial class GitHubCli : IGitHubCli
{
    public const string InstallHint = "Install the GitHub CLI (https://cli.github.com) and run: gh auth login";

    private static readonly TimeSpan Short = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(90);

    private readonly Lazy<string?> _path = new(() => Find(Environment.GetEnvironmentVariable("PATH"), File.Exists));

    public bool Installed => _path.Value is not null;

    /// <summary>The token, asking <c>gh</c> if the environment has none (blocks: call it off the UI thread).</summary>
    public static string? Token()
    {
        if (Environment.GetEnvironmentVariable("GH_TOKEN") is { Length: > 0 } gh)
        {
            return gh;
        }

        if (Environment.GetEnvironmentVariable("GITHUB_TOKEN") is { Length: > 0 } github)
        {
            return github;
        }

        var path = Find(Environment.GetEnvironmentVariable("PATH"), File.Exists);
        if (path is null)
        {
            return null;
        }

        var (code, output, _) = Run(path, ["auth", "token"], Short);
        return code == 0 && output.Trim() is { Length: > 0 } token ? token : null;
    }

    public Task<string?> TokenAsync(CancellationToken cancellationToken) => Task.Run(Token, cancellationToken);

    public async Task<string> CreateRepoAsync(string name, string description, CancellationToken cancellationToken)
    {
        var path = _path.Value ?? throw new InvalidOperationException(InstallHint);
        var (code, output, error) = await Task.Run(
            () => Run(path, ["repo", "create", name, "--private", "--description", description, "--disable-wiki"], Long),
            cancellationToken).ConfigureAwait(false);
        if (code != 0)
        {
            throw new InvalidOperationException($"gh repo create {name}: {FirstLine(error) ?? FirstLine(output) ?? $"exit code {code}"}");
        }

        return RepoOf(output) ?? throw new InvalidOperationException($"gh repo create {name} didn't say where it made it ({FirstLine(output)}).");
    }

    /// <summary>"owner/name" from what <c>gh repo create</c> prints (https://github.com/owner/name).</summary>
    public static string? RepoOf(string output)
    {
        var match = RepoUrl().Match(output ?? string.Empty);
        return match.Success ? $"{match.Groups[1].Value}/{match.Groups[2].Value}" : null;
    }

    /// <summary>
    /// Where <c>gh</c> is: on the PATH, else where installers put it (winget/MSI, Scoop; Homebrew on Apple silicon and
    /// Intel; Linux packages, Snap, Linuxbrew, ~/.local/bin).
    /// </summary>
    public static string? Find(string? pathVariable, Func<string, bool> exists, bool? windows = null, string? home = null)
    {
        ArgumentNullException.ThrowIfNull(exists);
        var onWindows = windows ?? OperatingSystem.IsWindows();
        var file = onWindows ? "gh.exe" : "gh";
        var userHome = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dirs = (pathVariable ?? string.Empty).Split(onWindows ? ';' : ':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (onWindows)
        {
            dirs.AddRange(
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "GitHub CLI"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "GitHub CLI"),
                Path.Combine(userHome, "scoop", "shims"),
            ]);
        }
        else
        {
            dirs.AddRange(["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/snap/bin", "/home/linuxbrew/.linuxbrew/bin", $"{userHome}/.local/bin", $"{userHome}/bin"]);
        }

        // Joined by hand on Linux and macOS (the tests ask for those paths on Windows too).
        return dirs.Where(d => d.Length > 0).Select(d => onWindows ? Path.Combine(d, file) : $"{d.TrimEnd('/')}/{file}").FirstOrDefault(exists);
    }

    private static (int Code, string Output, string Error) Run(string path, IReadOnlyList<string> args, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        // Never wait on a question nobody sees.
        start.Environment["GH_PROMPT_DISABLED"] = "1";
        start.Environment["NO_COLOR"] = "1";
        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return (-1, string.Empty, "gh didn't start");
            }

            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeout))
            {
                process.Kill(entireProcessTree: true);
                return (-1, string.Empty, "gh took too long");
            }

            return (process.ExitCode, output.Result, error.Result);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return (-1, string.Empty, ex.Message);
        }
    }

    private static string? FirstLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    [GeneratedRegex(@"https://github\.com/([A-Za-z0-9-]+)/([A-Za-z0-9._-]+?)(?:\.git)?\s*$", RegexOptions.Multiline)]
    private static partial Regex RepoUrl();
}
