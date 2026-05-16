using System.Diagnostics;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;

namespace Incursa.Codex.Telegram.Tests;

public sealed class DevUtilityServiceTests
{
    [Fact]
    public void BuildBackgroundProcessStartInfo_ForDefaultNpmRunDev_ConstructsExpectedShellInvocation()
    {
        ProcessStartInfo startInfo = DevUtilityService.BuildBackgroundProcessStartInfo(
            "/home/wakidu/projects/salonup",
            "npm run dev -- --hostname 0.0.0.0",
            "/home/wakidu/projects/salonup/.dev/dev.log");

        Assert.Equal("/home/wakidu/projects/salonup", startInfo.WorkingDirectory);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("cmd.exe", startInfo.FileName);
            Assert.Contains("npm run dev -- --hostname 0.0.0.0", startInfo.Arguments);
            Assert.Contains(".dev/dev.log", startInfo.Arguments);
            return;
        }

        Assert.Equal("/bin/bash", startInfo.FileName);
        Assert.Equal(["-lc", "exec npm run dev -- --hostname 0.0.0.0 >> '/home/wakidu/projects/salonup/.dev/dev.log' 2>&1"], startInfo.ArgumentList);
    }

    [Fact]
    public void BuildBackgroundProcessStartInfo_ForNpmRunBuild_ConstructsExpectedShellInvocation()
    {
        ProcessStartInfo startInfo = DevUtilityService.BuildBackgroundProcessStartInfo(
            "/home/wakidu/projects/webkell",
            "npm run build",
            "/home/wakidu/projects/webkell/.dev/build.log");

        Assert.Equal("/home/wakidu/projects/webkell", startInfo.WorkingDirectory);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("cmd.exe", startInfo.FileName);
            Assert.Contains("npm run build", startInfo.Arguments);
            Assert.Contains(".dev/build.log", startInfo.Arguments);
            return;
        }

        Assert.Equal("/bin/bash", startInfo.FileName);
        Assert.Equal(["-lc", "exec npm run build >> '/home/wakidu/projects/webkell/.dev/build.log' 2>&1"], startInfo.ArgumentList);
    }

    [Fact]
    public void DefaultDevCommand_UsesHostnameBindingWithoutPortOverride()
    {
        DevUtilityOptions options = new();

        Assert.Equal("npm run dev -- --hostname 0.0.0.0", options.DefaultCommand);
        Assert.DoesNotContain("--port", options.DefaultCommand, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseDetectedPort_FindsNextJsLocalUrlPort()
    {
        int? port = DevUtilityService.ParseDetectedPort("""
            ▲ Next.js 15.0.0
            - Local:        http://localhost:3500
            - Network:      http://0.0.0.0:3500
            """);

        Assert.Equal(3500, port);
    }

    [Fact]
    public void ParseDetectedPort_FindsReadyServerPort()
    {
        int? port = DevUtilityService.ParseDetectedPort("""
            ready - started server on 0.0.0.0:3500, url: http://localhost:3500
            """);

        Assert.Equal(3500, port);
    }

    [Fact]
    public void ParseSsListeners_FindsPidFromSudoSsOutput()
    {
        IReadOnlyList<DevUtilityService.ListeningProcessInfo> listeners = DevUtilityService.ParseSsListeners("""
            LISTEN 0 511 0.0.0.0:3500 0.0.0.0:* users:(("next-server (v15.5.15)",pid=119359,fd=22))
            """, 3500);

        DevUtilityService.ListeningProcessInfo listener = Assert.Single(listeners);
        Assert.Equal(119359, listener.ProcessId);
        Assert.Equal(3500, listener.Port);
    }

    [Fact]
    public void GetKillDevPorts_ReturnsExpectedFixedPorts()
    {
        Assert.Equal([3000, 3001, 3002, 3003, 3004, 3005, 3100, 3300, 3500, 4000], DevUtilityService.GetKillDevPorts());
    }

    [Fact]
    public void ResolveKillDevPortsScriptPath_PointsToProjectScript()
    {
        string path = DevUtilityService.ResolveKillDevPortsScriptPath();

        Assert.EndsWith(Path.Combine("src", "Incursa.Codex.Telegram", "kill-dev-ports.sh"), path, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildKillDevPortsReport_ListsClearedAndAlreadyFreePorts()
    {
        string report = DevUtilityService.BuildKillDevPortsReport(
            [3000, 3001, 3300, 3500, 4000],
            new Dictionary<int, IReadOnlyList<int>>
            {
                [3000] = [1001],
                [3500] = [2001, 2002],
            },
            new Dictionary<int, IReadOnlyList<int>>
            {
                [3000] = [1001],
                [3500] = [2001, 2002],
            },
            [3001, 4000]);

        Assert.Contains("🧹 Kill Dev Ports", report);
        Assert.Contains("Checked ports: 3000, 3001, 3300, 3500, 4000", report);
        Assert.Contains("🔎 Detected PIDs:", report);
        Assert.Contains("* 3000: 1001", report);
        Assert.Contains("* 3500: 2001, 2002", report);
        Assert.Contains("✅ Cleared:", report);
        Assert.Contains("* 3500 (killed: 2001, 2002)", report);
        Assert.Contains("* 3000 (killed: 1001)", report);
        Assert.Contains("ℹ Already free:", report);
        Assert.Contains("* 3001", report);
        Assert.Contains("* 4000", report);
    }

    [Fact]
    public void BuildKillDevPortsReport_IncludesStillBusyPorts()
    {
        string report = DevUtilityService.BuildKillDevPortsReport(
            [3000, 3001],
            new Dictionary<int, IReadOnlyList<int>>
            {
                [3000] = [1234],
            },
            new Dictionary<int, IReadOnlyList<int>>(),
            [3001],
            new Dictionary<int, string>
            {
                [3000] = "permission denied",
            });

        Assert.Contains("⚠ Still busy:", report);
        Assert.Contains("* 3000 (permission denied)", report);
        Assert.Contains("* 3001", report);
    }

    [Fact]
    public void BuildKillDevPortsReport_IncludesPasswordlessSudoGuidance()
    {
        string report = DevUtilityService.BuildKillDevPortsReport(
            [3000, 3500],
            new Dictionary<int, IReadOnlyList<int>>
            {
                [3500] = [9876],
            },
            new Dictionary<int, IReadOnlyList<int>>
            {
                [3500] = [9876],
            },
            [],
            new Dictionary<int, string> { [3000] = "sudo: a password is required" },
            requiresPasswordlessSudo: true);

        Assert.Contains("⚠ Requires passwordless sudo for full functionality.", report);
        Assert.Contains("sudo visudo", report);
        Assert.Contains("wakidu ALL=(ALL) NOPASSWD: /usr/bin/ss, /bin/kill, /usr/bin/kill", report);
    }

    [Theory]
    [InlineData("sudo: a password is required")]
    [InlineData("password is required")]
    [InlineData("sudo: password")]
    public void RequiresPasswordlessSudo_DetectsExpectedMessages(string text)
    {
        Assert.True(DevUtilityService.RequiresPasswordlessSudo(text));
    }
}
