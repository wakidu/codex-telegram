using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Configuration;

namespace Incursa.Codex.Telegram.Tests;

public sealed class CodexTelegramOptionMigrationsTests
{
    [Fact]
    public void ApplyRootLevelTailscaleFallback_UsesRootSectionWhenNestedSectionIsEmpty()
    {
        Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Tailscale:ExecutablePath"] = "tailscale",
            ["Tailscale:Ports:0"] = "3000",
            ["Tailscale:Entries:0:Name"] = "SalonUp",
            ["Tailscale:Entries:0:Port"] = "3100",
            ["Tailscale:Entries:1:Name"] = "SalonUp Studio",
            ["Tailscale:Entries:1:Port"] = "3500",
        };

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        CodexTelegramOptions options = new();

        CodexTelegramOptionMigrations.ApplyRootLevelTailscaleFallback(options, configuration);

        Assert.Equal("tailscale", options.Tailscale.ExecutablePath);
        Assert.Equal([3000], options.Tailscale.Ports);
        Assert.Collection(
            options.Tailscale.Entries,
            entry =>
            {
                Assert.Equal("SalonUp", entry.Name);
                Assert.Equal(3100, entry.Port);
            },
            entry =>
            {
                Assert.Equal("SalonUp Studio", entry.Name);
                Assert.Equal(3500, entry.Port);
            });
    }

    [Fact]
    public void ApplyRootLevelTailscaleFallback_DoesNotOverrideNestedSectionValues()
    {
        Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Tailscale:ExecutablePath"] = "tailscale",
            ["Tailscale:Entries:0:Name"] = "Root",
            ["Tailscale:Entries:0:Port"] = "3000",
        };

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        CodexTelegramOptions options = new()
        {
            Tailscale = new TailscaleUtilityOptions
            {
                ExecutablePath = "custom-tailscale",
                Entries =
                [
                    new TailscaleUtilityEntryOptions
                    {
                        Name = "Nested",
                        Port = 4000,
                    },
                ],
            },
        };

        CodexTelegramOptionMigrations.ApplyRootLevelTailscaleFallback(options, configuration);

        Assert.Equal("custom-tailscale", options.Tailscale.ExecutablePath);
        Assert.Collection(
            options.Tailscale.Entries,
            entry =>
            {
                Assert.Equal("Nested", entry.Name);
                Assert.Equal(4000, entry.Port);
            });
    }
}
