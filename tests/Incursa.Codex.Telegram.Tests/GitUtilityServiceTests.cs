using System.Diagnostics;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Tests;

public sealed class GitUtilityServiceTests
{
    [Fact]
    public void ResolveCurrentBranchDisplay_UsesBranchWhenAvailable()
    {
        string branch = GitUtilityService.ResolveCurrentBranchDisplay("main\n", "abc1234\n");

        Assert.Equal("main", branch);
    }

    [Fact]
    public void ResolveCurrentBranchDisplay_FallsBackToShortHeadWhenDetached()
    {
        string branch = GitUtilityService.ResolveCurrentBranchDisplay("", "abc1234\n");

        Assert.Equal("abc1234", branch);
    }

    [Fact]
    public void ParseStatusSnapshot_ReadsAheadBehindAndDirtyState()
    {
        GitStatusSnapshot snapshot = GitUtilityService.ParseStatusSnapshot(
            "## main...origin/main [ahead 2, behind 1]\n M app/page.tsx\n?? notes.txt\n",
            "main\n",
            "abc1234\n");

        Assert.Equal("main", snapshot.Branch);
        Assert.Equal("1 modified, 1 untracked", snapshot.StatusSummary);
        Assert.False(snapshot.IsClean);
        Assert.False(snapshot.IsDetached);
        Assert.Equal(2, snapshot.AheadCount);
        Assert.Equal(1, snapshot.BehindCount);
        Assert.Equal("origin/main", snapshot.UpstreamName);
    }

    [Fact]
    public void ParseStatusSnapshot_MarksDetachedHead()
    {
        GitStatusSnapshot snapshot = GitUtilityService.ParseStatusSnapshot(
            "## HEAD (no branch)\n",
            "",
            "abc1234\n");

        Assert.Equal("abc1234", snapshot.Branch);
        Assert.True(snapshot.IsClean);
        Assert.True(snapshot.IsDetached);
    }

    [Fact]
    public void ParseChangedFiles_MergesStagedAndUnstagedLists()
    {
        IReadOnlyList<string> files = GitUtilityService.ParseChangedFiles("src/app.ts\nREADME.md\n", "README.md\npackage.json\n");

        Assert.Equal(["src/app.ts", "README.md", "package.json"], files);
    }

    [Fact]
    public async Task CommitAsync_PassesMessageAsDedicatedGitArgument()
    {
        TemporaryDirectory temp = TemporaryDirectory.Create();
        try
        {
            FakeGitExecutor executor = new();
            executor.Results.Enqueue(new GitCommandResult(0, temp.Path, string.Empty));
            executor.Results.Enqueue(new GitCommandResult(0, string.Empty, string.Empty));
            executor.Results.Enqueue(new GitCommandResult(0, "[main abc1234] safe message", string.Empty));
            GitUtilityService service = CreateService(temp.Path, executor);

            await service.CommitAsync(temp.Path, "safe message", CancellationToken.None);

            ProcessStartInfo commitStartInfo = executor.Calls[2];
            Assert.Equal("git", commitStartInfo.FileName);
            Assert.Equal(["commit", "-m", "safe message"], commitStartInfo.ArgumentList);
            Assert.Equal(temp.Path, commitStartInfo.WorkingDirectory);
        }
        finally
        {
            temp.Dispose();
        }
    }

    [Fact]
    public async Task GetMenuStateAsync_ReturnsRichRepoStatus()
    {
        TemporaryDirectory temp = TemporaryDirectory.Create();
        try
        {
            FakeGitExecutor executor = new();
            executor.Results.Enqueue(new GitCommandResult(0, temp.Path, string.Empty));
            executor.Results.Enqueue(new GitCommandResult(0, "## main...origin/main [ahead 2, behind 1]\n M app/page.tsx\n?? notes.txt\n", string.Empty));
            executor.Results.Enqueue(new GitCommandResult(0, "main\n", string.Empty));
            executor.Results.Enqueue(new GitCommandResult(0, "abc1234\n", string.Empty));
            executor.Results.Enqueue(new GitCommandResult(0, "origin\n", string.Empty));
            GitUtilityService service = CreateService(temp.Path, executor);

            GitRepositoryMenuState state = await service.GetMenuStateAsync(temp.Path, CancellationToken.None);

            Assert.Equal(Path.GetFileName(temp.Path), state.ProjectName);
            Assert.Equal(Path.GetFileName(temp.Path), state.RepositoryRootName);
            Assert.Equal("main", state.Branch);
            Assert.Equal("1 modified, 1 untracked", state.StatusSummary);
            Assert.False(state.IsClean);
            Assert.False(state.IsDetached);
            Assert.Equal(2, state.AheadCount);
            Assert.Equal(1, state.BehindCount);
            Assert.Equal("origin", state.RemoteName);
        }
        finally
        {
            temp.Dispose();
        }
    }

    private static GitUtilityService CreateService(string workspaceRoot, FakeGitExecutor executor)
    {
        Directory.CreateDirectory(workspaceRoot);
        IOptions<CodexTelegramOptions> options = Microsoft.Extensions.Options.Options.Create(new CodexTelegramOptions
        {
            Workspace = new CodexWorkspaceOptions
            {
                WorkspaceRoots = [workspaceRoot],
            },
        });
        return new GitUtilityService(new CodexWorkspaceBrowser(options), executor, NullLogger<GitUtilityService>.Instance);
    }

    private sealed class FakeGitExecutor : IGitCommandExecutor
    {
        public List<ProcessStartInfo> Calls { get; } = [];

        public Queue<GitCommandResult> Results { get; } = [];

        public Task<GitCommandResult> ExecuteAsync(ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls.Add(startInfo);
            return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : new GitCommandResult(0, string.Empty, string.Empty));
        }
    }
}
