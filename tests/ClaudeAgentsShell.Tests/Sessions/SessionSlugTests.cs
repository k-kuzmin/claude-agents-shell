using ClaudeAgentsShell.Sessions.History;
using Xunit;

namespace ClaudeAgentsShell.Tests.Sessions;

public sealed class SessionSlugTests
{
    [Theory]
    // Живой каталог с машины пользователя: так Claude Code назвал каталог этого проекта.
    [InlineData(@"D:\Portfolio\Projects\claude-agents-shell", "D--Portfolio-Projects-claude-agents-shell")]
    [InlineData(@"D:\src\domovoy", "D--src-domovoy")]
    [InlineData(@"C:\Users\ivan\AppData\Roaming", "C--Users-ivan-AppData-Roaming")]
    public void Диск_и_разделители_становятся_дефисами(string path, string expected)
    {
        Assert.Equal(expected, SessionSlug.From(path));
    }

    [Fact]
    public void Хвостовой_разделитель_не_меняет_каталог()
    {
        Assert.Equal(
            SessionSlug.From(@"D:\src\domovoy"),
            SessionSlug.From(@"D:\src\domovoy\"));
    }

    [Fact]
    public void Прямые_слэши_дают_тот_же_каталог_что_и_обратные()
    {
        Assert.Equal(
            SessionSlug.From(@"D:\src\domovoy"),
            SessionSlug.From("D:/src/domovoy"));
    }

    [Fact]
    public void Пробелы_и_точки_тоже_дефисы()
    {
        Assert.Equal("D--src-my-project-v1-2", SessionSlug.From(@"D:\src\my project.v1.2"));
    }

    [Fact]
    public void Кириллица_заменяется_дефисами_как_и_всё_не_ASCII()
    {
        // Каталог создаёт Claude Code, а он оставляет только [a-zA-Z0-9].
        // С char.IsLetterOrDigit мы искали бы каталог, которого нет.
        Assert.Equal("D--src--------", SessionSlug.From(@"D:\src\домовой"));
    }

    [Fact]
    public void UNC_путь_превращается_в_дефисы_целиком()
    {
        Assert.Equal("--server-share-project", SessionSlug.From(@"\\server\share\project"));
    }

    [Fact]
    public void Пустой_путь_не_бросает()
    {
        Assert.Equal(string.Empty, SessionSlug.From(string.Empty));
        Assert.Equal(string.Empty, SessionSlug.From("   "));
    }
}
