using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Incursa.Codex.Telegram.Models;
using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Services;

internal interface IDevUtilityService
{
    Task<IReadOnlyList<string>> ListRunningProjectDirectoriesAsync(CancellationToken cancellationToken);

    Task<DevTargetDescriptor> DescribeTargetAsync(string workingDirectory, CancellationToken cancellationToken);

    Task<string> StartAsync(string workingDirectory, CancellationToken cancellationToken);

    Task<string> StopAsync(string workingDirectory, CancellationToken cancellationToken);

    Task<string> RestartAsync(string workingDirectory, CancellationToken cancellationToken);

    Task<string> GetStatusAsync(string workingDirectory, CancellationToken cancellationToken);

    Task<string> GetLogsAsync(string workingDirectory, CancellationToken cancellationToken);

    Task<string> GetPreviewAsync(string workingDirectory, CancellationToken cancellationToken);
}

internal sealed record DevTargetDescriptor(
    string WorkingDirectory,
    bool IsRunnable,
    bool HasPackageJson,
    bool HasDevScript,
    bool HasExplicitConfig);

internal sealed class DevUtilityService : IDevUtilityService
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly IOptions<CodexTelegramOptions> _options;
    private readonly CodexWorkspaceBrowser _workspaceBrowser;
    private readonly DevUtilityStateStore _stateStore;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DevUtilityService> _logger;

    public DevUtilityService(
        IOptions<CodexTelegramOptions> options,
        CodexWorkspaceBrowser workspaceBrowser,
        DevUtilityStateStore stateStore,
        TimeProvider timeProvider,
        ILogger<DevUtilityService> logger)
    {
        _options = options;
        _workspaceBrowser = workspaceBrowser;
        _stateStore = stateStore;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> ListRunningProjectDirectoriesAsync(CancellationToken cancellationToken)
    {
        List<DevProcessRecord> records = (await _stateStore.ListAsync(cancellationToken).ConfigureAwait(false)).ToList();
        List<string> running = [];
        foreach (DevProcessRecord record in records)
        {
            if (IsProcessRunning(record.ProcessId))
            {
                running.Add(record.WorkingDirectory);
            }
            else
            {
                await _stateStore.RemoveAsync(record.WorkingDirectory, cancellationToken).ConfigureAwait(false);
            }
        }

        return running.Distinct(PathComparer).ToArray();
    }

    public Task<DevTargetDescriptor> DescribeTargetAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(DescribeTarget(workingDirectory));
    }

    public async Task<string> StartAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        ResolvedDevProject project = ResolveProject(workingDirectory);
        DevProcessRecord? existing = await _stateStore.GetAsync(project.WorkingDirectory, cancellationToken).ConfigureAwait(false);
        if (existing is not null && IsProcessRunning(existing.ProcessId))
        {
            return BuildStatusText(project, existing, includePreview: false, heading: "Dev server is already running.");
        }

        if (existing is not null)
        {
            await _stateStore.RemoveAsync(project.WorkingDirectory, cancellationToken).ConfigureAwait(false);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(project.LogPath) ?? project.WorkingDirectory);
        await AppendLogHeaderAsync(project.LogPath, $"[dev] starting {project.Command}", cancellationToken).ConfigureAwait(false);

        DevProcessRecord? record = await TryStartProcessAsync(project, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            string failureLogs = await ReadTailAsync(project.LogPath, 20, cancellationToken).ConfigureAwait(false);
            return string.Join(Environment.NewLine, [
                "Dev server exited immediately.",
                $"Project: {ResolveProjectName(project.WorkingDirectory)}",
                $"Cwd: {project.WorkingDirectory}",
                string.IsNullOrWhiteSpace(failureLogs) ? "No log output was captured." : "Recent log output:",
                string.IsNullOrWhiteSpace(failureLogs) ? string.Empty : failureLogs
            ]).TrimEnd();
        }

        string logs = await ReadTailAsync(project.LogPath, 20, cancellationToken).ConfigureAwait(false);
        return string.Join(Environment.NewLine, [
            "Started dev server.",
            $"Project: {ResolveProjectName(project.WorkingDirectory)}",
            $"PID: {record.ProcessId}",
            $"Cwd: {project.WorkingDirectory}",
            $"Command: {record.Command}",
            $"Log: {record.LogPath}",
            project.Port.HasValue ? $"Local URL: http://localhost:{project.Port.Value}" : "Local URL: not configured",
            string.IsNullOrWhiteSpace(logs) ? "No initial log output yet." : "Recent log output:",
            string.IsNullOrWhiteSpace(logs) ? string.Empty : logs
        ]).TrimEnd();
    }

    public async Task<string> StopAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        ResolvedDevProject project = ResolveProject(workingDirectory);
        DevProcessRecord? record = await _stateStore.GetAsync(project.WorkingDirectory, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return BuildStatusText(project, null, includePreview: false, heading: "No tracked dev server is running for this project.");
        }

        bool stopped = await TryStopProcessAsync(record.ProcessId, cancellationToken).ConfigureAwait(false);
        await _stateStore.RemoveAsync(project.WorkingDirectory, cancellationToken).ConfigureAwait(false);

        return string.Join(Environment.NewLine, [
            stopped ? "Stopped dev server." : "Removed stale dev server record.",
            $"Project: {ResolveProjectName(project.WorkingDirectory)}",
            $"Cwd: {project.WorkingDirectory}",
            $"Log: {record.LogPath}"
        ]);
    }

    public async Task<string> RestartAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        string stop = await StopAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        string start = await StartAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        return stop + Environment.NewLine + Environment.NewLine + start;
    }

    public async Task<string> GetStatusAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        ResolvedDevProject project = ResolveProject(workingDirectory);
        DevProcessRecord? record = await _stateStore.GetAsync(project.WorkingDirectory, cancellationToken).ConfigureAwait(false);
        if (record is not null && !IsProcessRunning(record.ProcessId))
        {
            await _stateStore.RemoveAsync(project.WorkingDirectory, cancellationToken).ConfigureAwait(false);
            record = null;
        }

        string preview = await BuildPreviewTextAsync(project, cancellationToken).ConfigureAwait(false);
        return BuildStatusText(project, record, includePreview: true, heading: "Dev server status.")
            + Environment.NewLine
            + Environment.NewLine
            + preview;
    }

    public async Task<string> GetLogsAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        ResolvedDevProject project = ResolveProject(workingDirectory);
        string logs = await ReadTailAsync(project.LogPath, Math.Clamp(_options.Value.DevUtilities.LogTailLineCount, 10, 200), cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(logs))
        {
            return string.Join(Environment.NewLine, [
                "No log output is available yet.",
                $"Project: {ResolveProjectName(project.WorkingDirectory)}",
                $"Log: {project.LogPath}"
            ]);
        }

        return string.Join(Environment.NewLine, [
            $"Recent logs for {ResolveProjectName(project.WorkingDirectory)}:",
            $"Log: {project.LogPath}",
            logs
        ]);
    }

    public async Task<string> GetPreviewAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        ResolvedDevProject project = ResolveProject(workingDirectory);
        return await BuildPreviewTextAsync(project, cancellationToken).ConfigureAwait(false);
    }

    private ResolvedDevProject ResolveProject(string workingDirectory)
    {
        CodexWorkspaceValidationVm validation = _workspaceBrowser.ValidateWorkingDirectory(workingDirectory);
        if (!validation.IsValid || string.IsNullOrWhiteSpace(validation.NormalizedPath))
        {
            throw new InvalidOperationException($"Project path rejected: {validation.Message}");
        }

        string normalized = validation.NormalizedPath;
        DevTargetDescriptor descriptor = DescribeTarget(normalized);
        DevUtilityOptions options = _options.Value.DevUtilities;
        KeyValuePair<string, DevUtilityProjectOptions>? matched = options.Projects.FirstOrDefault(pair =>
        {
            string? configuredCwd = string.IsNullOrWhiteSpace(pair.Value.Cwd) ? null : Path.GetFullPath(pair.Value.Cwd);
            return (!string.IsNullOrWhiteSpace(configuredCwd) && PathComparer.Equals(configuredCwd, normalized))
                || string.Equals(pair.Key, ResolveProjectName(normalized), StringComparison.OrdinalIgnoreCase);
        });

        DevUtilityProjectOptions projectOptions = matched?.Value ?? new DevUtilityProjectOptions();
        if (!descriptor.IsRunnable)
        {
            throw new InvalidOperationException(
                $"The directory '{normalized}' does not look like a runnable JS app. Expected package.json with a dev script, or an explicit project dev config.");
        }

        string command = string.IsNullOrWhiteSpace(projectOptions.DevCommand) ? options.DefaultCommand : projectOptions.DevCommand.Trim();
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new InvalidOperationException("No dev command is configured for this project.");
        }

        string logSetting = string.IsNullOrWhiteSpace(projectOptions.LogFile) ? options.DefaultLogFile : projectOptions.LogFile.Trim();
        string logPath = ResolveLogPath(normalized, logSetting);
        return new ResolvedDevProject(
            normalized,
            command,
            logPath,
            projectOptions.Port,
            string.IsNullOrWhiteSpace(projectOptions.PreviewCommand) ? null : projectOptions.PreviewCommand.Trim());
    }

    private DevTargetDescriptor DescribeTarget(string workingDirectory)
    {
        CodexWorkspaceValidationVm validation = _workspaceBrowser.ValidateWorkingDirectory(workingDirectory);
        if (!validation.IsValid || string.IsNullOrWhiteSpace(validation.NormalizedPath))
        {
            throw new InvalidOperationException($"Project path rejected: {validation.Message}");
        }

        string normalized = validation.NormalizedPath;
        bool hasExplicitConfig = TryGetProjectOptions(normalized) is not null;
        string packageJsonPath = Path.Combine(normalized, "package.json");
        if (!File.Exists(packageJsonPath))
        {
            return new DevTargetDescriptor(normalized, hasExplicitConfig, false, false, hasExplicitConfig);
        }

        bool hasDevScript = false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            if (document.RootElement.TryGetProperty("scripts", out JsonElement scripts)
                && scripts.ValueKind == JsonValueKind.Object
                && scripts.TryGetProperty("dev", out JsonElement devScript)
                && devScript.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(devScript.GetString()))
            {
                hasDevScript = true;
            }
        }
        catch
        {
        }

        return new DevTargetDescriptor(normalized, hasExplicitConfig || hasDevScript, true, hasDevScript, hasExplicitConfig);
    }

    private Process StartBackgroundProcess(ResolvedDevProject project)
    {
        ProcessStartInfo startInfo = BuildBackgroundProcessStartInfo(project.WorkingDirectory, project.Command, project.LogPath);

        Process? process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException("Failed to start the dev process.");
        }

        return process;
    }

    internal static ProcessStartInfo BuildBackgroundProcessStartInfo(string workingDirectory, string command, string logPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/d /s /c \"{command} >> \\\"{logPath}\\\" 2>&1\"",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
        }

        string shellCommand = BuildUnixShellCommand(command, logPath);
        ProcessStartInfo startInfo = new()
        {
            FileName = "/bin/bash",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-lc");
        startInfo.ArgumentList.Add(shellCommand);
        return startInfo;
    }

    internal static string BuildUnixShellCommand(string command, string logPath)
        => $"exec {command} >> {EscapeBashSingleQuoted(logPath)} 2>&1";

    private async Task<bool> TryStopProcessAsync(int processId, CancellationToken cancellationToken)
    {
        Process? process = TryGetProcess(processId);
        if (process is null)
        {
            return false;
        }

        try
        {
            if (!process.HasExited)
            {
                if (OperatingSystem.IsWindows())
                {
                    process.Kill(entireProcessTree: true);
                }
                else
                {
                    using Process term = Process.Start(new ProcessStartInfo
                    {
                        FileName = "/bin/kill",
                        ArgumentList = { "-TERM", processId.ToString() },
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    }) ?? throw new InvalidOperationException("Failed to signal the dev process.");
                    await term.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                    if (!process.WaitForExit(5000))
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private async Task<string> BuildPreviewTextAsync(ResolvedDevProject project, CancellationToken cancellationToken)
    {
        string? tailscaleDnsName = await TryGetTailscaleDnsNameAsync(cancellationToken).ConfigureAwait(false);
        string? previewStatus = string.IsNullOrWhiteSpace(project.PreviewCommand)
            ? null
            : await TryRunShellCommandAsync(project.WorkingDirectory, project.PreviewCommand, cancellationToken).ConfigureAwait(false);

        List<string> lines = [
            $"Preview for {ResolveProjectName(project.WorkingDirectory)}:",
            $"Cwd: {project.WorkingDirectory}",
            project.Port.HasValue ? $"Local URL: http://localhost:{project.Port.Value}" : "Local URL: not configured",
        ];

        if (!string.IsNullOrWhiteSpace(tailscaleDnsName))
        {
            lines.Add($"Tailscale hostname: {tailscaleDnsName}");
        }

        if (!string.IsNullOrWhiteSpace(previewStatus))
        {
            lines.Add("Tailscale/preview status:");
            lines.Add(previewStatus);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private string BuildStatusText(ResolvedDevProject project, DevProcessRecord? record, bool includePreview, string heading)
    {
        List<string> lines = [
            heading,
            $"Project: {ResolveProjectName(project.WorkingDirectory)}",
            $"Running: {(record is not null && IsProcessRunning(record.ProcessId) ? "yes" : "no")}",
            $"Cwd: {project.WorkingDirectory}",
            $"Command: {project.Command}",
            project.Port.HasValue ? $"Port: {project.Port.Value}" : "Port: not configured",
            $"Log: {project.LogPath}",
        ];

        if (record is not null && IsProcessRunning(record.ProcessId))
        {
            lines.Add($"PID: {record.ProcessId}");
            lines.Add($"Started: {record.StartedAtUtc:O}");
        }

        if (includePreview)
        {
            lines.Add(project.Port.HasValue ? $"Local URL: http://localhost:{project.Port.Value}" : "Local URL: not configured");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string ResolveProjectName(string workingDirectory)
    {
        string name = Path.GetFileName(workingDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? workingDirectory : name;
    }

    internal static string EscapeBashSingleQuoted(string value)
        => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private string ResolveLogPath(string workingDirectory, string logSetting)
    {
        string path = Path.IsPathRooted(logSetting)
            ? Path.GetFullPath(logSetting)
            : Path.GetFullPath(Path.Combine(workingDirectory, logSetting));

        string? parent = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(parent))
        {
            return path;
        }

        IReadOnlyList<string> roots = _workspaceBrowser.GetWorkspaceRoots();
        bool allowed = roots.Any(root => IsPathUnderRoot(path, root));
        if (!allowed)
        {
            throw new InvalidOperationException($"The log path '{path}' is outside the allowlisted workspace roots.");
        }

        return path;
    }

    private static bool IsPathUnderRoot(string candidate, string root)
    {
        string normalizedCandidate = Path.GetFullPath(candidate)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(
            normalizedRoot,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private async Task<string> ReadTailAsync(string logPath, int lineCount, CancellationToken cancellationToken)
    {
        if (!File.Exists(logPath))
        {
            return string.Empty;
        }

        string[] lines = await File.ReadAllLinesAsync(logPath, cancellationToken).ConfigureAwait(false);
        IEnumerable<string> safeLines = lines
            .TakeLast(Math.Clamp(lineCount, 1, 200))
            .Select(SanitizeLogLine)
            .Where(line => !string.IsNullOrWhiteSpace(line));
        return string.Join(Environment.NewLine, safeLines);
    }

    private static string SanitizeLogLine(string line)
    {
        string trimmed = line.TrimEnd();
        string lower = trimmed.ToLowerInvariant();
        string[] sensitiveTokens = ["token", "secret", "password", "api_key", "apikey", "authorization", "cookie", ".env"];
        return sensitiveTokens.Any(token => lower.Contains(token, StringComparison.Ordinal))
            ? "[redacted potentially sensitive log line]"
            : trimmed;
    }

    private static bool IsProcessRunning(int processId)
    {
        Process? process = TryGetProcess(processId);
        return process is not null && !process.HasExited;
    }

    private static Process? TryGetProcess(int processId)
    {
        try
        {
            return Process.GetProcessById(processId);
        }
        catch
        {
            return null;
        }
    }

    private static async Task AppendLogHeaderAsync(string logPath, string header, CancellationToken cancellationToken)
    {
        string prefix = $"{DateTimeOffset.UtcNow:O} {header}{Environment.NewLine}";
        await File.AppendAllTextAsync(logPath, prefix, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> TryRunShellCommandAsync(string workingDirectory, string command, CancellationToken cancellationToken)
    {
        try
        {
            ProcessStartInfo startInfo = OperatingSystem.IsWindows()
                ? new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/d /s /c \"{command}\"",
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
                : new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments = $"-lc {EscapeBashSingleQuoted(command)}",
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start preview command.");
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            string text = ((await stdout.ConfigureAwait(false)) + Environment.NewLine + (await stderr.ConfigureAwait(false))).Trim();
            return string.IsNullOrWhiteSpace(text) ? null : SanitizeMultiline(text);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Preview command failed in {WorkingDirectory}.", workingDirectory);
            return null;
        }
    }

    private async Task<string?> TryGetTailscaleDnsNameAsync(CancellationToken cancellationToken)
    {
        try
        {
            string? json = await TryRunShellCommandAsync(Environment.CurrentDirectory, "tailscale status --json", cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("Self", out JsonElement self)
                && self.TryGetProperty("DNSName", out JsonElement dnsName))
            {
                string? value = dnsName.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value.TrimEnd('.');
            }
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Failed to read Tailscale status.");
        }

        return null;
    }

    private static string SanitizeMultiline(string text)
        => string.Join(Environment.NewLine, text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(SanitizeLogLine));

    private DevUtilityProjectOptions? TryGetProjectOptions(string normalizedWorkingDirectory)
        => _options.Value.DevUtilities.Projects
            .Select(pair => pair.Value)
            .FirstOrDefault(project =>
            {
                string? configuredCwd = string.IsNullOrWhiteSpace(project.Cwd) ? null : Path.GetFullPath(project.Cwd);
                return !string.IsNullOrWhiteSpace(configuredCwd) && PathComparer.Equals(configuredCwd, normalizedWorkingDirectory);
            });

    private async Task<DevProcessRecord?> TryStartProcessAsync(ResolvedDevProject project, CancellationToken cancellationToken)
    {
        using Process process = StartBackgroundProcess(project);
        DevProcessRecord record = new(
            project.WorkingDirectory,
            process.Id,
            project.Command,
            project.LogPath,
            project.Port,
            project.PreviewCommand,
            _timeProvider.GetUtcNow());

        await _stateStore.UpsertAsync(record, cancellationToken).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken).ConfigureAwait(false);
        if (IsProcessRunning(record.ProcessId))
        {
            return record;
        }

        await _stateStore.RemoveAsync(project.WorkingDirectory, cancellationToken).ConfigureAwait(false);
        if (!CanRetryWithoutHostname(project.Command))
        {
            return null;
        }

        string fallbackCommand = project.Command.Replace(" -- --hostname 0.0.0.0", string.Empty, StringComparison.Ordinal);
        await AppendLogHeaderAsync(project.LogPath, $"[dev] retrying without hostname flag: {fallbackCommand}", cancellationToken).ConfigureAwait(false);
        using Process fallback = StartBackgroundProcess(project with { Command = fallbackCommand });
        DevProcessRecord fallbackRecord = record with
        {
            ProcessId = fallback.Id,
            Command = fallbackCommand,
            StartedAtUtc = _timeProvider.GetUtcNow(),
        };

        await _stateStore.UpsertAsync(fallbackRecord, cancellationToken).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken).ConfigureAwait(false);
        if (IsProcessRunning(fallbackRecord.ProcessId))
        {
            return fallbackRecord;
        }

        await _stateStore.RemoveAsync(project.WorkingDirectory, cancellationToken).ConfigureAwait(false);
        return null;
    }

    private static bool CanRetryWithoutHostname(string command)
        => command.Contains(" -- --hostname 0.0.0.0", StringComparison.Ordinal);

    private sealed record ResolvedDevProject(
        string WorkingDirectory,
        string Command,
        string LogPath,
        int? Port,
        string? PreviewCommand);
}
