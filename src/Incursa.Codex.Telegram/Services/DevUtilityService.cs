using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    Task<string> KillDevPortsAsync(CancellationToken cancellationToken);
}

internal sealed record DevTargetDescriptor(
    string WorkingDirectory,
    bool IsRunnable,
    bool HasPackageJson,
    bool HasDevScript,
    bool HasExplicitConfig);

internal sealed class DevUtilityService : IDevUtilityService
{
    private static readonly int[] KillDevPorts = [3000, 3001, 3002, 3003, 3004, 3005, 3100, 3500, 4000];
    private static readonly HashSet<string> SafeDevProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node",
        "node.exe",
        "next",
        "next-server",
        "next.exe",
        "vite",
        "vite.exe",
        "npm",
        "npm.exe",
        "npm.cmd",
        "pnpm",
        "pnpm.exe",
        "bun",
        "bun.exe",
        "yarn",
        "yarn.exe",
        "bunx.exe",
    };
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static readonly Regex DetectedPortRegex = new(
        @"(?:(?:localhost|127\.0\.0\.1|0\.0\.0\.0|\[::1\])|Local:\s*https?://localhost|Local:\s*https?://127\.0\.0\.1|Local:\s*https?://0\.0\.0\.0).*?:(?<port>\d{1,5})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex TailscaleUrlRegex = new(
        @"https://[^\s]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

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
            int? currentPort = await GetEffectivePortAsync(project, existing, cancellationToken).ConfigureAwait(false);
            string? currentTailscaleUrl = await TryGetActiveTailscaleUrlAsync(currentPort, cancellationToken).ConfigureAwait(false);
            return BuildStatusText(project, existing, currentPort, currentTailscaleUrl, includePreview: false, heading: "Dev server is already running.");
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
        int? effectivePort = await GetEffectivePortAsync(project, record, cancellationToken).ConfigureAwait(false);
        string? tailscaleUrl = await TryGetActiveTailscaleUrlAsync(effectivePort, cancellationToken).ConfigureAwait(false);
        return string.Join(Environment.NewLine, [
            "Started dev server.",
            $"Project: {ResolveProjectName(project.WorkingDirectory)}",
            $"PID: {record.ProcessId}",
            $"Cwd: {project.WorkingDirectory}",
            $"Command: {record.Command}",
            $"Log: {record.LogPath}",
            effectivePort.HasValue ? $"Port: {effectivePort.Value}" : "Port: unknown",
            effectivePort.HasValue ? $"Local URL: http://localhost:{effectivePort.Value}" : "Local URL: unavailable",
            string.IsNullOrWhiteSpace(tailscaleUrl) ? "Tailscale URL: inactive" : $"Tailscale URL: {tailscaleUrl}",
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
            return BuildStatusText(project, null, project.Port, null, includePreview: false, heading: "No tracked dev server is running for this project.");
        }

        StopProcessResult stopResult = await TryStopProcessAsync(record.ProcessId, cancellationToken).ConfigureAwait(false);
        await _stateStore.RemoveAsync(project.WorkingDirectory, cancellationToken).ConfigureAwait(false);

        return string.Join(Environment.NewLine, [
            stopResult.Stopped ? "Stopped dev server." : "Removed stale dev server record.",
            $"Project: {ResolveProjectName(project.WorkingDirectory)}",
            $"Cwd: {project.WorkingDirectory}",
            $"Log: {record.LogPath}",
            string.IsNullOrWhiteSpace(stopResult.Diagnostic) ? string.Empty : $"Stop details: {stopResult.Diagnostic}"
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

        int? effectivePort = await GetEffectivePortAsync(project, record, cancellationToken).ConfigureAwait(false);
        string? tailscaleUrl = await TryGetActiveTailscaleUrlAsync(effectivePort, cancellationToken).ConfigureAwait(false);
        string preview = await BuildPreviewTextAsync(project, effectivePort, tailscaleUrl, cancellationToken).ConfigureAwait(false);
        return BuildStatusText(project, record, effectivePort, tailscaleUrl, includePreview: true, heading: "Dev server status.")
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
        DevProcessRecord? record = await _stateStore.GetAsync(project.WorkingDirectory, cancellationToken).ConfigureAwait(false);
        int? effectivePort = await GetEffectivePortAsync(project, record, cancellationToken).ConfigureAwait(false);
        string? tailscaleUrl = await TryGetActiveTailscaleUrlAsync(effectivePort, cancellationToken).ConfigureAwait(false);
        return await BuildPreviewTextAsync(project, effectivePort, tailscaleUrl, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> KillDevPortsAsync(CancellationToken cancellationToken)
    {
        string scriptPath = ResolveKillDevPortsScriptPath();
        if (!File.Exists(scriptPath))
        {
            throw new InvalidOperationException($"Kill Dev Ports script not found: {scriptPath}");
        }

        CommandExecutionResult result = await RunProcessCaptureAsync("/bin/bash", [scriptPath], cancellationToken).ConfigureAwait(false);
        string output = FirstNonEmpty(result.StandardOutput, result.StandardError);
        if (result.ExitCode == 0)
        {
            return string.IsNullOrWhiteSpace(output) ? "Kill Dev Ports finished with no output." : output;
        }

        return string.Join(Environment.NewLine, [
            "Kill Dev Ports failed.",
            $"Script: {scriptPath}",
            $"Exit code: {result.ExitCode}",
            string.IsNullOrWhiteSpace(result.StandardOutput) ? "Stdout: <empty>" : $"Stdout:{Environment.NewLine}{result.StandardOutput}",
            string.IsNullOrWhiteSpace(result.StandardError) ? "Stderr: <empty>" : $"Stderr:{Environment.NewLine}{result.StandardError}",
        ]);
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

    private async Task<StopProcessResult> TryStopProcessAsync(int processId, CancellationToken cancellationToken)
    {
        if (IsProtectedSystemProcessId(processId))
        {
            return new StopProcessResult(false, false, false, "Refused to signal PID 1.");
        }

        Process? process = TryGetProcess(processId);
        if (process is null)
        {
            return new StopProcessResult(false, false, false, null);
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
                    CommandExecutionResult termResult = await RunProcessCaptureAsync("/bin/kill", ["-TERM", processId.ToString()], cancellationToken).ConfigureAwait(false);
                    bool permissionDenied = IsPermissionDeniedError(termResult.StandardError) || IsPermissionDeniedError(termResult.StandardOutput);
                    bool usedSudo = false;
                    if (permissionDenied)
                    {
                        CommandExecutionResult sudoTermResult = await RunProcessCaptureAsync("sudo", ["-n", "/bin/kill", "-TERM", processId.ToString()], cancellationToken).ConfigureAwait(false);
                        termResult = sudoTermResult;
                        permissionDenied = IsPermissionDeniedError(termResult.StandardError) || IsPermissionDeniedError(termResult.StandardOutput);
                        usedSudo = true;
                    }

                    if (termResult.ExitCode != 0 && !process.HasExited)
                    {
                        return new StopProcessResult(false, usedSudo, permissionDenied, BuildSignalDiagnostic("TERM", termResult, usedSudo));
                    }

                    if (!process.WaitForExit(5000))
                    {
                        CommandExecutionResult killResult = await RunProcessCaptureAsync("/bin/kill", ["-KILL", processId.ToString()], cancellationToken).ConfigureAwait(false);
                        if (killResult.ExitCode != 0)
                        {
                            bool killPermissionDenied = IsPermissionDeniedError(killResult.StandardError) || IsPermissionDeniedError(killResult.StandardOutput);
                            if (killPermissionDenied)
                            {
                                CommandExecutionResult sudoKillResult = await RunProcessCaptureAsync("sudo", ["-n", "/bin/kill", "-KILL", processId.ToString()], cancellationToken).ConfigureAwait(false);
                                killResult = sudoKillResult;
                                usedSudo = true;
                                killPermissionDenied = IsPermissionDeniedError(killResult.StandardError) || IsPermissionDeniedError(killResult.StandardOutput);
                            }

                            if (killResult.ExitCode != 0 && !process.HasExited)
                            {
                                return new StopProcessResult(false, usedSudo, killPermissionDenied, BuildSignalDiagnostic("KILL", killResult, usedSudo));
                            }
                        }
                    }

                    await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                    return new StopProcessResult(true, usedSudo, false, usedSudo ? "Stopped after sudo retry." : null);
                }
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new StopProcessResult(true, false, false, null);
        }
        catch (InvalidOperationException)
        {
            return new StopProcessResult(false, false, false, null);
        }
        catch (System.ComponentModel.Win32Exception exception) when (!OperatingSystem.IsWindows())
        {
            return new StopProcessResult(false, false, IsPermissionDeniedError(exception.Message), exception.Message);
        }
    }

    private async Task<bool> IsPortOccupiedViaSudoSsAsync(int port, CancellationToken cancellationToken)
    {
        CommandExecutionResult result = await RunShellCommandCaptureAsync("sudo -n ss -lptn", cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return false;
        }

        return FilterProtectedListeners(ParseSsListeners(result.StandardOutput, port)).Count > 0;
    }

    private async Task<StopProcessResult> TryStopListenerAsync(ListeningProcessInfo listener, CancellationToken cancellationToken)
    {
        if (listener.Source == ListenerSource.Windows)
        {
            return await TryStopWindowsListenerAsync(listener, cancellationToken).ConfigureAwait(false);
        }

        return await TryStopProcessAsync(listener.ProcessId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<StopProcessResult> TryStopWindowsListenerAsync(ListeningProcessInfo listener, CancellationToken cancellationToken)
    {
        if (IsProtectedListener(listener))
        {
            return new StopProcessResult(false, false, false, "Refused to signal a protected Windows listener.");
        }

        if (!IsSafeWindowsListener(listener))
        {
            string reason = string.IsNullOrWhiteSpace(listener.SafetyReason)
                ? "unsafe to kill automatically"
                : listener.SafetyReason;
            return new StopProcessResult(false, false, false, $"{reason}. Manual: taskkill /PID {listener.ProcessId} /F");
        }

        CommandExecutionResult result = await RunProcessCaptureAsync(
            "cmd.exe",
            ["/c", "taskkill", "/PID", listener.ProcessId.ToString(), "/F"],
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == 0)
        {
            return new StopProcessResult(true, false, false, null);
        }

        string detail = FirstNonEmpty(result.StandardError, result.StandardOutput, $"exit code {result.ExitCode}");
        return new StopProcessResult(false, false, IsPermissionDeniedError(detail), $"taskkill failed: {detail}");
    }

    private async Task<string> BuildPreviewTextAsync(ResolvedDevProject project, int? effectivePort, string? tailscaleUrl, CancellationToken cancellationToken)
    {
        string? tailscaleDnsName = await TryGetTailscaleDnsNameAsync(cancellationToken).ConfigureAwait(false);
        string? previewStatus = string.IsNullOrWhiteSpace(project.PreviewCommand)
            ? null
            : await TryRunShellCommandAsync(project.WorkingDirectory, project.PreviewCommand, cancellationToken).ConfigureAwait(false);

        List<string> lines = [
            $"Preview for {ResolveProjectName(project.WorkingDirectory)}:",
            $"Cwd: {project.WorkingDirectory}",
            effectivePort.HasValue ? $"Port: {effectivePort.Value}" : "Port: unknown",
            effectivePort.HasValue ? $"Local URL: http://localhost:{effectivePort.Value}" : "Local URL: unavailable",
            string.IsNullOrWhiteSpace(tailscaleUrl) ? "Tailscale URL: inactive" : $"Tailscale URL: {tailscaleUrl}",
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

    private string BuildStatusText(ResolvedDevProject project, DevProcessRecord? record, int? effectivePort, string? tailscaleUrl, bool includePreview, string heading)
    {
        List<string> lines = [
            heading,
            $"Project: {ResolveProjectName(project.WorkingDirectory)}",
            $"Running: {(record is not null && IsProcessRunning(record.ProcessId) ? "yes" : "no")}",
            $"Cwd: {project.WorkingDirectory}",
            $"Command: {project.Command}",
            effectivePort.HasValue ? $"Port: {effectivePort.Value}" : "Port: unknown",
            $"Log: {project.LogPath}",
        ];

        if (record is not null && IsProcessRunning(record.ProcessId))
        {
            lines.Add($"PID: {record.ProcessId}");
            lines.Add($"Started: {record.StartedAtUtc:O}");
        }

        if (includePreview)
        {
            lines.Add(effectivePort.HasValue ? $"Local URL: http://localhost:{effectivePort.Value}" : "Local URL: unavailable");
            lines.Add(string.IsNullOrWhiteSpace(tailscaleUrl) ? "Tailscale URL: inactive" : $"Tailscale URL: {tailscaleUrl}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string ResolveProjectName(string workingDirectory)
    {
        string name = Path.GetFileName(workingDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? workingDirectory : name;
    }

    internal static string ResolveKillDevPortsScriptPath()
    {
        string projectLocalPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "kill-dev-ports.sh"));
        if (File.Exists(projectLocalPath))
        {
            return projectLocalPath;
        }

        return Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "src", "Incursa.Codex.Telegram", "kill-dev-ports.sh"));
    }

    internal static IReadOnlyList<int> GetKillDevPorts()
        => KillDevPorts;

    internal static string BuildKillDevPortsReport(
        IReadOnlyList<int> checkedPorts,
        IReadOnlyDictionary<int, IReadOnlyList<int>> detectedPidsByPort,
        IReadOnlyDictionary<int, IReadOnlyList<int>> killedPidsByPort,
        IReadOnlyList<int> alreadyFreePorts,
        IReadOnlyDictionary<int, string>? failedPorts = null,
        bool requiresPasswordlessSudo = false)
    {
        List<string> lines = ["🧹 Kill Dev Ports", $"Checked ports: {string.Join(", ", checkedPorts)}"];

        if (detectedPidsByPort.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("🔎 Detected PIDs:");
            lines.AddRange(detectedPidsByPort
                .OrderBy(entry => entry.Key)
                .Select(entry => $"* {entry.Key}: {string.Join(", ", entry.Value)}"));
        }

        lines.Add(string.Empty);
        lines.Add("✅ Cleared:");
        lines.AddRange(killedPidsByPort.Count == 0
            ? ["* (none)"]
            : killedPidsByPort
                .OrderBy(entry => entry.Key)
                .Select(entry => $"* {entry.Key} (killed: {string.Join(", ", entry.Value)})"));

        lines.Add(string.Empty);
        lines.Add("ℹ Already free:");
        lines.AddRange(alreadyFreePorts.Count == 0
            ? ["* (none)"]
            : alreadyFreePorts.Select(port => $"* {port}"));

        if (failedPorts is not null && failedPorts.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("⚠ Still busy:");
            lines.AddRange(failedPorts.OrderBy(entry => entry.Key).Select(entry => $"* {entry.Key} ({entry.Value})"));
        }

        if (requiresPasswordlessSudo)
        {
            lines.Add(string.Empty);
            lines.Add("⚠ Requires passwordless sudo for full functionality.");
            lines.Add("sudo visudo");
            lines.Add("Add:");
            lines.Add("wakidu ALL=(ALL) NOPASSWD: /usr/bin/ss, /bin/kill, /usr/bin/kill");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildForceStopPortText(
        int port,
        IReadOnlyList<ListeningProcessInfo> listeners,
        IReadOnlyList<ListeningProcessInfo> killed,
        IReadOnlyList<StopAttemptInfo> failed,
        int removedRecords,
        bool portStillOccupied)
    {
        List<string> lines = [$"Force stop for port {port}."];
        IReadOnlyList<ListeningProcessInfo> unixListeners = listeners.Where(listener => listener.Source == ListenerSource.Unix).ToArray();
        IReadOnlyList<ListeningProcessInfo> windowsListeners = listeners.Where(listener => listener.Source == ListenerSource.Windows).ToArray();
        if (listeners.Count == 0)
        {
            lines.Add("No listening process was found on that TCP port.");
        }
        else
        {
            lines.Add(unixListeners.Count == 0 ? "WSL listeners found: none" : "WSL listeners found:");
            if (unixListeners.Count > 0)
            {
                lines.AddRange(unixListeners.Select(FormatListeningProcess));
            }

            lines.Add(windowsListeners.Count == 0 ? "Windows listeners found: none" : "Windows listeners found:");
            if (windowsListeners.Count > 0)
            {
                lines.AddRange(windowsListeners.Select(FormatListeningProcess));
            }
        }

        if (killed.Count > 0)
        {
            lines.Add("Killed listeners:");
            lines.AddRange(killed.Select(FormatKilledProcess));
        }

        if (failed.Count > 0)
        {
            lines.Add("Failed listeners:");
            lines.AddRange(failed.Select(FormatStopAttempt));
        }

        if (windowsListeners.Count > 0)
        {
            lines.Add("Windows diagnostics:");
            foreach (ListeningProcessInfo listener in windowsListeners)
            {
                lines.AddRange(FormatWindowsListenerDiagnostics(listener));
            }
        }

        lines.Add($"Tracked runtime records removed: {removedRecords}");
        lines.Add(portStillOccupied ? "Port check: still occupied." : "Port check: clear.");
        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildKillAllDevServersText(
        IReadOnlyCollection<int> candidatePorts,
        IReadOnlyList<ListeningProcessInfo> killedTargets,
        IReadOnlyList<StopAttemptInfo> failedTargets,
        IReadOnlyList<ListeningProcessInfo> skippedTargets,
        int removedRecords)
    {
        List<string> lines =
        [
            "Kill-all dev cleanup finished.",
            candidatePorts.Count == 0
                ? "Candidate ports: none"
                : $"Candidate ports: {string.Join(", ", candidatePorts.OrderBy(port => port))}",
        ];

        IReadOnlyList<ListeningProcessInfo> killedUnix = killedTargets.Where(listener => listener.Source == ListenerSource.Unix).ToArray();
        IReadOnlyList<ListeningProcessInfo> killedWindows = killedTargets.Where(listener => listener.Source == ListenerSource.Windows).ToArray();
        if (killedTargets.Count == 0)
        {
            lines.Add("Killed listeners: none");
        }
        else
        {
            if (killedUnix.Count > 0)
            {
                lines.Add("Killed WSL listeners:");
                lines.AddRange(killedUnix.Select(FormatKilledProcess));
            }

            if (killedWindows.Count > 0)
            {
                lines.Add("Killed Windows listeners:");
                lines.AddRange(killedWindows.Select(FormatKilledProcess));
            }
        }

        if (failedTargets.Count > 0)
        {
            lines.Add("Failed listeners:");
            lines.AddRange(failedTargets.Select(FormatStopAttempt));
        }

        if (skippedTargets.Count > 0)
        {
            lines.Add("Skipped non-dev listeners:");
            lines.AddRange(skippedTargets.Select(FormatListeningProcess));
        }

        lines.Add($"Tracked runtime records removed: {removedRecords}");
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatListeningProcess(ListeningProcessInfo info)
    {
        string sudoText = info.StoppedWithSudo ? " · sudo" : string.Empty;
        string locationText = string.IsNullOrWhiteSpace(info.Endpoint) ? $"port {info.Port}" : info.Endpoint;
        if (info.Source == ListenerSource.Windows)
        {
            return $"- PID {info.ProcessId} · {info.ProcessName} · {locationText}{sudoText}";
        }

        string commandText = string.IsNullOrWhiteSpace(info.CommandLine)
            ? info.ProcessName
            : info.CommandLine;
        return $"- PID {info.ProcessId} · {locationText} · {commandText}{sudoText}";
    }

    private static string FormatStopAttempt(StopAttemptInfo attempt)
        => $"{FormatListeningProcess(attempt.Listener)} · {attempt.Diagnostic ?? "stop failed"}";

    private static string FormatKilledProcess(ListeningProcessInfo info)
        => $"Killed PID {info.ProcessId} · {info.ProcessName} · {info.Endpoint ?? $"port {info.Port}"}{(info.StoppedWithSudo ? " · sudo" : string.Empty)}.";

    private static IReadOnlyList<string> FormatWindowsListenerDiagnostics(ListeningProcessInfo listener)
    {
        List<string> lines = [$"Windows PID {listener.ProcessId} diagnostics:"];
        if (!string.IsNullOrWhiteSpace(listener.DetectionCommand))
        {
            lines.Add($"- Netstat command: {listener.DetectionCommand}");
        }

        if (!string.IsNullOrWhiteSpace(listener.DetectionOutput))
        {
            lines.Add($"- Netstat output: {listener.DetectionOutput}");
        }

        if (!string.IsNullOrWhiteSpace(listener.ParsedListenerRow))
        {
            lines.Add($"- Parsed LISTENING row: {listener.ParsedListenerRow}");
        }

        lines.Add($"- Parsed PID: {listener.ProcessId}");

        if (!string.IsNullOrWhiteSpace(listener.InspectionCommand))
        {
            lines.Add($"- Tasklist command: {listener.InspectionCommand}");
        }

        if (!string.IsNullOrWhiteSpace(listener.InspectionOutput))
        {
            lines.Add($"- Tasklist output: {listener.InspectionOutput}");
        }

        if (!string.IsNullOrWhiteSpace(listener.SafetyReason))
        {
            lines.Add($"- Safety decision: {listener.SafetyReason}");
        }
        else
        {
            lines.Add("- Safety decision: safe to kill automatically");
        }

        if (!string.IsNullOrWhiteSpace(listener.SuggestedKillCommand))
        {
            lines.Add($"- Taskkill command: {listener.SuggestedKillCommand}");
        }

        return lines;
    }

    private static ListeningProcessInfo DecorateListener(ListeningProcessInfo listener, StopProcessResult stopResult)
        => listener with { StoppedWithSudo = stopResult.UsedSudo };

    internal static string EscapeBashSingleQuoted(string value)
        => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private string ResolveLogPath(string workingDirectory, string logSetting)
    {
        string path = Path.IsPathRooted(logSetting)
            ? Path.GetFullPath(logSetting)
            : Path.GetFullPath(Path.Combine(GetDevLogsRootPath(), BuildProjectLogDirectoryName(workingDirectory), logSetting));

        string? parent = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(parent))
        {
            return path;
        }

        IReadOnlyList<string> roots = _workspaceBrowser.GetWorkspaceRoots();
        string dataRoot = GetDataRootPath();
        bool allowed = roots.Any(root => IsPathUnderRoot(path, root)) || IsPathUnderRoot(path, dataRoot);
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

    private async Task<string?> TryGetActiveTailscaleUrlAsync(int? port, CancellationToken cancellationToken)
    {
        if (!port.HasValue)
        {
            return null;
        }

        string? status = await TryRunShellCommandAsync(Environment.CurrentDirectory, "tailscale serve status", cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(status) ? null : ParseTailscaleUrlForPort(status, port.Value);
    }

    private async Task<int?> GetEffectivePortAsync(ResolvedDevProject project, DevProcessRecord? record, CancellationToken cancellationToken)
    {
        int? detectedPort = record?.DetectedPort;
        if (!detectedPort.HasValue)
        {
            detectedPort = await TryDetectPortAsync(project.LogPath, cancellationToken).ConfigureAwait(false);
            if (detectedPort.HasValue && record is not null)
            {
                record = record with { DetectedPort = detectedPort };
                await _stateStore.UpsertAsync(record, cancellationToken).ConfigureAwait(false);
            }
        }

        return detectedPort ?? project.Port;
    }

    private async Task<int?> TryDetectPortAsync(string logPath, CancellationToken cancellationToken)
    {
        string logs = await ReadTailAsync(logPath, Math.Clamp(_options.Value.DevUtilities.LogTailLineCount, 10, 200), cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(logs) ? null : ParseDetectedPort(logs);
    }

    internal static int? ParseDetectedPort(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        int? lastPort = null;
        foreach (Match match in DetectedPortRegex.Matches(text))
        {
            if (int.TryParse(match.Groups["port"].Value, out int port) && port is >= 1 and <= 65535)
            {
                lastPort = port;
            }
        }

        return lastPort;
    }

    internal static string? ParseTailscaleUrlForPort(string text, int port)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string? currentUrl = null;
        foreach (string rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            Match urlMatch = TailscaleUrlRegex.Match(line);
            if (urlMatch.Success)
            {
                currentUrl = urlMatch.Value;
            }

            if (line.Contains($":{port}", StringComparison.Ordinal)
                && line.Contains("proxy", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(currentUrl))
            {
                return currentUrl;
            }
        }

        return null;
    }

    private async Task<HashSet<int>> GetCandidateCleanupPortsAsync(CancellationToken cancellationToken)
    {
        HashSet<int> ports = [];
        foreach (int port in _options.Value.Tailscale.Ports.Where(IsValidPort))
        {
            ports.Add(port);
        }

        foreach (TailscaleUtilityEntryOptions entry in _options.Value.Tailscale.Entries)
        {
            if (IsValidPort(entry.Port))
            {
                ports.Add(entry.Port);
            }
        }

        foreach (DevUtilityProjectOptions project in _options.Value.DevUtilities.Projects.Values)
        {
            if (project.Port is int port && IsValidPort(port))
            {
                ports.Add(port);
            }
        }

        foreach (int commonPort in new[] { 3000, 3001, 3100, 3500, 4000, 5173, 5177 })
        {
            ports.Add(commonPort);
        }

        IReadOnlyList<DevProcessRecord> records = await _stateStore.ListAsync(cancellationToken).ConfigureAwait(false);
        foreach (DevProcessRecord record in records)
        {
            if (record.Port is int configuredPort && IsValidPort(configuredPort))
            {
                ports.Add(configuredPort);
            }

            if (record.DetectedPort is int detectedPort && IsValidPort(detectedPort))
            {
                ports.Add(detectedPort);
            }
        }

        return ports;
    }

    private async Task<int> CleanupTrackedRecordsAsync(
        Func<DevProcessRecord, bool> shouldRemove,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<DevProcessRecord> records = await _stateStore.ListAsync(cancellationToken).ConfigureAwait(false);
        int removed = 0;
        foreach (DevProcessRecord record in records)
        {
            if (!shouldRemove(record))
            {
                continue;
            }

            if (await _stateStore.RemoveAsync(record.WorkingDirectory, cancellationToken).ConfigureAwait(false))
            {
                removed++;
            }
        }

        return removed;
    }

    private async Task<IReadOnlyList<ListeningProcessInfo>> GetListeningProcessesByPortAsync(int port, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            string netstatOutput = await RunProcessForOutputAsync("netstat", "-ano -p tcp", cancellationToken).ConfigureAwait(false);
            return FilterProtectedListeners(ParseNetstatListeners(netstatOutput, port));
        }

        List<ListeningProcessInfo> listeners = [];

        string ssCommand = $"ss -lptn 'sport = :{port}'";
        string ssOutput = await RunProcessForOutputAsync("/bin/bash", $"-lc {EscapeBashSingleQuoted(ssCommand)}", cancellationToken).ConfigureAwait(false);
        listeners.AddRange(FilterProtectedListeners(ParseSsListeners(ssOutput, port)));

        string lsofCommand = $"sudo -n lsof -nP -iTCP:{port} -sTCP:LISTEN -Fpcn";
        string lsofOutput = await RunProcessForOutputAsync("/bin/bash", $"-lc {EscapeBashSingleQuoted(lsofCommand)}", cancellationToken).ConfigureAwait(false);
        if (listeners.Count == 0)
        {
            listeners.AddRange(FilterProtectedListeners(ParseLsofListeners(lsofOutput, port)));
        }

        string fuserCommand = $"sudo -n fuser {port}/tcp 2>/dev/null";
        string fuserOutput = await RunProcessForOutputAsync("/bin/bash", $"-lc {EscapeBashSingleQuoted(fuserCommand)}", cancellationToken).ConfigureAwait(false);
        if (listeners.Count == 0)
        {
            listeners.AddRange(FilterProtectedListeners(ParseFuserListeners(fuserOutput, port)));
        }

        listeners.AddRange(await GetWindowsListeningProcessesByPortAsync(port, cancellationToken).ConfigureAwait(false));
        return listeners
            .GroupBy(listener => $"{listener.Source}:{listener.ProcessId}:{listener.Port}")
            .Select(group => group.First())
            .ToArray();
    }

    private async Task<IReadOnlyList<ListeningProcessInfo>> GetWindowsListeningProcessesByPortAsync(int port, CancellationToken cancellationToken)
    {
        string netstatCommand = $"cmd.exe /c netstat -ano -p tcp | findstr :{port}";
        CommandExecutionResult result;
        try
        {
            result = await RunProcessCaptureAsync(
                "cmd.exe",
                ["/c", $"netstat -ano -p tcp | findstr :{port}"],
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Windows interop is unavailable for port {Port}.", port);
            return [];
        }

        string output = FirstNonEmpty(result.StandardOutput, result.StandardError);
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        IReadOnlyList<WindowsListenerInfo> listeners = ParseWindowsNetstatListeners(output, port);
        if (listeners.Count == 0)
        {
            return [];
        }

        List<ListeningProcessInfo> results = [];
        foreach (WindowsListenerInfo listener in listeners)
        {
            WindowsProcessInspection inspection = await InspectWindowsProcessAsync(listener.ProcessId, cancellationToken).ConfigureAwait(false);
            string processName = string.IsNullOrWhiteSpace(inspection.ProcessName) ? "unknown" : inspection.ProcessName;
            string? safetyReason = BuildWindowsSafetyReason(processName);
            results.Add(new ListeningProcessInfo(
                listener.ProcessId,
                listener.Port,
                processName,
                processName,
                listener.LocalAddress,
                false,
                ListenerSource.Windows,
                netstatCommand,
                output,
                listener.RawLine,
                inspection.Command,
                inspection.RawOutput,
                safetyReason,
                $"cmd.exe /c taskkill /PID {listener.ProcessId} /F"));
        }

        return FilterProtectedListeners(results);
    }

    private async Task<WindowsProcessInspection> InspectWindowsProcessAsync(int processId, CancellationToken cancellationToken)
    {
        string command = $"cmd.exe /c tasklist /FI \"PID eq {processId}\" /FO CSV /NH";
        try
        {
            CommandExecutionResult result = await RunProcessCaptureAsync(
                "cmd.exe",
                ["/c", "tasklist", "/FI", $"PID eq {processId}", "/FO", "CSV", "/NH"],
                cancellationToken).ConfigureAwait(false);
            string output = FirstNonEmpty(result.StandardOutput, result.StandardError);
            return new WindowsProcessInspection(
                ParseWindowsTasklistProcessName(output, processId),
                command,
                output);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Failed to inspect Windows PID {ProcessId}.", processId);
            return new WindowsProcessInspection(null, command, exception.Message);
        }
    }

    private async Task<string> RunProcessForOutputAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        try
        {
            CommandExecutionResult result = await RunProcessCaptureAsync(fileName, arguments, cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(result.StandardOutput) ? result.StandardError : result.StandardOutput;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Failed to inspect local listeners using {Executable}.", fileName);
            return string.Empty;
        }
    }

    private async Task<CommandExecutionResult> RunProcessCaptureAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return await RunProcessCaptureAsync(startInfo, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CommandExecutionResult> RunProcessCaptureAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        return await RunProcessCaptureAsync(startInfo, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CommandExecutionResult> RunShellCommandCaptureAsync(string command, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            return await RunProcessCaptureAsync("cmd.exe", $"/d /s /c \"{command}\"", cancellationToken).ConfigureAwait(false);
        }

        return await RunProcessCaptureAsync("/bin/bash", $"-lc {EscapeBashSingleQuoted(command)}", cancellationToken).ConfigureAwait(false);
    }

    private static async Task<CommandExecutionResult> RunProcessCaptureAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start {startInfo.FileName}.");
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new CommandExecutionResult(
            process.ExitCode,
            (await stdoutTask.ConfigureAwait(false)).Trim(),
            (await stderrTask.ConfigureAwait(false)).Trim());
    }

    internal static IReadOnlyList<ListeningProcessInfo> ParseLsofListeners(string text, int port)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        List<ListeningProcessInfo> listeners = [];
        int? processId = null;
        string? processName = null;
        string? endpoint = null;
        foreach (string rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (rawLine.Length < 2)
            {
                continue;
            }

            char prefix = rawLine[0];
            string value = rawLine[1..];
            switch (prefix)
            {
                case 'p':
                    if (processId.HasValue)
                    {
                        AddListener(listeners, processId.Value, processName, endpoint, port);
                    }

                    processId = int.TryParse(value, out int parsedProcessId) ? parsedProcessId : null;
                    processName = null;
                    endpoint = null;
                    break;
                case 'c':
                    processName = value;
                    break;
                case 'n':
                    endpoint = value;
                    break;
            }
        }

        if (processId.HasValue)
        {
            AddListener(listeners, processId.Value, processName, endpoint, port);
        }

        return listeners;
    }

    internal static IReadOnlyList<ListeningProcessInfo> ParseSsListeners(string text, int port)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        List<ListeningProcessInfo> listeners = [];
        foreach (string rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (!line.Contains("LISTEN", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!line.Contains($":{port}", StringComparison.Ordinal))
            {
                continue;
            }

            MatchCollection pidMatches = Regex.Matches(line, @"pid=(\d+)", RegexOptions.CultureInvariant);
            if (pidMatches.Count == 0)
            {
                continue;
            }

            string processName = TryParseSsProcessName(line) ?? "unknown";
            foreach (Match match in pidMatches)
            {
                if (!int.TryParse(match.Groups[1].Value, out int processId))
                {
                    continue;
                }

                string? commandLine = TryReadProcessCommandLine(processId);
                listeners.Add(new ListeningProcessInfo(processId, port, processName, commandLine ?? line, $"{port}/tcp", false, ListenerSource.Unix));
            }
        }

        return listeners;
    }

    internal static IReadOnlyList<ListeningProcessInfo> ParseNetstatListeners(string text, int port)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        List<ListeningProcessInfo> listeners = [];
        foreach (string rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (!line.Contains("LISTENING", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5)
            {
                continue;
            }

            string localAddress = parts[1];
            if (!TryParsePortFromEndpoint(localAddress, out int parsedPort) || parsedPort != port)
            {
                continue;
            }

            if (!int.TryParse(parts[^1], out int processId))
            {
                continue;
            }

            string processName = TryGetProcess(processId)?.ProcessName ?? "unknown";
            listeners.Add(new ListeningProcessInfo(processId, parsedPort, processName, processName, localAddress, false, ListenerSource.Unix));
        }

        return listeners;
    }

    internal static IReadOnlyList<WindowsListenerInfo> ParseWindowsNetstatListeners(string text, int port)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        List<WindowsListenerInfo> listeners = [];
        foreach (string rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (!line.Contains("LISTENING", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5)
            {
                continue;
            }

            string localAddress = parts[1];
            if (!TryParsePortFromEndpoint(localAddress, out int parsedPort) || parsedPort != port)
            {
                continue;
            }

            if (!int.TryParse(parts[^1], out int processId))
            {
                continue;
            }

            listeners.Add(new WindowsListenerInfo(processId, parsedPort, localAddress, line));
        }

        return listeners;
    }

    internal static IReadOnlyList<ListeningProcessInfo> ParseFuserListeners(string text, int port)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        int separatorIndex = text.IndexOf(':');
        string pidSegment = separatorIndex >= 0 ? text[(separatorIndex + 1)..] : text;
        List<ListeningProcessInfo> listeners = [];
        foreach (Match match in Regex.Matches(pidSegment, @"\b\d+\b", RegexOptions.CultureInvariant))
        {
            if (!int.TryParse(match.Value, out int processId))
            {
                continue;
            }

            Process? process = TryGetProcess(processId);
            string processName = process?.ProcessName ?? "unknown";
            listeners.Add(new ListeningProcessInfo(processId, port, processName, TryReadProcessCommandLine(processId), $"{port}/tcp", false, ListenerSource.Unix));
        }

        return listeners;
    }

    internal static string? ParseWindowsTasklistProcessName(string text, int processId)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Contains("No tasks are running", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (string rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (!line.StartsWith('"'))
            {
                continue;
            }

            string[] parts = line.Split("\",\"", StringSplitOptions.None);
            if (parts.Length < 2)
            {
                continue;
            }

            string imageName = parts[0].Trim('"');
            string pidText = parts[1].Trim('"');
            if (int.TryParse(pidText, out int parsedPid) && parsedPid == processId)
            {
                return imageName;
            }
        }

        return null;
    }

    private static string? TryParseSsProcessName(string line)
    {
        Match match = Regex.Match(line, "\\(\"(?<name>[^\"]+)\"", RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return null;
        }

        string name = match.Groups["name"].Value.Trim();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static IReadOnlyList<ListeningProcessInfo> FilterProtectedListeners(IReadOnlyList<ListeningProcessInfo> listeners)
        => listeners.Where(listener => !IsProtectedListener(listener)).ToArray();

    private static bool IsProtectedListener(ListeningProcessInfo listener)
        => IsProtectedSystemProcessId(listener.ProcessId)
            || string.Equals(listener.ProcessName, "systemd", StringComparison.OrdinalIgnoreCase)
            || string.Equals(listener.ProcessName, "init", StringComparison.OrdinalIgnoreCase)
            || (listener.CommandLine?.Contains("/sbin/init", StringComparison.OrdinalIgnoreCase) ?? false)
            || (listener.CommandLine?.Contains(" systemd", StringComparison.OrdinalIgnoreCase) ?? false);

    internal static bool IsPermissionDeniedError(string? text)
        => !string.IsNullOrWhiteSpace(text)
            && (text.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
                || text.Contains("operation not permitted", StringComparison.OrdinalIgnoreCase)
                || text.Contains("not permitted", StringComparison.OrdinalIgnoreCase)
                || text.Contains("access denied", StringComparison.OrdinalIgnoreCase));

    internal static bool RequiresPasswordlessSudo(string? text)
        => !string.IsNullOrWhiteSpace(text)
            && (text.Contains("password", StringComparison.OrdinalIgnoreCase)
                || text.Contains("a password is required", StringComparison.OrdinalIgnoreCase)
                || text.Contains("sudo:", StringComparison.OrdinalIgnoreCase));

    internal static bool IsProtectedSystemProcessId(int processId)
        => processId <= 1;

    private static string BuildSignalDiagnostic(string signal, CommandExecutionResult result, bool usedSudo)
    {
        string detail = FirstNonEmpty(result.StandardError, result.StandardOutput, $"exit code {result.ExitCode}");
        if (usedSudo && IsPermissionDeniedError(detail))
        {
            return $"sudo {signal} failed: {detail}";
        }

        if (usedSudo)
        {
            return $"sudo retry for {signal} failed: {detail}";
        }

        if (IsPermissionDeniedError(detail))
        {
            return $"permission denied during {signal}; sudo retry unavailable or failed: {detail}";
        }

        return $"{signal} failed: {detail}";
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private string GetDevLogsRootPath()
        => Path.Combine(GetDataRootPath(), "dev-logs");

    private string GetDataRootPath()
    {
        string? configuredRoot = _options.Value.Workspace.DataRoot;
        return string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(AppContext.BaseDirectory, "App_Data", "codex-telegram")
            : Path.GetFullPath(configuredRoot);
    }

    private static string BuildProjectLogDirectoryName(string workingDirectory)
    {
        string projectName = ResolveProjectName(workingDirectory);
        StringBuilder builder = new(projectName.Length);
        foreach (char character in projectName)
        {
            builder.Append(char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-');
        }

        return builder.Length == 0 ? "project" : builder.ToString().Trim('-');
    }

    private static string SanitizeMultiline(string text)
        => string.Join(Environment.NewLine, text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(SanitizeLogLine));

    private static void AddListener(List<ListeningProcessInfo> listeners, int processId, string? processName, string? endpoint, int fallbackPort)
    {
        int port = TryParsePortFromEndpoint(endpoint, out int parsedPort) ? parsedPort : fallbackPort;
        string normalizedName = string.IsNullOrWhiteSpace(processName) ? "unknown" : processName.Trim();
        string? commandLine = TryReadProcessCommandLine(processId);
        listeners.Add(new ListeningProcessInfo(processId, port, normalizedName, commandLine, endpoint, false, ListenerSource.Unix));
    }

    private static bool TryParsePortFromEndpoint(string? endpoint, out int port)
    {
        port = 0;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return false;
        }

        int separatorIndex = endpoint.LastIndexOf(':');
        if (separatorIndex < 0 || separatorIndex >= endpoint.Length - 1)
        {
            return false;
        }

        return int.TryParse(endpoint[(separatorIndex + 1)..], out port) && IsValidPort(port);
    }

    private static bool IsValidPort(int port)
        => port is >= 1 and <= 65535;

    private static string? TryReadProcessCommandLine(int processId)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return TryGetProcess(processId)?.ProcessName;
            }

            string path = $"/proc/{processId}/cmdline";
            if (!File.Exists(path))
            {
                return null;
            }

            string raw = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            string commandLine = raw.Replace('\0', ' ').Trim();
            return string.IsNullOrWhiteSpace(commandLine) ? null : commandLine;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSafeDevProcess(ListeningProcessInfo listener)
    {
        if (listener.Source == ListenerSource.Windows)
        {
            return IsSafeWindowsListener(listener);
        }

        if (SafeDevProcessNames.Contains(listener.ProcessName))
        {
            return true;
        }

        string commandLine = listener.CommandLine ?? string.Empty;
        return commandLine.Contains("next dev", StringComparison.OrdinalIgnoreCase)
            || commandLine.Contains("vite", StringComparison.OrdinalIgnoreCase)
            || commandLine.Contains("npm run dev", StringComparison.OrdinalIgnoreCase)
            || commandLine.Contains("pnpm dev", StringComparison.OrdinalIgnoreCase)
            || commandLine.Contains("yarn dev", StringComparison.OrdinalIgnoreCase)
            || commandLine.Contains("bun run dev", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafeWindowsListener(ListeningProcessInfo listener)
    {
        return IsSafeWindowsProcessName(listener.ProcessName);
    }

    internal static bool IsSafeWindowsProcessName(string? processName)
    {
        string normalized = processName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (SafeDevProcessNames.Contains(normalized))
        {
            return true;
        }

        return normalized.Contains("node", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("next", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("vite", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("npm", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("pnpm", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("bun", StringComparison.OrdinalIgnoreCase);
    }

    private static string? BuildWindowsSafetyReason(string? processName)
    {
        string normalized = processName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "unsafe to kill automatically: process name could not be identified";
        }

        if (IsSafeWindowsProcessName(normalized))
        {
            return null;
        }

        return $"unsafe to kill automatically: process name '{normalized}' is not in the dev-server allowlist";
    }

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

    internal sealed record ListeningProcessInfo(
        int ProcessId,
        int Port,
        string ProcessName,
        string? CommandLine,
        string? Endpoint,
        bool StoppedWithSudo = false,
        ListenerSource Source = ListenerSource.Unix,
        string? DetectionCommand = null,
        string? DetectionOutput = null,
        string? ParsedListenerRow = null,
        string? InspectionCommand = null,
        string? InspectionOutput = null,
        string? SafetyReason = null,
        string? SuggestedKillCommand = null);

    private sealed record StopProcessResult(
        bool Stopped,
        bool UsedSudo,
        bool PermissionDenied,
        string? Diagnostic);

    private sealed record StopAttemptInfo(
        ListeningProcessInfo Listener,
        string? Diagnostic);

    private sealed record CommandExecutionResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);

    internal sealed record WindowsListenerInfo(
        int ProcessId,
        int Port,
        string LocalAddress,
        string RawLine);

    private sealed record WindowsProcessInspection(
        string? ProcessName,
        string Command,
        string RawOutput);

    internal enum ListenerSource
    {
        Unix,
        Windows,
    }
}
