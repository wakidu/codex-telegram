using Incursa.Codex.Telegram.Services;
using Incursa.Codex.Telegram.Telegram;

namespace Incursa.Codex.Telegram.Tests;

public sealed class TelegramButtonTests
{
    [Fact]
    public void BuildSessionButtons_UsesPlainUseLabelForSingleSession()
    {
        CodexSessionSummary session = CreateSession("thread-1", "Session 1", "/workspace/repo-one");

        IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? rows = TelegramCodexBotCommandHandler.BuildSessionButtons([session]);

        Assert.NotNull(rows);
        IReadOnlyList<TelegramReplyButton> buttons = Assert.Single(rows);
        Assert.Collection(
            buttons,
            button =>
            {
                Assert.Equal("repo-one", button.Text);
                Assert.Equal("use:thread-1", button.CallbackData);
            },
            button =>
            {
                Assert.Equal("⛔ Stop", button.Text);
                Assert.Equal("stop:thread-1", button.CallbackData);
            },
            button =>
            {
                Assert.Equal("🗑 Delete", button.Text);
                Assert.Equal("delete:thread-1", button.CallbackData);
            });
    }

    [Fact]
    public void BuildSessionButtons_UsesProjectNamesForMultipleSessions()
    {
        CodexSessionSummary first = CreateSession("thread-1", "Session 1", "/workspace/repo-one");
        CodexSessionSummary second = CreateSession("thread-2", "Session 2", "/workspace/repo-two");

        IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? rows = TelegramCodexBotCommandHandler.BuildSessionButtons([first, second]);

        Assert.NotNull(rows);
        Assert.Equal(["repo-one", "⛔ Stop", "🗑 Delete", "repo-two", "⛔ Stop", "🗑 Delete"], rows.SelectMany(row => row.Select(button => button.Text)).ToArray());
    }

    [Fact]
    public void BuildSessionButtons_AddsSuffixWhenProjectNamesRepeat()
    {
        CodexSessionSummary first = CreateSession("thread-1", "Session 1", "/workspace/repo");
        CodexSessionSummary second = CreateSession("thread-2", "Session 2", "/another/repo");

        IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? rows = TelegramCodexBotCommandHandler.BuildSessionButtons([first, second]);

        Assert.NotNull(rows);
        Assert.Equal(["repo 1", "⛔ Stop", "🗑 Delete", "repo 2", "⛔ Stop", "🗑 Delete"], rows.SelectMany(row => row.Select(button => button.Text)).ToArray());
    }

    [Fact]
    public void BuildSessionButtons_ReturnsNoButtonsWhenUseIsNotRelevant()
    {
        CodexSessionSummary session = CreateSession("thread-1", "Session 1", "/workspace/repo-one");

        IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? rows = TelegramCodexBotCommandHandler.BuildSessionButtons([session], includeUse: false);

        Assert.Null(rows);
    }

    [Fact]
    public void BuildNavigationButtons_DoesNotAdvertiseTopicManagementGlobally()
    {
        IReadOnlyList<IReadOnlyList<TelegramReplyButton>> rows = TelegramCodexBotCommandHandler.BuildNavigationButtons();

        IReadOnlyList<string> labels = rows.SelectMany(row => row.Select(button => button.Text)).ToArray();

        Assert.Equal(["🤖 Codex Sessions", "📁 Projects", "🚀 Next App Server", "🌐 Tailscale", "🌿 Git", "❓ Help", "🛑 Stop AI"], labels);
    }

    [Fact]
    public void BuildHelpMenuButtons_ShowsCompactHelpCategories()
    {
        IReadOnlyList<IReadOnlyList<TelegramReplyButton>> rows = TelegramCodexBotCommandHandler.BuildHelpMenuButtons();

        IReadOnlyList<string> labels = rows.SelectMany(row => row.Select(button => button.Text)).ToArray();

        Assert.Equal(["🤖 Codex Sessions", "📁 Projects", "🚀 Next App Server", "🌐 Tailscale", "🤖 Codex", "🛠 Admin/Debug", "📚 Full Command Reference", "⬅ Back"], labels);
    }

    private static CodexSessionSummary CreateSession(string id, string name, string workingDirectory)
        => new(
            id,
            name,
            CodexSessionStatus.Exited,
            workingDirectory,
            DateTimeOffset.Parse("2026-05-04T00:00:00Z"),
            DateTimeOffset.Parse("2026-05-04T00:00:00Z"),
            null,
            null);
}
