using CompanionOS.Core.Modules;
using Xunit;

namespace CompanionOS.Core.Tests;

/// <summary>
/// Containment proof for the workspace. Every rejection here is an escape route
/// that must stay closed: traversal, absolute paths, drive letters, alternate data
/// streams, and the IRIS:// label being mistaken for a real scheme with authority.
/// </summary>
public sealed class WorkspacePathGuardTests : IDisposable
{
    private readonly string _root;

    public WorkspacePathGuardTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "iris-ws-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("notes/today.md")]
    [InlineData("IRIS://notes/today.md")]
    [InlineData("tasks\\2026-02-01-fix.md")]
    public void PathsInsideTheRootResolve(string requested)
    {
        Assert.True(WorkspacePathGuard.TryResolve(_root, requested, out string full, out _));
        Assert.StartsWith(Path.GetFullPath(_root), full);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("notes/../../outside.txt")]
    [InlineData("..\\..\\Windows\\win.ini")]
    public void TraversalIsRejected(string requested)
    {
        Assert.False(WorkspacePathGuard.TryResolve(_root, requested, out _, out string reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Theory]
    [InlineData("C:/Windows/system32/drivers/etc/hosts")]
    [InlineData("C:\\secrets.txt")]
    [InlineData("/etc/passwd")]
    public void AbsoluteAndDrivePathsAreRejected(string requested)
    {
        Assert.False(WorkspacePathGuard.TryResolve(_root, requested, out _, out _));
    }

    [Fact]
    public void AlternateDataStreamsAreRejected()
    {
        Assert.False(WorkspacePathGuard.TryResolve(_root, "notes/today.md:hidden", out _, out string reason));
        Assert.Contains(":", reason);
    }

    [Fact]
    public void EmptyPathIsRejectedWithGuidance()
    {
        Assert.False(WorkspacePathGuard.TryResolve(_root, "   ", out _, out string reason));
        Assert.Contains("workspace", reason);
    }

    [Fact]
    public void MissingWorkspaceRootIsRejectedWithGuidance()
    {
        string missingRoot = Path.Combine(_root, "missing");
        Assert.False(WorkspacePathGuard.TryResolve(missingRoot, "notes/today.md", out _, out string reason));
        Assert.Contains("workspace", reason);
    }

    [Fact]
    public void OnlyTextShapedFilesAreWritable()
    {
        Assert.True(WorkspacePathGuard.IsWritableFileType("notes/a.md"));
        Assert.True(WorkspacePathGuard.IsWritableFileType("a.json"));
        Assert.False(WorkspacePathGuard.IsWritableFileType("a.exe"));
        Assert.False(WorkspacePathGuard.IsWritableFileType("a.dll"));
        Assert.False(WorkspacePathGuard.IsWritableFileType("a"));
    }

    [Fact]
    public void VirtualPathIsALabelOverTheRealRelativePath()
    {
        Assert.True(WorkspacePathGuard.TryResolve(_root, "notes/today.md", out string full, out _));
        string virtualPath = WorkspacePathGuard.ToVirtualPath(_root, full);
        Assert.Equal("IRIS://notes/today.md", virtualPath);
    }

    [Fact]
    public void RoundTripSurvivesWriteAndRestartStyleReRead()
    {
        // Persistence check with a real file: unicode content (the charmap case —
        // a full-width colon in a YouTube title) written as UTF-8 and read back
        // by a fresh resolve, the way a restarted process would.
        Assert.True(WorkspacePathGuard.TryResolve(_root, "notes/title.md", out string full, out _));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        const string title = "【MV】Yates XV：Midnight Signal（Official）";
        File.WriteAllText(full, title, System.Text.Encoding.UTF8);

        Assert.True(WorkspacePathGuard.TryResolve(_root, "IRIS://notes/title.md", out string reFull, out _));
        Assert.Equal(title, File.ReadAllText(reFull, System.Text.Encoding.UTF8));
    }
}
