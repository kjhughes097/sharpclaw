using System.Text.Json;
using SharpClaw.Agent.Tools.Workspace;

namespace SharpClaw.Agent.Tests;

/// <summary>
/// External tools spec section 8 (tech-debt #10): a path that passed the checks can become a
/// symbolic link before the I/O runs (a sandbox writes into the same workspace). Execution re-checks
/// the path and then opens every component without following links, so the swap fails the call
/// instead of redirecting it. The tool tests run the execute step directly, as if the link appeared
/// after assessment; the <see cref="NoFollow"/> tests cover a link appearing after that re-check.
/// </summary>
public partial class WorkspaceToolsTests
{
    /// <summary>Replaces the workspace folder <paramref name="relative"/> with a link to the outside folder.</summary>
    private void SwapForLink(string relative)
    {
        var full = Path.Combine(_root, relative);
        Directory.Delete(full, recursive: true);
        Directory.CreateSymbolicLink(full, _outside);
    }

    private Task<Exception> Raced(string tool, object args) =>
        Assert.ThrowsAnyAsync<Exception>(() => _tools[tool].ExecuteUncheckedAsync(JsonSerializer.SerializeToElement(args), CancellationToken.None));

    [Fact]
    public async Task Reads_through_a_swapped_in_directory_link_fail()
    {
        Put("notes/secret.txt", "inside");
        SwapForLink("notes");

        Assert.IsAssignableFrom<WorkspacePathException>(await Raced("workspace_read", new { path = "notes/secret.txt" }));
        Assert.IsAssignableFrom<WorkspacePathException>(await Raced("workspace_edit", new { path = "notes/secret.txt", oldText = "outside", newText = "pwned" }));
        Assert.IsAssignableFrom<WorkspacePathException>(await Raced("workspace_copy", new { from = "notes/secret.txt", to = "copy.txt" }));
        Assert.False(File.Exists(Path.Combine(_root, "copy.txt")));
        Assert.Equal("outside secret", File.ReadAllText(Path.Combine(_outside, "secret.txt")));
    }

    [Fact]
    public async Task A_swapped_in_final_link_is_not_followed_on_read()
    {
        File.CreateSymbolicLink(Path.Combine(_root, "file.txt"), Path.Combine(_outside, "secret.txt"));

        Assert.IsAssignableFrom<WorkspacePathException>(await Raced("workspace_read", new { path = "file.txt" }));
    }

    [Fact]
    public async Task Writes_through_a_swapped_in_directory_link_fail_and_leave_the_outside_untouched()
    {
        Put("notes/a.txt", "a");
        SwapForLink("notes");

        Assert.IsAssignableFrom<WorkspacePathException>(await Raced("workspace_write", new { path = "notes/new.txt", content = "x" }));
        Assert.IsAssignableFrom<WorkspacePathException>(await Raced("workspace_append", new { path = "notes/secret.txt", content = "x" }));
        Assert.IsAssignableFrom<WorkspacePathException>(await Raced("workspace_delete", new { path = "notes/secret.txt" }));
        Assert.IsAssignableFrom<WorkspacePathException>(await Raced("workspace_move", new { from = "notes/secret.txt", to = "moved.txt" }));

        Assert.Equal(new[] { "secret.txt" }, Directory.GetFiles(_outside).Select(Path.GetFileName));
        Assert.Equal("outside secret", File.ReadAllText(Path.Combine(_outside, "secret.txt")));
    }

    [Fact]
    public async Task NoFollow_writing_over_a_final_link_replaces_the_link_not_its_target()
    {
        var link = Path.Combine(_root, "file.txt");
        File.CreateSymbolicLink(link, Path.Combine(_outside, "secret.txt"));

        await NoFollow.WriteAtomicAsync(_root, "file.txt", "new"u8.ToArray(), default);

        Assert.Null(new FileInfo(link).LinkTarget);
        Assert.Equal("new", File.ReadAllText(link));
        Assert.Equal("outside secret", File.ReadAllText(Path.Combine(_outside, "secret.txt")));
    }

    [Fact]
    public async Task Listing_a_swapped_in_directory_link_fails()
    {
        Put("notes/a.txt", "a");
        SwapForLink("notes");

        Assert.IsAssignableFrom<WorkspacePathException>(await Raced("workspace_list", new { path = "notes" }));
    }

    [Fact]
    public void NoFollow_move_without_overwrite_refuses_an_existing_destination_atomically()
    {
        Put("a.txt", "a");
        Put("b.txt", "b");

        // The tool checks existence first; a destination created after that check must still survive.
        Assert.ThrowsAny<IOException>(() => NoFollow.Move(_root, "a.txt", "b.txt", overwrite: false));
        Assert.Equal("b", File.ReadAllText(Path.Combine(_root, "b.txt")));
    }

    // ----- NoFollow on its own: the path was checked, then a component became a link. -----

    [Fact]
    public async Task NoFollow_refuses_every_operation_through_a_directory_link()
    {
        Put("notes/a.txt", "a");
        SwapForLink("notes");

        Assert.Throws<WorkspaceLinkException>(() => NoFollow.OpenRead(_root, "notes/secret.txt"));
        await Assert.ThrowsAsync<WorkspaceLinkException>(() => NoFollow.WriteAtomicAsync(_root, "notes/x.txt", new byte[] { 1 }, default));
        await Assert.ThrowsAsync<WorkspaceLinkException>(() => NoFollow.WriteAtomicAsync(_root, "notes/sub/x.txt", new byte[] { 1 }, default));
        Assert.Throws<WorkspaceLinkException>(() => NoFollow.Delete(_root, "notes/secret.txt"));
        Assert.Throws<WorkspaceLinkException>(() => NoFollow.Move(_root, "notes/secret.txt", "m.txt", overwrite: false));
        Put("b.txt", "b");
        Assert.Throws<WorkspaceLinkException>(() => NoFollow.Move(_root, "b.txt", "notes/b.txt", overwrite: false));
        Assert.Throws<WorkspaceLinkException>(() => NoFollow.OpenDirectory(_root, "notes"));

        Assert.Equal(new[] { "secret.txt" }, Directory.GetFiles(_outside).Select(Path.GetFileName));
        Assert.Equal("outside secret", File.ReadAllText(Path.Combine(_outside, "secret.txt")));
    }

    [Fact]
    public void NoFollow_refuses_to_read_a_final_link()
    {
        File.CreateSymbolicLink(Path.Combine(_root, "file.txt"), Path.Combine(_outside, "secret.txt"));

        Assert.Throws<WorkspaceLinkException>(() => NoFollow.OpenRead(_root, "file.txt"));
    }

    [Fact]
    public async Task NoFollow_writes_create_missing_folders_and_round_trip()
    {
        await NoFollow.WriteAtomicAsync(_root, "a/b/c.txt", "hello"u8.ToArray(), default);

        Assert.Equal("hello", File.ReadAllText(Path.Combine(_root, "a", "b", "c.txt")));
        Assert.Equal("hello"u8.ToArray(), await NoFollow.ReadAllBytesAsync(_root, "a/b/c.txt", default));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "a", "b")));
    }

    [Fact]
    public void NoFollow_directory_path_lists_the_opened_directory()
    {
        Put("notes/a.txt", "a");

        var (handle, path) = NoFollow.OpenDirectory(_root, "notes");
        using (handle)
            Assert.Equal(new[] { "a.txt" }, Directory.GetFiles(path).Select(Path.GetFileName));
    }
}
