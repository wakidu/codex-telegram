using Microsoft.Extensions.Configuration;

namespace Incursa.Codex.Telegram.Options;

/// <summary>
/// Default values for local Codex workspace browsing and persistence.
/// </summary>
public static class CodexWorkspaceDefaults
{
    /// <summary>
    /// Default maximum file count retained in one thread manifest.
    /// </summary>
    public const int MaxFilesPerThread = 200;

    /// <summary>
    /// Default maximum workspace entries shown while browsing local folders.
    /// </summary>
    public const int MaxWorkspaceEntries = 200;

    /// <summary>
    /// Default directory traversal depth while browsing workspace roots.
    /// </summary>
    public const int WorkspaceSearchDepth = 3;
}

/// <summary>
/// Root configuration for the local Codex host integration.
/// </summary>
public sealed class CodexTelegramOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether the Codex runtime should be initialized during startup.
    /// </summary>
    public bool InitializeOnStart { get; set; } = true;

    /// <summary>
    /// Gets or sets the default Codex session context applied to new sessions.
    /// </summary>
    public CodexContextOptions Context { get; set; } = new();

    /// <summary>
    /// Gets or sets local workspace and state-storage options.
    /// </summary>
    public CodexWorkspaceOptions Workspace { get; set; } = new();

    /// <summary>
    /// Gets or sets local development utility options exposed through Telegram.
    /// </summary>
    public DevUtilityOptions DevUtilities { get; set; } = new();

    /// <summary>
    /// Gets or sets Tailscale Serve utility options exposed through Telegram.
    /// </summary>
    public TailscaleUtilityOptions Tailscale { get; set; } = new();
}

/// <summary>
/// Default context values used when creating or continuing Codex sessions.
/// </summary>
public sealed class CodexContextOptions
{
    /// <summary>
    /// Gets or sets the default working directory for new Codex sessions.
    /// </summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Gets or sets base instructions passed to Codex for new sessions.
    /// </summary>
    public string? BaseInstructions { get; set; }

    /// <summary>
    /// Gets or sets developer instructions passed to Codex for new sessions.
    /// </summary>
    public string? DeveloperInstructions { get; set; }

    /// <summary>
    /// Gets or sets the preferred Codex model ID.
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// Gets or sets the preferred model provider, when Codex exposes provider selection.
    /// </summary>
    public string? ModelProvider { get; set; }

    /// <summary>
    /// Gets or sets the optional Codex personality preset.
    /// </summary>
    public string? Personality { get; set; }

    /// <summary>
    /// Gets or sets the Codex sandbox mode override.
    /// </summary>
    public string? Sandbox { get; set; }

    /// <summary>
    /// Gets or sets the Codex service tier override.
    /// </summary>
    public string? ServiceTier { get; set; }

    /// <summary>
    /// Gets or sets the Codex approval mode override.
    /// </summary>
    public string? ApprovalMode { get; set; }

    /// <summary>
    /// Gets or sets the Codex approvals reviewer override.
    /// </summary>
    public string? ApprovalsReviewer { get; set; }

    /// <summary>
    /// Gets or sets the requested Codex reasoning effort.
    /// </summary>
    public string? ReasoningEffort { get; set; }

    /// <summary>
    /// Gets or sets the requested Codex reasoning summary mode.
    /// </summary>
    public string? ReasoningSummary { get; set; }

    /// <summary>
    /// Gets or sets the requested Codex web-search mode.
    /// </summary>
    public string? WebSearchMode { get; set; }

    /// <summary>
    /// Gets or sets whether Codex network access should be enabled.
    /// </summary>
    public bool? NetworkAccessEnabled { get; set; }

    /// <summary>
    /// Gets or sets whether Codex web search should be enabled.
    /// </summary>
    public bool? WebSearchEnabled { get; set; }

    /// <summary>
    /// Gets or sets whether Codex should skip the Git repository check.
    /// </summary>
    public bool? SkipGitRepoCheck { get; set; }

    /// <summary>
    /// Gets or sets whether new Codex sessions should be ephemeral.
    /// </summary>
    public bool? Ephemeral { get; set; }

    /// <summary>
    /// Gets additional local directories exposed to Codex for new sessions.
    /// </summary>
    public List<string> AdditionalDirectories { get; set; } = [];
}

/// <summary>
/// Configuration for local state storage and workspace allowlisting.
/// </summary>
public sealed class CodexWorkspaceOptions
{
    /// <summary>
    /// Gets or sets the local data root for projects, Telegram state, and thread manifests.
    /// </summary>
    public string? DataRoot { get; set; }

    /// <summary>
    /// Gets the local directory roots that Telegram users may add as projects.
    /// </summary>
    public List<string> WorkspaceRoots { get; set; } = [];

    /// <summary>
    /// Gets or sets the maximum file count retained in one thread manifest.
    /// </summary>
    public int MaxFilesPerThread { get; set; } = CodexWorkspaceDefaults.MaxFilesPerThread;

    /// <summary>
    /// Gets or sets the maximum workspace entry count returned while browsing local folders.
    /// </summary>
    public int MaxWorkspaceEntries { get; set; } = CodexWorkspaceDefaults.MaxWorkspaceEntries;

    /// <summary>
    /// Gets or sets the directory traversal depth used while discovering workspace entries.
    /// </summary>
    public int WorkspaceSearchDepth { get; set; } = CodexWorkspaceDefaults.WorkspaceSearchDepth;
}

/// <summary>
/// Configuration for local development utility commands.
/// </summary>
public sealed class DevUtilityOptions
{
    /// <summary>
    /// Gets or sets the default dev command used when a project does not override it.
    /// </summary>
    public string DefaultCommand { get; set; } = "npm run dev -- --hostname 0.0.0.0";

    /// <summary>
    /// Gets or sets the default relative log file path used when a project does not override it.
    /// </summary>
    public string DefaultLogFile { get; set; } = ".dev/dev.log";

    /// <summary>
    /// Gets or sets the default tail line count returned for log requests.
    /// </summary>
    public int LogTailLineCount { get; set; } = 100;

    /// <summary>
    /// Gets or sets the configured project-specific dev utility options.
    /// </summary>
    public Dictionary<string, DevUtilityProjectOptions> Projects { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Project-specific development utility configuration.
/// </summary>
public sealed class DevUtilityProjectOptions
{
    /// <summary>
    /// Gets or sets the working directory used to match this project configuration.
    /// </summary>
    public string? Cwd { get; set; }

    /// <summary>
    /// Gets or sets the dev command override for the project.
    /// </summary>
    public string? DevCommand { get; set; }

    /// <summary>
    /// Gets or sets the default local preview port for the project.
    /// </summary>
    public int? Port { get; set; }

    /// <summary>
    /// Gets or sets the preview command override for the project.
    /// </summary>
    public string? PreviewCommand { get; set; }

    /// <summary>
    /// Gets or sets the project log file path override.
    /// </summary>
    public string? LogFile { get; set; }
}

/// <summary>
/// Configuration for Tailscale Serve utility controls.
/// </summary>
public sealed class TailscaleUtilityOptions
{
    /// <summary>
    /// Gets or sets the Tailscale executable path.
    /// </summary>
    public string ExecutablePath { get; set; } = "tailscale";

    /// <summary>
    /// Gets or sets the simple configured ports list.
    /// </summary>
    public List<int> Ports { get; set; } = [];

    /// <summary>
    /// Gets or sets the richer configured entries list.
    /// </summary>
    public List<TailscaleUtilityEntryOptions> Entries { get; set; } = [];

    /// <summary>
    /// Gets or sets the maximum number of seconds to wait for each Tailscale CLI call.
    /// </summary>
    public int CommandTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Gets or sets the resolved local settings file path used for diagnostics.
    /// </summary>
    public string? ConfigurationSourcePath { get; set; }
}

/// <summary>
/// Named Tailscale Serve entry configuration.
/// </summary>
public sealed class TailscaleUtilityEntryOptions
{
    /// <summary>
    /// Gets or sets the display name for the entry.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the local port served through Tailscale.
    /// </summary>
    public int Port { get; set; }
}

internal static class CodexTelegramOptionMigrations
{
    public static void ApplyRootLevelTailscaleFallback(CodexTelegramOptions options, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);

        IConfigurationSection section = configuration.GetSection("Tailscale");
        if (!section.Exists())
        {
            return;
        }

        TailscaleUtilityOptions fallback = new();
        section.Bind(fallback);

        if (string.IsNullOrWhiteSpace(options.Tailscale.ExecutablePath) && !string.IsNullOrWhiteSpace(fallback.ExecutablePath))
        {
            options.Tailscale.ExecutablePath = fallback.ExecutablePath;
        }

        if (options.Tailscale.Ports.Count == 0 && fallback.Ports.Count > 0)
        {
            options.Tailscale.Ports = fallback.Ports.ToList();
        }

        if (options.Tailscale.Entries.Count == 0 && fallback.Entries.Count > 0)
        {
            options.Tailscale.Entries = fallback.Entries
                .Select(entry => new TailscaleUtilityEntryOptions
                {
                    Name = entry.Name,
                    Port = entry.Port,
                })
                .ToList();
        }
    }
}
