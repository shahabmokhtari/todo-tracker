using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TodoTracker.Server.Plugins.AgentChat;

/// <summary>A started agent process: its three pipes, how it ended, and a way to stop it with everything it started.</summary>
public interface IAgentProcess : IDisposable
{
    int Id { get; }

    Stream Input { get; }

    Stream Output { get; }

    /// <summary>The agent's stderr.</summary>
    Stream Diagnostics { get; }

    /// <summary>Completes with the exit code when the process ends.</summary>
    Task<int> Exited { get; }

    /// <summary>Stops the agent and every process it started.</summary>
    void KillTree();
}

public static class AgentProcess
{
    /// <summary>
    /// Starts an agent. On Windows the child gets only its three pipes (no other inherited handles: Copilot CLI
    /// 1.0.93 crashes when it inherits more) and runs in a job object, so stopping it also stops everything it started.
    /// </summary>
    public static IAgentProcess Start(AgentLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        return OperatingSystem.IsWindows() ? WindowsAgentProcess.Start(launch) : new PortableAgentProcess(launch);
    }
}

/// <summary>macOS and Linux: a plain child process.</summary>
internal sealed class PortableAgentProcess : IAgentProcess
{
    private readonly Process _process;

    public PortableAgentProcess(AgentLaunch launch)
    {
        var start = new ProcessStartInfo(launch.Command)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = launch.WorkingDirectory,
        };
        foreach (var argument in launch.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in launch.Environment)
        {
            start.Environment[name] = value;
        }

        _process = Process.Start(start) ?? throw new AcpException($"Couldn't start {launch.Command}.");
        Exited = WaitAsync();
    }

    public int Id => _process.Id;

    public Stream Input => _process.StandardInput.BaseStream;

    public Stream Output => _process.StandardOutput.BaseStream;

    public Stream Diagnostics => _process.StandardError.BaseStream;

    public Task<int> Exited { get; }

    public void KillTree()
    {
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
        }
    }

    public void Dispose() => _process.Dispose();

    private async Task<int> WaitAsync()
    {
        await _process.WaitForExitAsync().ConfigureAwait(false);
        return _process.ExitCode;
    }
}

/// <summary>Windows: CreateProcess with an explicit handle list, inside a kill-on-close job.</summary>
internal sealed partial class WindowsAgentProcess : IAgentProcess
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateSuspended = 0x00000004;
    private const int StartfUseStdHandles = 0x00000100;
    private const int HandleFlagInherit = 0x00000001;
    private static readonly IntPtr ProcThreadAttributeHandleList = 0x00020002;

    private readonly SafeProcessHandle _process;
    private readonly SafeFileHandle _job;
    private readonly RegisteredWaitHandle _waiter;
    private readonly ManualResetEvent _exitEvent;
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private WindowsAgentProcess(int id, SafeProcessHandle process, SafeFileHandle job, Stream input, Stream output, Stream error)
    {
        Id = id;
        _process = process;
        _job = job;
        Input = input;
        Output = output;
        Diagnostics = error;
        _exitEvent = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false) };
        _waiter = ThreadPool.RegisterWaitForSingleObject(_exitEvent, (_, _) => _exited.TrySetResult(GetExitCodeProcess(_process, out var code) ? (int)code : -1), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    public int Id { get; }

    public Stream Input { get; }

    public Stream Output { get; }

    public Stream Diagnostics { get; }

    public Task<int> Exited => _exited.Task;

    public static WindowsAgentProcess Start(AgentLaunch launch)
    {
        var executable = Resolve(launch.Command);
        var (application, commandLine) = CommandLine(executable, launch.Arguments);

        CreatePipe(out var stdinRead, out var stdinWrite, inheritRead: true);
        CreatePipe(out var stdoutRead, out var stdoutWrite, inheritRead: false);
        CreatePipe(out var stderrRead, out var stderrWrite, inheritRead: false);
        var attributes = IntPtr.Zero;
        var handles = IntPtr.Zero;
        var started = false;
        try
        {
            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            Check(InitializeProcThreadAttributeList(attributes, 1, 0, ref size));
            handles = Marshal.AllocHGlobal(IntPtr.Size * 3);
            Marshal.WriteIntPtr(handles, 0, stdinRead.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, IntPtr.Size, stdoutWrite.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, IntPtr.Size * 2, stderrWrite.DangerousGetHandle());
            Check(UpdateProcThreadAttribute(attributes, 0, ProcThreadAttributeHandleList, handles, IntPtr.Size * 3, IntPtr.Zero, IntPtr.Zero));

            var info = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    cb = Marshal.SizeOf<StartupInfoEx>(),
                    dwFlags = StartfUseStdHandles,
                    hStdInput = stdinRead.DangerousGetHandle(),
                    hStdOutput = stdoutWrite.DangerousGetHandle(),
                    hStdError = stderrWrite.DangerousGetHandle(),
                },
                lpAttributeList = attributes,
            };
            var environment = EnvironmentBlock(launch.Environment);
            if (!CreateProcessW(
                application,
                (commandLine + "\0").ToCharArray(),
                IntPtr.Zero,
                IntPtr.Zero,
                bInheritHandles: true,
                ExtendedStartupInfoPresent | CreateNoWindow | CreateUnicodeEnvironment | CreateSuspended,
                environment,
                launch.WorkingDirectory,
                ref info,
                out var process))
            {
                throw new AcpException($"Couldn't start {launch.Command}: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
            }

            var processHandle = new SafeProcessHandle(process.hProcess, ownsHandle: true);
            using var thread = new SafeProcessHandle(process.hThread, ownsHandle: true);
            var job = CreateJobObjectW(IntPtr.Zero, null);
            try
            {
                var limits = new JobObjectExtendedLimitInformation { BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = 0x2000 } }; // kill on close
                Check(!job.IsInvalid && SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<JobObjectExtendedLimitInformation>()));
                Check(AssignProcessToJobObject(job, processHandle));
            }
            catch
            {
                // Never leave a suspended child (or its handles) behind.
                TerminateProcess(processHandle, 1);
                processHandle.Dispose();
                job.Dispose();
                throw;
            }

            ResumeThread(thread);
            started = true;
            return new WindowsAgentProcess(
                process.dwProcessId,
                processHandle,
                job,
                new FileStream(stdinWrite, FileAccess.Write, 1),
                new FileStream(stdoutRead, FileAccess.Read, 1),
                new FileStream(stderrRead, FileAccess.Read, 1));
        }
        finally
        {
            // The child has its own copies now.
            stdinRead.Dispose();
            stdoutWrite.Dispose();
            stderrWrite.Dispose();
            if (!started)
            {
                stdinWrite.Dispose();
                stdoutRead.Dispose();
                stderrRead.Dispose();
            }

            if (attributes != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }

            if (handles != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(handles);
            }
        }
    }

    public void KillTree()
    {
        // Ends every process in the job: the agent, its MCP servers, npx's node…
        TerminateJobObject(_job, 1);
    }

    public void Dispose()
    {
        _waiter.Unregister(null);
        _exitEvent.Dispose();
        Input.Dispose();
        Output.Dispose();
        Diagnostics.Dispose();
        _job.Dispose();
        _process.Dispose();
    }

    private static readonly char[] CmdSpecial = [' ', '\t', '"', '&', '|', '<', '>', '^', '(', ')', ',', ';', '=', '!'];

    /// <summary>A bare name ("dotnet") is looked up on the PATH, like the shell (and Process.Start) would.</summary>
    private static string Resolve(string command)
    {
        if (Path.IsPathRooted(command) || command.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return command;
        }

        var extensions = Path.HasExtension(command)
            ? [string.Empty]
            : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(dir.Trim('"'), command + extension.ToLowerInvariant());
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new AcpException($"Couldn't find {command}.");
    }

    private static void CreatePipe(out SafeFileHandle read, out SafeFileHandle write, bool inheritRead)
    {
        var security = new SecurityAttributes { nLength = Marshal.SizeOf<SecurityAttributes>(), bInheritHandle = 1 };
        Check(CreatePipeNative(out read, out write, ref security, 0));

        // Only the child's end stays inheritable (and the handle list limits it to this child anyway).
        Check(SetHandleInformation(inheritRead ? write : read, HandleFlagInherit, 0));
    }

    private static string EnvironmentBlock(IReadOnlyDictionary<string, string?> extra)
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            variables[(string)entry.Key] = (string?)entry.Value ?? string.Empty;
        }

        foreach (var (name, value) in extra)
        {
            if (value is null)
            {
                variables.Remove(name);
            }
            else
            {
                variables[name] = value;
            }
        }

        var block = new StringBuilder();
        foreach (var (name, value) in variables)
        {
            block.Append(name).Append('=').Append(value).Append('\0');
        }

        return block.Append('\0').ToString();
    }

    /// <summary>
    /// The program to start and its command line. Scripts (e.g. npx.cmd, copilot.cmd) run through cmd.exe:
    /// /s /c "&lt;whole command&gt;" keeps their quoting intact, and every argument with a character cmd.exe treats
    /// specially (&amp; | &lt; &gt; ^ ( ) and so on) is quoted so it stays one plain argument.
    /// </summary>
    internal static (string Application, string CommandLine) CommandLine(string executable, IEnumerable<string> arguments)
    {
        var extension = Path.GetExtension(executable);
        var script = extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase);
        var command = string.Join(' ', new[] { executable }.Concat(arguments).Select(a => Quote(a, script)));
        return script
            ? (Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"cmd.exe /d /s /c \"{command}\"")
            : (executable, command);
    }

    /// <summary>Quotes one argument the way the C runtime parses command lines.</summary>
    private static string Quote(string argument, bool forCmd)
    {
        if (argument.Length > 0 && argument.IndexOfAny(forCmd ? CmdSpecial : [' ', '\t', '"']) < 0)
        {
            return argument;
        }

        var quoted = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            quoted.Append('\\', c == '"' ? (backslashes * 2) + 1 : backslashes).Append(c);
            backslashes = 0;
        }

        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }

    private static void Check(bool ok)
    {
        if (!ok)
        {
            throw new AcpException($"Couldn't start the agent: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreatePipe", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreatePipeNative(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, int size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetHandleInformation(SafeFileHandle handle, int mask, int flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);

    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string application,
        [In, Out] char[] commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint creationFlags,
        string environment,
        string currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref JobObjectExtendedLimitInformation info, int length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObject(SafeFileHandle job, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint ResumeThread(SafeProcessHandle thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
}
