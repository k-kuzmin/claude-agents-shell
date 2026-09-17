using System.Windows.Input;
using ClaudeAgentsShell.App.Input;
using Xunit;

namespace ClaudeAgentsShell.Tests.App;

/// <summary>
/// Разбор сочетаний. Проверяется и то, что распознаётся, и то, что обязано уйти в оболочку:
/// клавиши сравниваются по физической кнопке, поэтому тесты не зависят от раскладки.
/// </summary>
public sealed class ShellShortcutMapTests
{
    private static (bool Handled, ShellShortcut Shortcut, int Number) Map(Key key, ModifierKeys modifiers)
    {
        var handled = ShellShortcutMap.TryMap(key, modifiers, out var shortcut, out var number);
        return (handled, shortcut, number);
    }

    [Theory]
    [InlineData(Key.T, ShellShortcut.NewSession)]
    [InlineData(Key.W, ShellShortcut.CloseTab)]
    public void Ctrl_shift_letters_are_window_commands(Key key, ShellShortcut expected)
    {
        var result = Map(key, ModifierKeys.Control | ModifierKeys.Shift);

        Assert.True(result.Handled);
        Assert.Equal(expected, result.Shortcut);
    }

    [Theory]
    [InlineData(Key.T)]
    [InlineData(Key.W)]
    [InlineData(Key.C)]
    [InlineData(Key.R)]
    public void Ctrl_letters_belong_to_the_shell(Key key)
    {
        // Ctrl+T и Ctrl+W — transpose-chars и kill-word в readline: окно их не трогает.
        var result = Map(key, ModifierKeys.Control);

        Assert.False(result.Handled);
        Assert.Equal(ShellShortcut.None, result.Shortcut);
    }

    [Fact]
    public void Ctrl_tab_and_ctrl_shift_tab_walk_the_strip()
    {
        Assert.Equal(ShellShortcut.NextTab, Map(Key.Tab, ModifierKeys.Control).Shortcut);
        Assert.Equal(ShellShortcut.PreviousTab, Map(Key.Tab, ModifierKeys.Control | ModifierKeys.Shift).Shortcut);
    }

    [Theory]
    [InlineData(Key.D1, 1)]
    [InlineData(Key.D5, 5)]
    [InlineData(Key.D9, 9)]
    [InlineData(Key.NumPad1, 1)]
    [InlineData(Key.NumPad9, 9)]
    public void Ctrl_digit_selects_a_tab_by_number(Key key, int expected)
    {
        var result = Map(key, ModifierKeys.Control);

        Assert.True(result.Handled);
        Assert.Equal(ShellShortcut.SelectTab, result.Shortcut);
        Assert.Equal(expected, result.Number);
    }

    [Theory]
    [InlineData(Key.D0)]
    [InlineData(Key.NumPad0)]
    public void Zero_is_not_a_tab_number(Key key) => Assert.False(Map(key, ModifierKeys.Control).Handled);

    [Theory]
    [InlineData(Key.D1, ModifierKeys.None)]
    [InlineData(Key.D1, ModifierKeys.Shift)]
    [InlineData(Key.D1, ModifierKeys.Control | ModifierKeys.Alt)]
    [InlineData(Key.T, ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt)]
    [InlineData(Key.Tab, ModifierKeys.None)]
    [InlineData(Key.Tab, ModifierKeys.Alt)]
    [InlineData(Key.A, ModifierKeys.Control | ModifierKeys.Shift)]
    [InlineData(Key.Enter, ModifierKeys.Control)]
    [InlineData(Key.OemPlus, ModifierKeys.Control)]
    public void Everything_else_goes_to_the_terminal(Key key, ModifierKeys modifiers)
    {
        var result = Map(key, modifiers);

        Assert.False(result.Handled);
        Assert.Equal(ShellShortcut.None, result.Shortcut);
        Assert.Equal(0, result.Number);
    }

    [Fact]
    public void Windows_key_combinations_are_left_alone() =>
        Assert.False(Map(Key.T, ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Windows).Handled);
}
