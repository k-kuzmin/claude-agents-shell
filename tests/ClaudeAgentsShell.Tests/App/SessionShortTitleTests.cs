using ClaudeAgentsShell.App.State;
using Xunit;

namespace ClaudeAgentsShell.Tests.App;

/// <summary>
/// Короткое имя сессии (раздел 6.3 ТЗ): первые слова первого сообщения одной строкой
/// и не длиннее предела вместе с многоточием.
/// </summary>
public sealed class SessionShortTitleTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n")]
    [InlineData("\t \t")]
    public void Пустое_сообщение_имени_не_даёт(string? raw) => Assert.Null(SessionShortTitle.Shorten(raw));

    [Fact]
    public void Короткое_сообщение_берётся_целиком() =>
        Assert.Equal("почини сборку", SessionShortTitle.Shorten("почини сборку"));

    [Fact]
    public void Края_обрезаются() =>
        Assert.Equal("почини сборку", SessionShortTitle.Shorten("  почини сборку \t "));

    [Fact]
    public void Берётся_только_первая_строка() =>
        Assert.Equal("почини сборку", SessionShortTitle.Shorten("почини сборку\nи ещё тесты\nи документацию"));

    [Fact]
    public void Перевод_строки_в_начале_не_мешает() =>
        Assert.Equal("почини сборку", SessionShortTitle.Shorten("\n\n   почини сборку"));

    [Fact]
    public void Пробельные_пачки_схлопываются() =>
        Assert.Equal("почини сборку", SessionShortTitle.Shorten("почини \t   сборку"));

    [Fact]
    public void Ровно_предельная_длина_не_режется()
    {
        var raw = new string('я', SessionShortTitle.MaxLength);

        Assert.Equal(raw, SessionShortTitle.Shorten(raw));
    }

    [Fact]
    public void На_символ_длиннее_предела_уже_режется()
    {
        var result = SessionShortTitle.Shorten(new string('я', SessionShortTitle.MaxLength + 1));

        Assert.NotNull(result);
        Assert.Equal(SessionShortTitle.MaxLength, result.Length);
        Assert.EndsWith("…", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Длинное_сообщение_режется_по_границе_слова()
    {
        // 47 символов: граница слова приходится на пробел перед «полосе».
        const string raw = "перепиши координатор состояний вкладок в полосе";

        var result = SessionShortTitle.Shorten(raw);

        Assert.NotNull(result);
        Assert.Equal("перепиши координатор состояний вкладок…", result);
        Assert.True(result.Length <= SessionShortTitle.MaxLength);
    }

    [Fact]
    public void Слово_без_пробелов_режется_жёстко()
    {
        var result = SessionShortTitle.Shorten(new string('щ', 200));

        Assert.NotNull(result);
        Assert.Equal(SessionShortTitle.MaxLength, result.Length);
        Assert.Equal(new string('щ', SessionShortTitle.MaxLength - 1) + "…", result);
    }

    [Fact]
    public void Слишком_ранняя_граница_слова_не_используется()
    {
        // Пробел стоит на третьем символе: обрыв по нему оставил бы «раз…» вместо смысла.
        var raw = "раз " + new string('ы', 100);

        var result = SessionShortTitle.Shorten(raw);

        Assert.NotNull(result);
        Assert.Equal(SessionShortTitle.MaxLength, result.Length);
        Assert.StartsWith("раз ы", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Суррогатная_пара_не_разрезается_пополам()
    {
        // Эмодзи занимает два char: жёсткий рез пришёлся бы ровно на его середину.
        var raw = new string('a', SessionShortTitle.MaxLength - 2) + "\U0001F600" + new string('b', 20);

        var result = SessionShortTitle.Shorten(raw);

        Assert.NotNull(result);
        Assert.DoesNotContain(result, char.IsHighSurrogate);
        Assert.True(result.Length <= SessionShortTitle.MaxLength);
    }

    [Fact]
    public void Управляющие_символы_становятся_пробелом() =>
        Assert.Equal("почини сборку", SessionShortTitle.Shorten("почини\u0007сборку"));

    [Fact]
    public void Очень_длинное_сообщение_не_перебирается_целиком()
    {
        // Вставленный лог на сотни килобайт не должен ни ронять, ни задерживать разбор.
        var result = SessionShortTitle.Shorten(new string('о', 500_000));

        Assert.NotNull(result);
        Assert.Equal(SessionShortTitle.MaxLength, result.Length);
    }
}
