using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Incursa.Codex.Telegram.Models;
using Microsoft.Extensions.Logging;

namespace Incursa.Codex.Telegram.Services;

internal interface IGitUtilityService
{
    Task<GitRepositoryMenuState> GetMenuStateAsync(string workingDirectory, CancellationToken cancellationToken);

    Task<string> GetStatusAsync(string workingDirectory, CancellationToken cancellationToken);

    Task<string> GetDiffSummaryAsync(string workingDirectory, CancellationToken cancellationToken);

    Task<GitActionResult> CommitAsync(string workingDirectory, string message, CancellationToken cancellationToken);

    Task<GitActionResult> PushAsync(string workingDirectory, CancellationToken cancellationToken);

    Task<GitActionResult> PullAsync(string workingDirectory, CancellationToken cancellationToken);

    Task<GitBranchMenuState> GetBranchMenuStateAsync(string workingDirectory, CancellationToken cancellationToken);

    Task<GitActionResult> CheckoutBranchAsync(string workingDirectory, string branchName, CancellationToken cancellationToken);
}

internal sealed record GitRepositoryMenuState(
    string ProjectName,
    string WorkingDirectory,
    string Branch,
    string StatusSummary,
    string? RemoteName,
    string RepositoryRootName,
    bool IsClean,
    bool IsDetached,
    int AheadCount,
    int BehindCount);

internal sealed record GitBranchMenuState(
    string ProjectName,
    string WorkingDirectory,
    string CurrentBranch,
    bool IsDetached,
    IReadOnlyList<string> LocalBranches);

internal sealed record GitStatusSnapshot(
    string Branch,
    string StatusSummary,
    bool IsClean,
    bool IsDetached,
    int AheadCount,
    int BehindCount,
    string? UpstreamName);

internal sealed record GitActionResult(bool Success, string Message);

internal sealed record GitCommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool TimedOut { get; init; }

    public string CombinedText
        => string.IsNullOrWhiteSpace(StandardError)
            ? StandardOutput.Trim()
            : (StandardOutput + Environment.NewLine + StandardError).Trim();
}

internal interface IGitCommandExecutor
{
    Task<GitCommandResult> ExecuteAsync(ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken cancellationToken);
}

internal sealed class GitCommandExecutor : IGitCommandExecutor
{
    public async Task<GitCommandResult> ExecuteAsync(ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start {startInfo.FileName}.");
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            return new GitCommandResult(
                -1,
                await stdoutTask.ConfigureAwait(false),
                await stderrTask.ConfigureAwait(false))
            {
                TimedOut = true,
            };
        }

        return new GitCommandResult(
            process.ExitCode,
            await stdoutTask.ConfigureAwait(false),
            await stderrTask.ConfigureAwait(false));
    }
}

internal sealed class GitUtilityService : IGitUtilityService
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(30);
    private static readonly Regex AheadBehindRegex = new(@"\[(?<parts>[^\]]+)\]", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex AheadRegex = new(@"ahead\s+(?<count>\d+)", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex BehindRegex = new(@"behind\s+(?<count>\d+)", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly CodexWorkspaceBrowser _workspaceBrowser;
    private readonly IGitCommandExecutor _executor;
    private readonly ILogger<GitUtilityService> _logger;

    public GitUtilityService(
        CodexWorkspaceBrowser workspaceBrowser,
        IGitCommandExecutor executor,
        ILogger<GitUtilityService> logger)
    {
        _workspaceBrowser = workspaceBrowser;
        _executor = executor;
        _logger = logger;
    }

    public async Task<GitRepositoryMenuState> GetMenuStateAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        string repoRoot = await ResolveRepositoryRootAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        GitStatusSnapshot status = await GetStatusSnapshotAsync(repoRoot, cancellationToken).ConfigureAwait(false);
        GitCommandResult remoteResult = await RunGitAsync(repoRoot, cancellationToken, "remote").ConfigureAwait(false);
        return new GitRepositoryMenuState(
            ResolveProjectName(workingDirectory),
            repoRoot,
            status.Branch,
            status.StatusSummary,
            !string.IsNullOrWhiteSpace(status.UpstreamName) ? ParseRemoteNameFromUpstream(status.UpstreamName) : remoteResult.ExitCode == 0 ? ParsePrimaryRemote(remoteResult.StandardOutput) : null,
            ResolveProjectName(repoRoot),
            status.IsClean,
            status.IsDetached,
            status.AheadCount,
            status.BehindCount);
    }

    public async Task<string> GetStatusAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        string repoRoot = await ResolveRepositoryRootAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        GitCommandResult result = await RunGitRequiredAsync(repoRoot, cancellationToken, "status", "--short", "--branch").ConfigureAwait(false);
        GitStatusSnapshot snapshot = await GetStatusSnapshotAsync(repoRoot, cancellationToken).ConfigureAwait(false);
        string[] lines =
        [
            $"🌿 Git status for {ResolveProjectName(repoRoot)}",
            $"🌿 Branch: {snapshot.Branch}",
            $"📄 Status: {snapshot.StatusSummary}",
            snapshot.IsDetached ? "⚠️ Detached HEAD" : FormatAheadBehind(snapshot.AheadCount, snapshot.BehindCount),
            FormatCommandBody(result.StandardOutput, "Repository is clean.")
        ];
        return string.Join(
            Environment.NewLine,
            lines.Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    public async Task<string> GetDiffSummaryAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        string repoRoot = await ResolveRepositoryRootAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        GitCommandResult unstagedNames = await RunGitRequiredAsync(repoRoot, cancellationToken, "diff", "--name-only").ConfigureAwait(false);
        GitCommandResult stagedNames = await RunGitRequiredAsync(repoRoot, cancellationToken, "diff", "--cached", "--name-only").ConfigureAwait(false);
        GitCommandResult unstaged = await RunGitRequiredAsync(repoRoot, cancellationToken, "diff", "--stat").ConfigureAwait(false);
        GitCommandResult staged = await RunGitRequiredAsync(repoRoot, cancellationToken, "diff", "--cached", "--stat").ConfigureAwait(false);
        IReadOnlyList<string> changedFiles = ParseChangedFiles(unstagedNames.StandardOutput, stagedNames.StandardOutput);
        List<string> lines =
        [
            $"📄 Git diff for {ResolveProjectName(repoRoot)}",
            changedFiles.Count == 0 ? "📝 Changed files: none" : "📝 Changed files:",
        ];

        foreach (string file in changedFiles.Take(8))
        {
            lines.Add($"• {file}");
        }

        if (changedFiles.Count > 8)
        {
            lines.Add($"• ... {changedFiles.Count - 8} more");
        }

        lines.AddRange(
        [
            string.Empty,
            "📊 Unstaged:",
            FormatCommandBody(unstaged.StandardOutput, "No unstaged changes."),
            string.Empty,
            "📦 Staged:",
            FormatCommandBody(staged.StandardOutput, "No staged changes."),
        ]);
        return string.Join(Environment.NewLine, lines).TrimEnd();
    }

    public async Task<GitActionResult> CommitAsync(string workingDirectory, string message, CancellationToken cancellationToken)
    {
        string trimmedMessage = message?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedMessage))
        {
            return new GitActionResult(false, "⚠️ Commit message cannot be empty.");
        }

        string repoRoot = await ResolveRepositoryRootAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        GitCommandResult addResult = await RunGitAsync(repoRoot, cancellationToken, "add", "-A").ConfigureAwait(false);
        if (addResult.ExitCode != 0)
        {
            return new GitActionResult(false, BuildActionFailure("git add -A", addResult));
        }

        GitCommandResult commitResult = await RunGitAsync(repoRoot, cancellationToken, "commit", "-m", trimmedMessage).ConfigureAwait(false);
        if (commitResult.ExitCode != 0)
        {
            return new GitActionResult(false, BuildActionFailure("git commit", commitResult));
        }

        return new GitActionResult(true, BuildActionSuccess("✅ Commit completed.", commitResult));
    }

    public async Task<GitActionResult> PushAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        string repoRoot = await ResolveRepositoryRootAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        GitCommandResult result = await RunGitAsync(repoRoot, cancellationToken, "push").ConfigureAwait(false);
        return result.ExitCode == 0
            ? new GitActionResult(true, BuildActionSuccess("⬆️ Push completed.", result))
            : new GitActionResult(false, BuildActionFailure("git push", result));
    }

    public async Task<GitActionResult> PullAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        string repoRoot = await ResolveRepositoryRootAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        GitCommandResult result = await RunGitAsync(repoRoot, cancellationToken, "pull").ConfigureAwait(false);
        return result.ExitCode == 0
            ? new GitActionResult(true, BuildActionSuccess("⬇️ Pull completed.", result))
            : new GitActionResult(false, BuildActionFailure("git pull", result));
    }

    public async Task<GitBranchMenuState> GetBranchMenuStateAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        string repoRoot = await ResolveRepositoryRootAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        GitStatusSnapshot status = await GetStatusSnapshotAsync(repoRoot, cancellationToken).ConfigureAwait(false);
        GitCommandResult branchResult = await RunGitRequiredAsync(repoRoot, cancellationToken, "branch", "--format=%(refname:short)").ConfigureAwait(false);
        IReadOnlyList<string> branches = ParseLocalBranches(branchResult.StandardOutput);
        return new GitBranchMenuState(ResolveProjectName(workingDirectory), repoRoot, status.Branch, status.IsDetached, branches);
    }

    public async Task<GitActionResult> CheckoutBranchAsync(string workingDirectory, string branchName, CancellationToken cancellationToken)
    {
        string trimmedBranch = branchName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedBranch))
        {
            return new GitActionResult(false, "Branch name is missing.");
        }

        string repoRoot = await ResolveRepositoryRootAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> localBranches = (await GetBranchMenuStateAsync(repoRoot, cancellationToken).ConfigureAwait(false)).LocalBranches;
        if (!localBranches.Contains(trimmedBranch, StringComparer.Ordinal))
        {
            return new GitActionResult(false, $"Branch '{trimmedBranch}' is not available in this repository.");
        }

        GitCommandResult result = await RunGitAsync(repoRoot, cancellationToken, "checkout", trimmedBranch).ConfigureAwait(false);
        return result.ExitCode == 0
            ? new GitActionResult(true, BuildActionSuccess($"🌿 Checked out {trimmedBranch}.", result))
            : new GitActionResult(false, BuildActionFailure("git checkout", result));
    }

    internal static string ResolveCurrentBranchDisplay(string branchOutput, string fallbackCommitOutput)
    {
        string branch = branchOutput?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(branch))
        {
            return branch;
        }

        string fallback = fallbackCommitOutput?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(fallback) ? "<detached>" : fallback;
    }

    internal static string SummarizePorcelainStatus(string rawStatus)
    {
        string[] lines = rawStatus
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
        {
            return "clean";
        }

        int modified = 0;
        int untracked = 0;
        foreach (string line in lines)
        {
            if (line.StartsWith("??", StringComparison.Ordinal))
            {
                untracked++;
            }
            else
            {
                modified++;
            }
        }

        List<string> parts = [];
        if (modified > 0)
        {
            parts.Add($"{modified.ToString(CultureInfo.InvariantCulture)} modified");
        }

        if (untracked > 0)
        {
            parts.Add($"{untracked.ToString(CultureInfo.InvariantCulture)} untracked");
        }

        return parts.Count == 0 ? "clean" : string.Join(", ", parts);
    }

    internal static GitStatusSnapshot ParseStatusSnapshot(string rawStatusShortBranch, string branchOutput, string fallbackCommitOutput)
    {
        string[] lines = rawStatusShortBranch
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? header = lines.FirstOrDefault(line => line.StartsWith("##", StringComparison.Ordinal));
        IReadOnlyList<string> changeLines = header is null ? lines : lines.Skip(1).ToArray();
        string branch = ResolveCurrentBranchDisplay(branchOutput, fallbackCommitOutput);
        bool isDetached = string.IsNullOrWhiteSpace(branchOutput?.Trim());
        (int aheadCount, int behindCount) = ParseAheadBehind(header);
        string? upstreamName = ParseUpstreamName(header);
        return new GitStatusSnapshot(
            branch,
            SummarizePorcelainStatus(string.Join(Environment.NewLine, changeLines)),
            changeLines.Count == 0,
            isDetached,
            aheadCount,
            behindCount,
            upstreamName);
    }

    internal static string? ParsePrimaryRemote(string rawRemotes)
        => rawRemotes
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

    internal static IReadOnlyList<string> ParseLocalBranches(string rawBranches)
        => rawBranches
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(branch => !string.IsNullOrWhiteSpace(branch))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    internal static IReadOnlyList<string> ParseChangedFiles(params string[] rawLists)
        => rawLists
            .SelectMany(rawList => rawList.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(file => !string.IsNullOrWhiteSpace(file))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private async Task<string> ResolveRepositoryRootAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        CodexWorkspaceValidationVm validation = _workspaceBrowser.ValidateWorkingDirectory(workingDirectory);
        if (!validation.IsValid || string.IsNullOrWhiteSpace(validation.NormalizedPath))
        {
            throw new InvalidOperationException($"Project path rejected: {validation.Message}");
        }

        GitCommandResult repoResult = await RunGitAsync(validation.NormalizedPath, cancellationToken, "rev-parse", "--show-toplevel").ConfigureAwait(false);
        if (repoResult.ExitCode != 0)
        {
            throw new InvalidOperationException($"The selected project is not a git repo.{Environment.NewLine}{BuildActionFailure("git rev-parse --show-toplevel", repoResult)}");
        }

        string repoRoot = repoResult.StandardOutput.Trim();
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            throw new InvalidOperationException("Git did not return a repository root.");
        }

        CodexWorkspaceValidationVm repoValidation = _workspaceBrowser.ValidateWorkingDirectory(repoRoot);
        if (!repoValidation.IsValid || string.IsNullOrWhiteSpace(repoValidation.NormalizedPath))
        {
            throw new InvalidOperationException($"Repository path rejected: {repoValidation.Message}");
        }

        if (!PathComparer.Equals(repoValidation.NormalizedPath, validation.NormalizedPath)
            && !repoValidation.NormalizedPath.StartsWith(validation.NormalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            && !validation.NormalizedPath.StartsWith(repoValidation.NormalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            _logger.LogDebug("Git repository root {RepoRoot} differs from selected path {SelectedPath}.", repoValidation.NormalizedPath, validation.NormalizedPath);
        }

        return repoValidation.NormalizedPath;
    }

    private async Task<string> GetCurrentBranchAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        GitCommandResult branchResult = await RunGitRequiredAsync(workingDirectory, cancellationToken, "branch", "--show-current").ConfigureAwait(false);
        GitCommandResult fallbackResult = await RunGitRequiredAsync(workingDirectory, cancellationToken, "rev-parse", "--short", "HEAD").ConfigureAwait(false);
        return ResolveCurrentBranchDisplay(branchResult.StandardOutput, fallbackResult.StandardOutput);
    }

    private async Task<GitStatusSnapshot> GetStatusSnapshotAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        GitCommandResult statusResult = await RunGitRequiredAsync(workingDirectory, cancellationToken, "status", "--short", "--branch").ConfigureAwait(false);
        GitCommandResult branchResult = await RunGitRequiredAsync(workingDirectory, cancellationToken, "branch", "--show-current").ConfigureAwait(false);
        GitCommandResult fallbackResult = await RunGitRequiredAsync(workingDirectory, cancellationToken, "rev-parse", "--short", "HEAD").ConfigureAwait(false);
        return ParseStatusSnapshot(statusResult.StandardOutput, branchResult.StandardOutput, fallbackResult.StandardOutput);
    }

    private async Task<GitCommandResult> RunGitRequiredAsync(string workingDirectory, CancellationToken cancellationToken, params string[] arguments)
    {
        GitCommandResult result = await RunGitAsync(workingDirectory, cancellationToken, arguments).ConfigureAwait(false);
        if (result.ExitCode == 0)
        {
            return result;
        }

        throw new InvalidOperationException(BuildActionFailure("git " + string.Join(' ', arguments), result));
    }

    private async Task<GitCommandResult> RunGitAsync(string workingDirectory, CancellationToken cancellationToken, params string[] arguments)
    {
        ProcessStartInfo startInfo = BuildStartInfo(workingDirectory, arguments);
        GitCommandResult result = await _executor.ExecuteAsync(startInfo, GitTimeout, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Git command {Command} in {WorkingDirectory} exited with {ExitCode}.", string.Join(' ', arguments), workingDirectory, result.ExitCode);
        return result;
    }

    internal static ProcessStartInfo BuildStartInfo(string workingDirectory, params string[] arguments)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static string BuildActionSuccess(string heading, GitCommandResult result)
    {
        string body = FormatCommandBody(result.CombinedText, "No additional output.");
        return string.Join(Environment.NewLine, [heading, body]);
    }

    private static string BuildActionFailure(string command, GitCommandResult result)
    {
        StringBuilder builder = new();
        builder.AppendLine($"{command} failed.");
        if (result.TimedOut)
        {
            builder.AppendLine("The command timed out.");
        }

        if (result.ExitCode != 0)
        {
            builder.AppendLine($"Exit code: {result.ExitCode.ToString(CultureInfo.InvariantCulture)}");
        }

        builder.Append(FormatCommandBody(result.CombinedText, "No additional output."));
        return builder.ToString().TrimEnd();
    }

    private static string FormatCommandBody(string text, string fallback)
    {
        string[] lines = text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        if (lines.Length == 0)
        {
            return fallback;
        }

        int take = Math.Min(lines.Length, 20);
        List<string> selected = lines.Take(take).ToList();
        if (lines.Length > take)
        {
            selected.Add($"... {lines.Length - take} more line(s)");
        }

        return string.Join(Environment.NewLine, selected);
    }

    private static string ResolveProjectName(string workingDirectory)
    {
        string name = Path.GetFileName(workingDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? workingDirectory : name;
    }

    private static string? ParseRemoteNameFromUpstream(string upstreamName)
    {
        string[] parts = upstreamName.Split('/', 2, StringSplitOptions.TrimEntries);
        return parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]) ? parts[0] : upstreamName;
    }

    private static string? ParseUpstreamName(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        int ellipsisIndex = header.IndexOf("...", StringComparison.Ordinal);
        if (ellipsisIndex < 0)
        {
            return null;
        }

        int start = ellipsisIndex + 3;
        int end = header.IndexOf(' ', start);
        return end < 0 ? header[start..].Trim() : header[start..end].Trim();
    }

    private static (int AheadCount, int BehindCount) ParseAheadBehind(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return (0, 0);
        }

        Match wrapperMatch = AheadBehindRegex.Match(header);
        if (!wrapperMatch.Success)
        {
            return (0, 0);
        }

        string details = wrapperMatch.Groups["parts"].Value;
        int aheadCount = AheadRegex.Match(details) is Match aheadMatch && aheadMatch.Success
            ? int.Parse(aheadMatch.Groups["count"].Value, CultureInfo.InvariantCulture)
            : 0;
        int behindCount = BehindRegex.Match(details) is Match behindMatch && behindMatch.Success
            ? int.Parse(behindMatch.Groups["count"].Value, CultureInfo.InvariantCulture)
            : 0;
        return (aheadCount, behindCount);
    }

    private static string FormatAheadBehind(int aheadCount, int behindCount)
    {
        if (aheadCount <= 0 && behindCount <= 0)
        {
            return string.Empty;
        }

        List<string> parts = [];
        if (aheadCount > 0)
        {
            parts.Add($"⬆️ ahead {aheadCount.ToString(CultureInfo.InvariantCulture)}");
        }

        if (behindCount > 0)
        {
            parts.Add($"⬇️ behind {behindCount.ToString(CultureInfo.InvariantCulture)}");
        }

        return string.Join(", ", parts);
    }
}
