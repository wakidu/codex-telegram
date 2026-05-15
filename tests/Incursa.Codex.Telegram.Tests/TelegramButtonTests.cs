using Incursa.Codex.Telegram.Services;
using Incursa.Codex.Telegram.Telegram;

namespace Incursa.Codex.Telegram.Tests;

public sealed class TelegramButtonTests
{
    [Fact]
    public void BuildSessionButtons_UsesPlainUseLabelForSingleSession()
    {
        CodexSessionSummary session = CreateSession("thread-1", "Session 1");

        IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? rows = TelegramCodexBotCommandHandler.BuildSessionButtons([session]);

        Assert.NotNull(rows);
        TelegramReplyButton button = Assert.Single(Assert.Single(rows));
        Assert.Equal("Use", button.Text);
        Assert.Equal("use:thread-1", button.CallbackData);
    }

    [Fact]
    public void BuildSessionButtons_UsesOrdinalsOnlyForMultipleSessions()
    {
        CodexSessionSummary first = CreateSession("thread-1", "Session 1");
        CodexSessionSummary second = CreateSession("thread-2", "Session 2");

        IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? rows = TelegramCodexBotCommandHandler.BuildSessionButtons([first, second]);

        Assert.NotNull(rows);
        Assert.Equal(["Use 1", "Use 2"], rows.SelectMany(row => row.Select(button => button.Text)).ToArray());
    }

    [Fact]
    public void BuildSessionButtons_ReturnsNoButtonsWhenUseIsNotRelevant()
    {
        CodexSessionSummary session = CreateSession("thread-1", "Session 1");

        IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? rows = TelegramCodexBotCommandHandler.BuildSessionButtons([session], includeUse: false);

        Assert.Null(rows);
    }

    [Fact]
    public void BuildNavigationButtons_DoesNotAdvertiseTopicManagementGlobally()
    {
        IReadOnlyList<IReadOnlyList<TelegramReplyButton>> rows = TelegramCodexBotCommandHandler.BuildNavigationButtons();

        IReadOnlyList<string> labels = rows.SelectMany(row => row.Select(button => button.Text)).ToArray();

        Assert.Equal(["Codex Sessions", "Projects", "Next App Server", "Tailscale", "🛑 Stop AI", "Help"], labels);
    }

    [Fact]
    public void BuildHelpMenuButtons_ShowsCompactHelpCategories()
    {
        IReadOnlyList<IReadOnlyList<TelegramReplyButton>> rows = TelegramCodexBotCommandHandler.BuildHelpMenuButtons();

        IReadOnlyList<string> labels = rows.SelectMany(row => row.Select(button => button.Text)).ToArray();

        Assert.Equal(["Codex Sessions", "Projects", "Next App Server", "Tailscale", "Codex", "Admin/Debug", "Full Command Reference", "Back"], labels);
    }

    private static CodexSessionSummary CreateSession(string id, string name)
        => new(
            id,
            name,
            CodexSessionStatus.Exited,
            @"C:\src\repo",
            DateTimeOffset.Parse("2026-05-04T00:00:00Z"),
            DateTimeOffset.Parse("2026-05-04T00:00:00Z"),
            null,
            null);
}
