using ClaudeAgentsShell.App.ViewModels;
using Xunit;

namespace ClaudeAgentsShell.Tests.App;

/// <summary>Разбор и сборка строки дополнительных аргументов (раздел 6.5 ТЗ).</summary>
public sealed class ExtraArgsSyntaxTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t \t")]
    public void Parse_EmptyText_ReturnsNoArguments(string? text) =>
        Assert.Empty(ExtraArgsSyntax.Parse(text));

    [Fact]
    public void Parse_SplitsOnWhitespace()
    {
        var args = ExtraArgsSyntax.Parse("--model opus --verbose");

        Assert.Equal(["--model", "opus", "--verbose"], args);
    }

    [Fact]
    public void Parse_CollapsesExtraWhitespace()
    {
        var args = ExtraArgsSyntax.Parse("   --model \t\t opus   ");

        Assert.Equal(["--model", "opus"], args);
    }

    [Fact]
    public void Parse_KeepsQuotedSpaces()
    {
        var args = ExtraArgsSyntax.Parse("--add-dir \"C:\\Program Files\\repo\" --verbose");

        Assert.Equal(["--add-dir", @"C:\Program Files\repo", "--verbose"], args);
    }

    [Fact]
    public void Parse_TrailingBackslashInsideQuotesSurvives()
    {
        // Обратный слэш не экранирует: путь Windows с хвостовым слэшем обязан выжить.
        var args = ExtraArgsSyntax.Parse(@"--add-dir ""C:\repo\""");

        Assert.Equal(["--add-dir", @"C:\repo\"], args);
    }

    [Fact]
    public void Parse_DoubledQuoteInsideQuotesIsLiteral()
    {
        var args = ExtraArgsSyntax.Parse("--append-system-prompt \"say \"\"hi\"\" twice\"");

        Assert.Equal(["--append-system-prompt", "say \"hi\" twice"], args);
    }

    [Fact]
    public void Parse_QuotedEmptyStringIsAnArgument()
    {
        var args = ExtraArgsSyntax.Parse("--flag \"\"");

        Assert.Equal(["--flag", string.Empty], args);
    }

    [Fact]
    public void Parse_QuotesGlueNeighbouringText()
    {
        var args = ExtraArgsSyntax.Parse("--dir=\"C:\\my repo\"");

        Assert.Equal([@"--dir=C:\my repo"], args);
    }

    [Fact]
    public void Parse_UnterminatedQuoteIsTolerated()
    {
        // Пользователь ещё набирает строку — ругаться на каждое нажатие нельзя.
        var args = ExtraArgsSyntax.Parse("--add-dir \"C:\\repo");

        Assert.Equal(["--add-dir", @"C:\repo"], args);
    }

    [Fact]
    public void Format_EmptyList_ReturnsEmptyText()
    {
        Assert.Equal(string.Empty, ExtraArgsSyntax.Format(null));
        Assert.Equal(string.Empty, ExtraArgsSyntax.Format([]));
    }

    [Fact]
    public void Format_LeavesPlainArgumentsUnquoted() =>
        Assert.Equal("--model opus", ExtraArgsSyntax.Format(["--model", "opus"]));

    [Fact]
    public void Format_QuotesOnlyWhatNeedsIt() =>
        Assert.Equal(
            "--add-dir \"C:\\my repo\"",
            ExtraArgsSyntax.Format(["--add-dir", @"C:\my repo"]));

    [Fact]
    public void Parse_AfterFormat_ReturnsSameArguments()
    {
        // Инвариант здесь ровно один — Parse(Format(args)) == args. Обратный не годится:
        // Format нормализует лишние пробелы и кавычки, и это его работа, а не дефект.
        string[][] cases =
        [
            [],
            ["--verbose"],
            ["--add-dir", @"C:\my repo\src"],
            ["--add-dir", @"C:\repo\"],
            ["--append-system-prompt", "say \"hi\""],
            ["--flag", string.Empty],
            ["--a", "  ", "--b"],
        ];

        foreach (var args in cases)
        {
            var text = ExtraArgsSyntax.Format(args);

            Assert.Equal(args, ExtraArgsSyntax.Parse(text));
        }
    }
}
