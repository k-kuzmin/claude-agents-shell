namespace ClaudeAgentsShell.Terminal.Shells;

/// <summary>
/// Ищет исполняемый файл оболочки: сначала по известным каталогам установки,
/// затем по <c>PATH</c>. Общая часть провайдеров, чтобы каждый не переписывал поиск.
/// </summary>
internal static class ExecutableLocator
{
    /// <summary>Первый существующий путь из списка кандидатов; <c>null</c>, если ничего не найдено.</summary>
    internal static string? FindFirstExisting(IEnumerable<string> candidates)
    {
        foreach (string candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Ищет файл в каталогах из переменной <c>PATH</c>.</summary>
    internal static string? FindInPath(string fileName)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = directory.Trim('"');
            if (trimmed.Length == 0)
            {
                continue;
            }

            string candidate;
            try
            {
                candidate = Path.Combine(trimmed, fileName);
            }
            catch (ArgumentException)
            {
                // В PATH встречается мусор с недопустимыми символами — просто пропускаем запись.
                continue;
            }

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Путь внутри каталога Windows, например <c>System32</c>.</summary>
    internal static string SystemPath(params string[] parts)
    {
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return Path.Combine(new[] { system }.Concat(parts).ToArray());
    }
}
