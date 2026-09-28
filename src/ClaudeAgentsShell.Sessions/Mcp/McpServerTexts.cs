namespace ClaudeAgentsShell.Sessions.Mcp;

/// <summary>
/// Тексты для агента в одном месте: подсказка сервера и описания инструментов. Подсказка
/// (<c>instructions</c> в ответе <c>initialize</c>) Claude Code вставляет в системный промпт
/// всегда, а схемы инструментов MCP у него отложены (ToolSearch): без подсказки агент видит
/// только имя инструмента и не догадывается его вызвать.
/// </summary>
internal static class McpServerTexts
{
    /// <summary>Подсказка сервера: где идёт сессия, когда вызывать инструменты, как загрузить схему.</summary>
    public const string Instructions =
        "This Claude Code session runs inside Agents Shell, a desktop app that hosts it in a terminal tab "
        + "with a viewer panel right next to the terminal. Prefer the panel over printing code into the terminal:\n"
        + "- " + McpProtocol.ShowDiffPermissionRule + ": call it when you finish a piece of work or when the user asks "
        + "to see or review the changes, instead of printing a diff in the terminal.\n"
        + "- " + McpProtocol.ShowFilePermissionRule + ": call it when the user asks to show or open a file, or when you "
        + "want to point the user at specific lines, instead of printing the file in the terminal.\n"
        + "Both are pre-approved and do not wait for the user to read the result. If their schemas are not loaded yet, load them first with "
        + "ToolSearch: select:" + McpProtocol.ShowDiffPermissionRule + "," + McpProtocol.ShowFilePermissionRule;

    /// <summary>Описание <c>show_diff</c>.</summary>
    public const string ShowDiffDescription =
        "Show the user a diff of your changes in the Agents Shell app's diff panel, right next to this terminal. "
        + "Call it when you want the user to review what you changed: after finishing a piece of work, "
        + "or when the user asks to see the diff. Prefer it to printing a diff in the terminal. "
        + "By default it shows everything the current branch changed "
        + "against its base branch: commits plus uncommitted and untracked files. Use `files` to point the user "
        + "at specific files and `note` to say what they are looking at. Returns immediately; "
        + "it does not wait for the user to read the diff.";

    /// <summary>Описание <c>show_file</c>.</summary>
    public const string ShowFileDescription =
        "Show the user one or more files in the Agents Shell app's panel, right next to this terminal, "
        + "with syntax highlighting. Call it when the user asks to show or open a file, or when you want to point "
        + "the user at specific lines (where a bug is, what to look at); prefer it to printing file contents "
        + "in the terminal. Give `start_line`/`end_line` for a file and the panel scrolls to those lines and "
        + "highlights them. Paths are relative to `path` or absolute local paths; files must be inside the repository "
        + "containing `path` (or inside `path` itself, outside a repository). Use `note` to say what the user is looking at. Returns immediately; "
        + "it does not wait for the user.";
}
