using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SharpClaw.Agent.Tools.Workspace;

/// <summary>A workspace path broke a rule at execution time (the workspace changed after the call was assessed).</summary>
public class WorkspacePathException(string message) : IOException(message);

/// <summary>A path component turned out to be a symbolic link (or not a directory) when it was opened.</summary>
public sealed class WorkspaceLinkException(string message) : WorkspacePathException(message);

/// <summary>
/// File operations under a root directory that never follow symbolic links (external tools spec
/// section 8, tech-debt #10). <see cref="WorkspaceRoot.Resolve"/> checks a path before a call runs,
/// but sandboxes now write into the same workspace, so a component could become a symlink between
/// that check and the I/O. Here every directory component is opened with <c>O_NOFOLLOW</c> relative
/// to its parent (<c>openat</c>), and the final name is opened, created, renamed or removed relative
/// to that directory handle, so a swapped-in link fails the operation instead of redirecting it.
///
/// Linux only (the agent and sandbox run in Linux containers); elsewhere it falls back to the
/// ordinary path-based APIs, which keep the earlier check-then-use behaviour.
/// </summary>
internal static partial class NoFollow
{
    // Flag values differ between x86-64 and ARM (asm-generic) for O_DIRECTORY and O_NOFOLLOW.
    private static readonly bool IsArm = RuntimeInformation.ProcessArchitecture is Architecture.Arm or Architecture.Arm64;
    private const int O_RDONLY = 0, O_WRONLY = 1, O_CREAT = 0x40, O_EXCL = 0x80, O_CLOEXEC = 0x80000;
    private static readonly int O_DIRECTORY = IsArm ? 0x4000 : 0x10000;
    private static readonly int O_NOFOLLOW = IsArm ? 0x8000 : 0x20000;
    private const uint RENAME_NOREPLACE = 1;
    private const int ENOENT = 2, EEXIST = 17, ENOTDIR = 20, EISDIR = 21, ELOOP = 40;

    private static bool Native => OperatingSystem.IsLinux();

    /// <summary>Opens a file for reading. The file and every directory above it must not be links.</summary>
    public static FileStream OpenRead(string root, string relative)
    {
        if (!Native)
            return File.OpenRead(Path.Combine(root, relative));
        using var parent = OpenParent(root, relative, create: false, out var name);
        var fd = Check(openat(Fd(parent), name, O_RDONLY | O_NOFOLLOW | O_CLOEXEC, 0), relative);
        return new FileStream(new SafeFileHandle(fd, ownsHandle: true), FileAccess.Read);
    }

    public static async Task<byte[]> ReadAllBytesAsync(string root, string relative, CancellationToken ct)
    {
        await using var stream = OpenRead(root, relative);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    /// <summary>
    /// Writes via a temporary file in the same directory and a rename, so a crash never leaves a
    /// partial file. Missing parent directories are created (each one without following links).
    /// </summary>
    public static async Task WriteAtomicAsync(string root, string relative, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        if (!Native)
        {
            await ManagedWriteAtomicAsync(Path.Combine(root, relative), content, ct);
            return;
        }

        using var parent = OpenParent(root, relative, create: true, out var name);
        var temp = $".{name}.{Guid.NewGuid():N}.tmp";
        var fd = Check(openat(Fd(parent), temp, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0b110_110_110), relative);
        try
        {
            await using (var stream = new FileStream(new SafeFileHandle(fd, ownsHandle: true), FileAccess.Write))
                await stream.WriteAsync(content, ct);
            // rename(2) replaces the final name itself; it never follows a link there.
            Check(renameat2(Fd(parent), temp, Fd(parent), name, 0), relative);
        }
        catch
        {
            _ = unlinkat(Fd(parent), temp, 0);
            throw;
        }
    }

    /// <summary>Removes a file (a link at the final name is removed itself, never its target).</summary>
    public static void Delete(string root, string relative)
    {
        if (!Native)
        {
            File.Delete(Path.Combine(root, relative));
            return;
        }
        using var parent = OpenParent(root, relative, create: false, out var name);
        Check(unlinkat(Fd(parent), name, 0), relative);
    }

    /// <summary>Renames a file. Without <paramref name="overwrite"/> an existing destination fails atomically.</summary>
    public static void Move(string root, string from, string to, bool overwrite)
    {
        if (!Native)
        {
            var target = Path.Combine(root, to);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(Path.Combine(root, from), target, overwrite);
            return;
        }
        using var fromParent = OpenParent(root, from, create: false, out var fromName);
        using var toParent = OpenParent(root, to, create: true, out var toName);
        Check(renameat2(Fd(fromParent), fromName, Fd(toParent), toName, overwrite ? 0 : RENAME_NOREPLACE), to);
    }

    /// <summary>
    /// Opens a directory (no component may be a link) and returns a path that names exactly that
    /// directory while the handle is open: <c>/proc/self/fd/N</c> on Linux, otherwise the plain path.
    /// </summary>
    public static (IDisposable Handle, string Path) OpenDirectory(string root, string relative)
    {
        if (!Native)
            return (new NoHandle(), Path.Combine(root, relative));
        var dir = OpenDirectoryHandle(root, relative == "." ? Array.Empty<string>() : relative.Split('/'), create: false, relative);
        return (dir, $"/proc/self/fd/{Fd(dir)}");
    }

    private static async Task ManagedWriteAtomicAsync(string fullPath, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
                await stream.WriteAsync(content, ct);
            File.Move(temp, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    /// <summary>Opens the directory that holds <paramref name="relative"/>'s final component.</summary>
    private static SafeFileHandle OpenParent(string root, string relative, bool create, out string name)
    {
        var parts = relative.Split('/');
        name = parts[^1];
        if (parts.Any(p => p is "" or "." or ".."))
            throw new ArgumentException("path must be a normalised relative path", nameof(relative));
        return OpenDirectoryHandle(root, parts[..^1], create, relative);
    }

    private static SafeFileHandle OpenDirectoryHandle(string root, string[] parts, bool create, string relative)
    {
        // The root itself comes from trusted configuration, so it may be reached through links.
        var current = new SafeFileHandle(Check(open(root, O_RDONLY | O_DIRECTORY | O_CLOEXEC, 0), "."), ownsHandle: true);
        try
        {
            foreach (var part in parts)
            {
                var flags = O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC;
                var fd = openat(Fd(current), part, flags, 0);
                if (fd < 0 && create && Marshal.GetLastPInvokeError() == ENOENT)
                {
                    if (mkdirat(Fd(current), part, 0b111_111_111) < 0 && Marshal.GetLastPInvokeError() != EEXIST)
                        Fail(relative);
                    fd = openat(Fd(current), part, flags, 0);
                }
                var next = new SafeFileHandle(Check(fd, relative), ownsHandle: true);
                current.Dispose();
                current = next;
            }
            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static int Fd(SafeFileHandle handle) => (int)handle.DangerousGetHandle();

    private static int Check(int result, string relative) => result >= 0 ? result : Fail(relative);

    // Messages carry the workspace-relative path only (the model already knows it), never content.
    private static int Fail(string relative)
    {
        var errno = Marshal.GetLastPInvokeError();
        throw errno switch
        {
            ELOOP or ENOTDIR => new WorkspaceLinkException($"'{relative}' crosses a symbolic link or a non-directory."),
            ENOENT => new FileNotFoundException($"'{relative}' does not exist."),
            EEXIST => new IOException($"'{relative}' already exists."),
            EISDIR => new IOException($"'{relative}' is a directory."),
            _ => new IOException($"I/O error {errno} on '{relative}'."),
        };
    }

    private sealed class NoHandle : IDisposable
    {
        public void Dispose() { }
    }

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int open(string path, int flags, int mode);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int openat(int dirfd, string path, int flags, int mode);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int mkdirat(int dirfd, string path, int mode);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int unlinkat(int dirfd, string path, int flags);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int renameat2(int olddirfd, string oldpath, int newdirfd, string newpath, uint flags);
}
