namespace Incursa.Codex.Telegram.Telegram;

internal sealed record TelegramPostTurnActionMenu(
    string Text,
    IReadOnlyList<IReadOnlyList<TelegramReplyButton>> Buttons);

internal sealed record TelegramPostTurnActionMenuContext(
    string? WorkingDirectory,
    bool HasGitRepository,
    bool HasGitChanges,
    bool CanPush,
    bool IsDevServerRunning);

internal static class TelegramPostTurnActionMenuFactory
{
    public static TelegramPostTurnActionMenu BuildCompletedTurnMenu(TelegramPostTurnActionMenuContext context)
    {
        List<TelegramReplyButton> buttons = [];

        if (context.HasGitRepository)
        {
            buttons.Add(new TelegramReplyButton("🌿 Git", "nav:git"));
            if (context.HasGitChanges)
            {
                buttons.Add(new TelegramReplyButton("📄 Diff", "git:diff"));
                buttons.Add(new TelegramReplyButton("✅ Commit", "git:commit"));
            }

            if (context.CanPush)
            {
                buttons.Add(new TelegramReplyButton("⬆️ Push", "git:push"));
            }
        }

        if (context.IsDevServerRunning)
        {
            buttons.Add(new TelegramReplyButton("🚀 Next App Server", "nav:dev"));
            buttons.Add(new TelegramReplyButton("🌐 Tailscale", "nav:tailscale"));
            buttons.Add(new TelegramReplyButton("📜 Logs", "dev:logs"));
        }

        buttons.Add(new TelegramReplyButton("📋 Menu", "nav:menu"));
        return new TelegramPostTurnActionMenu("✅ Finished", ToRows(buttons));
    }

    private static IReadOnlyList<IReadOnlyList<TelegramReplyButton>> ToRows(IReadOnlyList<TelegramReplyButton> buttons)
    {
        List<IReadOnlyList<TelegramReplyButton>> rows = [];
        foreach (TelegramReplyButton button in buttons)
        {
            if (rows.Count > 0 && rows[^1].Count < 2)
            {
                List<TelegramReplyButton> updated = rows[^1].ToList();
                updated.Add(button);
                rows[^1] = updated;
            }
            else
            {
                rows.Add([button]);
            }
        }

        return rows;
    }
}
