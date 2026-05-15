using System.Diagnostics;
using Incursa.Codex.Telegram.Services;

namespace Incursa.Codex.Telegram.Tests;

public sealed class DevUtilityServiceTests
{
    [Fact]
    public void BuildBackgroundProcessStartInfo_ForNpmRunDev_ConstructsExpectedShellInvocation()
    {
        ProcessStartInfo startInfo = DevUtilityService.BuildBackgroundProcessStartInfo(
            "/home/wakidu/projects/salonup",
            "npm run dev",
            "/home/wakidu/projects/salonup/.dev/dev.log");

        Assert.Equal("/home/wakidu/projects/salonup", startInfo.WorkingDirectory);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("cmd.exe", startInfo.FileName);
            Assert.Contains("npm run dev", startInfo.Arguments);
            Assert.Contains(".dev/dev.log", startInfo.Arguments);
            return;
        }

        Assert.Equal("/bin/bash", startInfo.FileName);
        Assert.Equal(["-lc", "exec npm run dev >> '/home/wakidu/projects/salonup/.dev/dev.log' 2>&1"], startInfo.ArgumentList);
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
}
