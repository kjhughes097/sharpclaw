using System.Runtime.InteropServices;

namespace SharpClaw.Sandbox.Infrastructure;

/// <summary>The few Linux calls the runner needs that .NET doesn't expose.</summary>
internal static partial class Native
{
    private const int SIGKILL = 9;
    private const int PR_SET_DUMPABLE = 4;

    /// <summary>Kills every process in a process group (best effort; it may already be gone).</summary>
    public static void KillGroup(int pgid)
    {
        if (OperatingSystem.IsLinux() && pgid > 1)
            _ = kill(-pgid, SIGKILL);
    }

    /// <summary>
    /// Makes this process non-dumpable: its <c>/proc/&lt;pid&gt;</c> files (including <c>environ</c>,
    /// which holds the service secret) become unreadable to code in the sandbox running as the same
    /// user, and it can't be ptraced by it.
    /// </summary>
    public static void MakeNonDumpable()
    {
        if (OperatingSystem.IsLinux())
            _ = prctl(PR_SET_DUMPABLE, 0, 0, 0, 0);
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int kill(int pid, int sig);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int prctl(int option, nuint arg2, nuint arg3, nuint arg4, nuint arg5);
}
