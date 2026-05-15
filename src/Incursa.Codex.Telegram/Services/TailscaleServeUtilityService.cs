using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Services;

internal interface ITailscaleServeUtilityService
{
    Task<TailscaleServeMenuState> GetMenuStateAsync(CancellationToken cancellationToken);

    Task<TailscaleServeToggleResult> TogglePortAsync(int port, CancellationToken cancellationToken);

    Task<TailscaleServeResetResult> ResetAsync(CancellationToken cancellationToken);
}

internal sealed record TailscaleServeEntryState(string Name, int Port, bool Enabled, string LocalUrl, string? TailscaleUrl);

internal sealed record TailscaleServeMenuState(
    IReadOnlyList<TailscaleServeEntryState> Entries,
    string? Hostname,
    IReadOnlySet<int> ActivePorts,
    string? ConfigurationSourcePath);

internal sealed record TailscaleServeToggleResult(bool Enabled, int Port, string Message, string LocalUrl, string? TailscaleUrl);

internal sealed record TailscaleServeResetResult(string Message);

internal sealed record TailscaleCommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool TimedOut { get; init; }

    public string CombinedText
        => string.IsNullOrWhiteSpace(StandardError)
            ? StandardOutput.Trim()
            : (StandardOutput + Environment.NewLine + StandardError).Trim();
}

internal interface ITailscaleServeCommandExecutor
{
    Task<TailscaleCommandResult> ExecuteAsync(ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken cancellationToken);
}

internal sealed class TailscaleServeCommandExecutor : ITailscaleServeCommandExecutor
{
    public async Task<TailscaleCommandResult> ExecuteAsync(ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken cancellationToken)
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
            return new TailscaleCommandResult(
                -1,
                await stdoutTask.ConfigureAwait(false),
                await stderrTask.ConfigureAwait(false))
            {
                TimedOut = true,
            };
        }

        return new TailscaleCommandResult(
            process.ExitCode,
            await stdoutTask.ConfigureAwait(false),
            await stderrTask.ConfigureAwait(false));
    }
}

internal sealed class TailscaleServeUtilityService : ITailscaleServeUtilityService
{
    private static readonly Regex LocalhostPortRegex = new(@"(?:https?://|tcp://)?(?:localhost|127\.0\.0\.1|\[::1\]):(?<port>\d{1,5})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly TailscaleUtilityOptions _options;
    private readonly ITailscaleServeCommandExecutor _executor;
    private readonly ILogger<TailscaleServeUtilityService> _logger;

    public TailscaleServeUtilityService(
        IOptions<CodexTelegramOptions> options,
        ITailscaleServeCommandExecutor executor,
        ILogger<TailscaleServeUtilityService> logger)
        : this(options.Value.Tailscale, executor, logger)
    {
    }

    internal TailscaleServeUtilityService(
        TailscaleUtilityOptions options,
        ITailscaleServeCommandExecutor executor,
        ILogger<TailscaleServeUtilityService> logger)
    {
        _options = options;
        _executor = executor;
        _logger = logger;
    }

    public async Task<TailscaleServeMenuState> GetMenuStateAsync(CancellationToken cancellationToken)
    {
        string? hostname = await TryGetHostnameAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlySet<int> activePorts = await GetActivePortsAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<TailscaleUtilityEntryOptions> configuredEntries = GetConfiguredEntries();
        List<TailscaleServeEntryState> entries = configuredEntries
            .Select(entry => new TailscaleServeEntryState(
                string.IsNullOrWhiteSpace(entry.Name) ? entry.Port.ToString() : entry.Name.Trim(),
                entry.Port,
                activePorts.Contains(entry.Port),
                $"http://localhost:{entry.Port}",
                BuildRootTailscaleUrl(hostname)))
            .OrderBy(entry => entry.Port)
            .ToList();

        return new TailscaleServeMenuState(entries, hostname, activePorts, _options.ConfigurationSourcePath);
    }

    public async Task<TailscaleServeToggleResult> TogglePortAsync(int port, CancellationToken cancellationToken)
    {
        ValidatePort(port);
        _logger.LogInformation("Tailscale toggle requested for port {Port}.", port);
        IReadOnlySet<int> activePorts = await GetActivePortsAsync(cancellationToken).ConfigureAwait(false);
        bool enabled = !activePorts.Contains(port);
        ProcessStartInfo startInfo = enabled
            ? BuildEnableStartInfo(_options.ExecutablePath, port)
            : BuildDisableStartInfo(_options.ExecutablePath);

        TailscaleCommandResult result = await ExecuteSafeAsync(startInfo, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw CreateCommandFailure(enabled ? "enable" : "disable", port, startInfo, result);
        }

        string? hostname = await TryGetHostnameAsync(cancellationToken).ConfigureAwait(false);
        string localUrl = $"http://localhost:{port}";
        string? tailscaleUrl = BuildRootTailscaleUrl(hostname);
        string message = enabled
            ? $"Enabled Tailscale Serve root route for backend port {port}."
            : $"Disabled the current Tailscale Serve root route for backend port {port}.";

        return new TailscaleServeToggleResult(enabled, port, message, localUrl, tailscaleUrl);
    }

    public async Task<TailscaleServeResetResult> ResetAsync(CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = BuildResetStartInfo(_options.ExecutablePath);
        _logger.LogInformation("Tailscale reset requested.");
        TailscaleCommandResult result = await ExecuteSafeAsync(startInfo, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw CreateCommandFailure("reset", null, startInfo, result);
        }

        return new TailscaleServeResetResult("Reset all Tailscale Serve routes.");
    }

    internal static ProcessStartInfo BuildEnableStartInfo(string executablePath, int port)
    {
        ProcessStartInfo startInfo = BuildBaseStartInfo(executablePath);
        startInfo.ArgumentList.Add("serve");
        startInfo.ArgumentList.Add("--bg");
        startInfo.ArgumentList.Add($"http://localhost:{port}");
        return startInfo;
    }

    internal static ProcessStartInfo BuildDisableStartInfo(string executablePath)
    {
        ProcessStartInfo startInfo = BuildBaseStartInfo(executablePath);
        startInfo.ArgumentList.Add("serve");
        startInfo.ArgumentList.Add("off");
        return startInfo;
    }

    internal static ProcessStartInfo BuildServeStatusStartInfo(string executablePath)
    {
        ProcessStartInfo startInfo = BuildBaseStartInfo(executablePath);
        startInfo.ArgumentList.Add("serve");
        startInfo.ArgumentList.Add("status");
        startInfo.ArgumentList.Add("--json");
        return startInfo;
    }

    internal static ProcessStartInfo BuildTailscaleStatusStartInfo(string executablePath)
    {
        ProcessStartInfo startInfo = BuildBaseStartInfo(executablePath);
        startInfo.ArgumentList.Add("status");
        startInfo.ArgumentList.Add("--json");
        return startInfo;
    }

    internal static ProcessStartInfo BuildResetStartInfo(string executablePath)
    {
        ProcessStartInfo startInfo = BuildBaseStartInfo(executablePath);
        startInfo.ArgumentList.Add("serve");
        startInfo.ArgumentList.Add("reset");
        return startInfo;
    }

    internal static IReadOnlySet<int> ParseActivePorts(string rawStatus)
    {
        HashSet<int> ports = [];
        if (string.IsNullOrWhiteSpace(rawStatus))
        {
            return ports;
        }

        foreach (Match match in LocalhostPortRegex.Matches(rawStatus))
        {
            if (int.TryParse(match.Groups["port"].Value, out int port) && port is >= 1 and <= 65535)
            {
                ports.Add(port);
            }
        }

        return ports;
    }

    internal static string? ParseHostname(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(rawJson);
            if (document.RootElement.TryGetProperty("Self", out JsonElement self)
                && self.TryGetProperty("DNSName", out JsonElement dnsName))
            {
                string? value = dnsName.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value.TrimEnd('.');
            }
        }
        catch
        {
        }

        return null;
    }

    internal static string? BuildRootTailscaleUrl(string? hostname)
        => string.IsNullOrWhiteSpace(hostname)
            ? null
            : $"https://{hostname.TrimEnd('/')}/";

    private async Task<IReadOnlySet<int>> GetActivePortsAsync(CancellationToken cancellationToken)
    {
        TailscaleCommandResult result = await ExecuteSafeAsync(BuildServeStatusStartInfo(_options.ExecutablePath), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            _logger.LogDebug("tailscale serve status failed. {Details}", FormatCommandDiagnostics(BuildServeStatusStartInfo(_options.ExecutablePath), result));
            return new HashSet<int>();
        }

        return ParseActivePorts(result.CombinedText);
    }

    private async Task<string?> TryGetHostnameAsync(CancellationToken cancellationToken)
    {
        TailscaleCommandResult result = await ExecuteSafeAsync(BuildTailscaleStatusStartInfo(_options.ExecutablePath), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            _logger.LogDebug("tailscale status failed. {Details}", FormatCommandDiagnostics(BuildTailscaleStatusStartInfo(_options.ExecutablePath), result));
            return null;
        }

        return ParseHostname(result.StandardOutput);
    }

    private async Task<TailscaleCommandResult> ExecuteSafeAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        try
        {
            TimeSpan timeout = TimeSpan.FromSeconds(Math.Clamp(_options.CommandTimeoutSeconds, 1, 120));
            string commandText = FormatCommandText(startInfo);
            _logger.LogInformation("Executing Tailscale command: {Command}", commandText);
            TailscaleCommandResult result = await _executor.ExecuteAsync(startInfo, timeout, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Tailscale command finished. Command: {Command}. ExitCode: {ExitCode}. TimedOut: {TimedOut}. StdoutLength: {StdoutLength}. StderrLength: {StderrLength}.",
                commandText,
                result.ExitCode,
                result.TimedOut,
                result.StandardOutput.Length,
                result.StandardError.Length);
            return result;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Tailscale command failed to start: {Command}", FormatCommandText(startInfo));
            return new TailscaleCommandResult(-1, string.Empty, exception.Message);
        }
    }

    private IReadOnlyList<TailscaleUtilityEntryOptions> GetConfiguredEntries()
    {
        if (_options.Entries.Count > 0)
        {
            return _options.Entries
                .Where(entry => entry.Port is >= 1 and <= 65535)
                .DistinctBy(entry => entry.Port)
                .ToArray();
        }

        return _options.Ports
            .Where(port => port is >= 1 and <= 65535)
            .Distinct()
            .OrderBy(port => port)
            .Select(port => new TailscaleUtilityEntryOptions { Port = port, Name = port.ToString() })
            .ToArray();
    }

    private static ProcessStartInfo BuildBaseStartInfo(string executablePath)
        => new()
        {
            FileName = string.IsNullOrWhiteSpace(executablePath) ? "tailscale" : executablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

    private static void ValidatePort(int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new InvalidOperationException("Port must be between 1 and 65535.");
        }
    }

    private static InvalidOperationException CreateCommandFailure(string action, int? port, ProcessStartInfo startInfo, TailscaleCommandResult result)
    {
        if (ContainsOperatorSetupHint(result))
        {
            return new InvalidOperationException(
                string.Join(
                    Environment.NewLine,
                    [
                        "Tailscale needs operator access before this bot can manage Serve routes.",
                        "Run this once on the machine:",
                        "sudo tailscale set --operator=$USER",
                        string.Empty,
                        FormatCommandDiagnostics(startInfo, result),
                    ]));
        }

        string scope = port is null ? "Tailscale Serve" : $"Tailscale Serve {action} for port {port.Value}";
        return new InvalidOperationException(
            $"{scope} failed.{Environment.NewLine}{FormatCommandDiagnostics(startInfo, result)}");
    }

    internal static string FormatCommandDiagnostics(ProcessStartInfo startInfo, TailscaleCommandResult result)
        => string.Join(
            Environment.NewLine,
            [
                $"Executable: {startInfo.FileName}",
                $"Command: {FormatCommandText(startInfo)}",
                result.TimedOut ? "Exit code: timeout" : $"Exit code: {result.ExitCode}",
                $"Stdout: {FormatMultilineValue(result.StandardOutput)}",
                $"Stderr: {FormatMultilineValue(result.StandardError)}",
            ]);

    internal static string FormatCommandText(ProcessStartInfo startInfo)
    {
        IEnumerable<string> parts = [startInfo.FileName, .. startInfo.ArgumentList.Cast<string>()];
        return string.Join(" ", parts.Select(EscapeArgument));
    }

    private static string EscapeArgument(string value)
        => value.Any(char.IsWhiteSpace) || value.Contains('"')
            ? "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : value;

    private static string FormatMultilineValue(string value)
        => string.IsNullOrWhiteSpace(value) ? "<empty>" : value.Trim();

    private static bool ContainsOperatorSetupHint(TailscaleCommandResult result)
        => (result.StandardError?.Contains("sudo tailscale set --operator=$USER", StringComparison.OrdinalIgnoreCase) ?? false)
            || (result.StandardOutput?.Contains("sudo tailscale set --operator=$USER", StringComparison.OrdinalIgnoreCase) ?? false);
}
