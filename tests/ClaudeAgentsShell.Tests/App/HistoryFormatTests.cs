using ClaudeAgentsShell.App.History;
using Xunit;

namespace ClaudeAgentsShell.Tests.App;

public sealed class HistoryFormatTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 18, 0, 0, TimeSpan.Zero);

    // Фиксированный пояс UTC+3 без перехода на летнее время — результат не зависит от машины.
    private static readonly TimeZoneInfo Plus3 =
        TimeZoneInfo.CreateCustomTimeZone("test+3", TimeSpan.FromHours(3), "test+3", "test+3");

    [Theory]
    [InlineData(2026, 9, 23, 14, 36, "сегодня 14:36")]
    [InlineData(2026, 9, 22, 21, 48, "вчера 21:48")]
    [InlineData(2026, 9, 15, 11, 24, "15 сен 11:24")]
    [InlineData(2026, 5, 1, 8, 0, "1 мая 08:00")]
    [InlineData(2025, 12, 31, 23, 0, "31 дек 2025")]
    public void Date_is_relative_near_today_and_short_further(int year, int month, int day, int hour, int minute, string expected)
    {
        var modified = new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero);

        Assert.Equal(expected, SessionHistoryFormat.Date(modified, Now, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Date_uses_users_calendar_day()
    {
        // 22:30 UTC — это уже 01:30 следующего дня по UTC+3.
        var now = new DateTimeOffset(2026, 9, 23, 22, 30, 0, TimeSpan.Zero);
        var modified = new DateTimeOffset(2026, 9, 23, 20, 0, 0, TimeSpan.Zero);

        Assert.Equal("вчера 23:00", SessionHistoryFormat.Date(modified, now, Plus3));
    }

    [Theory]
    [InlineData("0d41f2a7-1111-2222-3333-444455556666", "0d41f2a7")]
    [InlineData("short", "short")]
    public void Short_id_is_first_eight_characters(string id, string expected) =>
        Assert.Equal(expected, SessionHistoryFormat.ShortId(id));

    [Theory]
    [InlineData(null, null)]
    [InlineData("   \n\t ", null)]
    [InlineData("\n\n  первая \t строка  \nвторая", "первая строка")]
    [InlineData("одна", "одна")]
    public void Single_line_takes_first_non_empty_line_and_collapses_spaces(string? raw, string? expected) =>
        Assert.Equal(expected, SessionHistoryFormat.SingleLine(raw));
}
