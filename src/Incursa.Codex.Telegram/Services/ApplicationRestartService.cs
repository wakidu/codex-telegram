using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Incursa.Codex.Telegram.Services;

internal interface IApplicationRestartService
{
    Task RequestRestartAsync(CancellationToken cancellationToken);
}

internal sealed class ApplicationRestartService : IApplicationRestartService
{
    private readonly ILogger<ApplicationRestartService> _logger;

    public ApplicationRestartService(ILogger<ApplicationRestartService> logger)
    {
        _logger = logger;
    }

    public Task RequestRestartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string scriptPath = ResolveScriptPath();
        ProcessStartInfo startInfo = new()
        {
            FileName = OperatingSystem.IsWindows() ? "bash" : "/bin/bash",
            WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(scriptPath);

        Process? process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException($"Failed to start restart script: {scriptPath}");
        }

        _logger.LogInformation("Application restart requested via script {ScriptPath}.", scriptPath);
        return Task.CompletedTask;
    }

    internal static string ResolveScriptPath()
    {
        string[] candidates =
        [
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "restart.sh")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "restart.sh")),
            Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "restart.sh")),
            Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "src", "Incursa.Codex.Telegram", "restart.sh")),
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            "restart.sh was not found. Expected it next to the app, in the project output root, or at src/Incursa.Codex.Telegram/restart.sh.");
    }
}
