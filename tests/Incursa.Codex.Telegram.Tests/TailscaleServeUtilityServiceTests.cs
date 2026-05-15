using System.Diagnostics;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Incursa.Codex.Telegram.Tests;

public sealed class TailscaleServeUtilityServiceTests
{
    [Fact]
    public void ParseActivePorts_FindsConfiguredLocalhostBackends()
    {
        string status = """
            Available within your tailnet:
            |-- https://node.tailnet.ts.net:3000
            |--> http://localhost:3000
            |-- https://node.tailnet.ts.net:4000
            |--> http://127.0.0.1:4000
            """;

        IReadOnlySet<int> ports = TailscaleServeUtilityService.ParseActivePorts(status);

        Assert.Equal([3000, 4000], ports.OrderBy(port => port).ToArray());
    }

    [Fact]
    public async Task TogglePortAsync_EnablesConfiguredPortWithSpecificServeCommand()
    {
        FakeTailscaleExecutor executor = new();
        executor.Results.Enqueue(new TailscaleCommandResult(0, string.Empty, string.Empty)); // serve status
        executor.Results.Enqueue(new TailscaleCommandResult(0, string.Empty, string.Empty)); // toggle
        executor.Results.Enqueue(new TailscaleCommandResult(0, """{"Self":{"DNSName":"node.tailnet.ts.net."}}""", string.Empty)); // status

        TailscaleServeUtilityService service = CreateService(executor);

        TailscaleServeToggleResult result = await service.TogglePortAsync(3000, CancellationToken.None);

        Assert.True(result.Enabled);
        ProcessStartInfo startInfo = executor.Calls[1];
        Assert.Equal("tailscale", startInfo.FileName);
        Assert.Equal(["serve", "--bg", "http://localhost:3000"], startInfo.ArgumentList);
    }

    [Fact]
    public async Task TogglePortAsync_DisablesOnlySelectedPortAndDoesNotResetOthers()
    {
        FakeTailscaleExecutor executor = new();
        executor.Results.Enqueue(new TailscaleCommandResult(0, "http://localhost:3000\nhttp://localhost:3500", string.Empty)); // serve status
        executor.Results.Enqueue(new TailscaleCommandResult(0, string.Empty, string.Empty)); // toggle off
        executor.Results.Enqueue(new TailscaleCommandResult(0, """{"Self":{"DNSName":"node.tailnet.ts.net."}}""", string.Empty)); // status

        TailscaleServeUtilityService service = CreateService(executor);

        TailscaleServeToggleResult result = await service.TogglePortAsync(3000, CancellationToken.None);

        Assert.False(result.Enabled);
        ProcessStartInfo startInfo = executor.Calls[1];
        Assert.Equal(["serve", "--https=3000", "http://localhost:3000", "off"], startInfo.ArgumentList);
        Assert.DoesNotContain("reset", startInfo.ArgumentList);
    }

    [Fact]
    public async Task GetMenuStateAsync_PreservesUnrelatedConfiguredEntriesWhenOnePortIsActive()
    {
        FakeTailscaleExecutor executor = new();
        executor.Results.Enqueue(new TailscaleCommandResult(0, """{"Self":{"DNSName":"node.tailnet.ts.net."}}""", string.Empty));
        executor.Results.Enqueue(new TailscaleCommandResult(0, "http://localhost:3000", string.Empty));

        TailscaleServeUtilityService service = CreateService(
            executor,
            new TailscaleUtilityOptions
            {
                ExecutablePath = "tailscale",
                Entries =
                [
                    new TailscaleUtilityEntryOptions { Name = "Salonup", Port = 3000 },
                    new TailscaleUtilityEntryOptions { Name = "Admin", Port = 3500 },
                ],
            });

        TailscaleServeMenuState state = await service.GetMenuStateAsync(CancellationToken.None);

        Assert.Collection(
            state.Entries.OrderBy(entry => entry.Port),
            entry =>
            {
                Assert.Equal(3000, entry.Port);
                Assert.True(entry.Enabled);
            },
            entry =>
            {
                Assert.Equal(3500, entry.Port);
                Assert.False(entry.Enabled);
            });
    }

    [Fact]
    public async Task TogglePortAsync_RejectsInvalidCustomPort()
    {
        TailscaleServeUtilityService service = CreateService(new FakeTailscaleExecutor());

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.TogglePortAsync(70000, CancellationToken.None));

        Assert.Contains("between 1 and 65535", exception.Message);
    }

    [Fact]
    public async Task ResetAsync_UsesTailscaleServeReset()
    {
        FakeTailscaleExecutor executor = new();
        executor.Results.Enqueue(new TailscaleCommandResult(0, string.Empty, string.Empty));

        TailscaleServeUtilityService service = CreateService(executor);

        TailscaleServeResetResult result = await service.ResetAsync(CancellationToken.None);

        Assert.Equal("Reset all Tailscale Serve routes.", result.Message);
        ProcessStartInfo startInfo = Assert.Single(executor.Calls);
        Assert.Equal(["serve", "reset"], startInfo.ArgumentList);
    }

    [Fact]
    public async Task TogglePortAsync_FailureIncludesCommandDiagnostics()
    {
        FakeTailscaleExecutor executor = new();
        executor.Results.Enqueue(new TailscaleCommandResult(0, string.Empty, string.Empty));
        executor.Results.Enqueue(new TailscaleCommandResult(1, "status output", "permission denied"));

        TailscaleServeUtilityService service = CreateService(executor);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.TogglePortAsync(3000, CancellationToken.None));

        Assert.Contains("Executable: tailscale", exception.Message);
        Assert.Contains("Command: tailscale serve --bg http://localhost:3000", exception.Message);
        Assert.Contains("Exit code: 1", exception.Message);
        Assert.Contains("Stdout: status output", exception.Message);
        Assert.Contains("Stderr: permission denied", exception.Message);
    }

    [Fact]
    public async Task TogglePortAsync_OperatorHintShowsFriendlySetupMessage()
    {
        FakeTailscaleExecutor executor = new();
        executor.Results.Enqueue(new TailscaleCommandResult(0, string.Empty, string.Empty));
        executor.Results.Enqueue(new TailscaleCommandResult(1, string.Empty, "run sudo tailscale set --operator=$USER first"));

        TailscaleServeUtilityService service = CreateService(executor);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.TogglePortAsync(3000, CancellationToken.None));

        Assert.Contains("Tailscale needs operator access", exception.Message);
        Assert.Contains("sudo tailscale set --operator=$USER", exception.Message);
        Assert.Contains("Command: tailscale serve --bg http://localhost:3000", exception.Message);
    }

    [Fact]
    public async Task CommandExecutor_TimesOutLongRunningCommand()
    {
        TailscaleServeCommandExecutor executor = new();
        ProcessStartInfo startInfo = new()
        {
            FileName = "/bin/bash",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-lc");
        startInfo.ArgumentList.Add("sleep 2");

        TailscaleCommandResult result = await executor.ExecuteAsync(startInfo, TimeSpan.FromMilliseconds(200), CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
    }

    private static TailscaleServeUtilityService CreateService(FakeTailscaleExecutor executor, TailscaleUtilityOptions? options = null)
        => new(
            options ?? new TailscaleUtilityOptions
            {
                ExecutablePath = "tailscale",
                Entries = [new TailscaleUtilityEntryOptions { Name = "Salonup", Port = 3000 }],
            },
            executor,
            NullLogger<TailscaleServeUtilityService>.Instance);

    private sealed class FakeTailscaleExecutor : ITailscaleServeCommandExecutor
    {
        public List<ProcessStartInfo> Calls { get; } = [];

        public Queue<TailscaleCommandResult> Results { get; } = [];

        public Task<TailscaleCommandResult> ExecuteAsync(ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls.Add(startInfo);
            return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : new TailscaleCommandResult(0, string.Empty, string.Empty));
        }
    }
}
