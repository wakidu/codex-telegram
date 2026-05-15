using System.Text.Json;
using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Services;

internal sealed record DevProcessRecord(
    string WorkingDirectory,
    int ProcessId,
    string Command,
    string LogPath,
    int? Port,
    string? PreviewCommand,
    DateTimeOffset StartedAtUtc);

internal sealed class DevUtilityStateStore
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly IOptions<CodexTelegramOptions> _options;

    public DevUtilityStateStore(IOptions<CodexTelegramOptions> options)
    {
        _options = options;
    }

    public async Task<IReadOnlyList<DevProcessRecord>> ListAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await LoadCoreAsync(cancellationToken).ConfigureAwait(false)).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DevProcessRecord?> GetAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        string normalized = Path.GetFullPath(workingDirectory);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<DevProcessRecord> records = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            return records.FirstOrDefault(record => PathComparer.Equals(record.WorkingDirectory, normalized));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertAsync(DevProcessRecord record, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<DevProcessRecord> records = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            records.RemoveAll(existing => PathComparer.Equals(existing.WorkingDirectory, record.WorkingDirectory));
            records.Add(record);
            await SaveCoreAsync(records, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RemoveAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        string normalized = Path.GetFullPath(workingDirectory);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<DevProcessRecord> records = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            int removed = records.RemoveAll(record => PathComparer.Equals(record.WorkingDirectory, normalized));
            if (removed > 0)
            {
                await SaveCoreAsync(records, cancellationToken).ConfigureAwait(false);
                return true;
            }

            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<DevProcessRecord>> LoadCoreAsync(CancellationToken cancellationToken)
    {
        string path = GetPath();
        if (!File.Exists(path))
        {
            return [];
        }

        await using FileStream stream = File.OpenRead(path);
        List<DevProcessRecord>? records = await JsonSerializer.DeserializeAsync<List<DevProcessRecord>>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
        return records ?? [];
    }

    private async Task SaveCoreAsync(IReadOnlyList<DevProcessRecord> records, CancellationToken cancellationToken)
    {
        string path = GetPath();
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = Path.Combine(directory ?? Path.GetTempPath(), $"{Guid.NewGuid():N}.json.tmp");
        await using (FileStream stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, records, _jsonOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, path, overwrite: true);
    }

    private string GetPath()
    {
        string? configuredRoot = _options.Value.Workspace.DataRoot;
        string dataRoot = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(AppContext.BaseDirectory, "App_Data", "codex-telegram")
            : Path.GetFullPath(configuredRoot);
        return Path.Combine(dataRoot, "dev-processes.json");
    }
}
