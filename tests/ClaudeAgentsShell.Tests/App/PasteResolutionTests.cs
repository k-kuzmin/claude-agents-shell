using System.Runtime.InteropServices;
using ClaudeAgentsShell.App.Input;
using ClaudeAgentsShell.App.Services;
using ClaudeAgentsShell.Terminal.Protocol;
using Xunit;

namespace ClaudeAgentsShell.Tests.App;

public sealed class PasteResolutionTests
{
    [Fact]
    public void Пути_без_пробелов_идут_через_пробел_без_кавычек()
    {
        Assert.Equal(@"C:\a.txt D:\b\c.png", PasteResolution.FormatPaths([@"C:\a.txt", @"D:\b\c.png"]));
    }

    [Fact]
    public void Путь_с_пробелом_берётся_в_кавычки()
    {
        Assert.Equal(
            @"""C:\Мои файлы\скрин 1.png"" C:\x.txt",
            PasteResolution.FormatPaths([@"C:\Мои файлы\скрин 1.png", @"C:\x.txt"]));
    }

    [Fact]
    public void Завершающего_пробела_нет()
    {
        Assert.Equal(@"C:\a.txt", PasteResolution.FormatPaths([@"C:\a.txt"]));
    }

    [Fact]
    public void Пустые_элементы_пропускаются()
    {
        Assert.Equal(@"C:\a C:\b", PasteResolution.FormatPaths([@"C:\a", "", @"C:\b"]));
    }

    [Fact]
    public void Бросок_без_путей_ничего_не_вставляет()
    {
        Assert.IsType<PasteContent.None>(PasteResolution.FromDrop([]));
    }

    [Fact]
    public void Бросок_файлов_вставляет_пути()
    {
        var text = Assert.IsType<PasteContent.Text>(PasteResolution.FromDrop([@"C:\a b\c.txt"]));

        Assert.Equal(@"""C:\a b\c.txt""", text.Value);
    }

    [Fact]
    public void Файлы_в_буфере_побеждают_изображение()
    {
        var clipboard = new FakeClipboard(new ClipboardSnapshot([@"C:\shot.png"], HasImage: true));

        var text = Assert.IsType<PasteContent.Text>(PasteResolution.FromClipboard(clipboard));

        Assert.Equal(@"C:\shot.png", text.Value);
    }

    [Fact]
    public void Изображение_без_файлов_вставляется_картинкой()
    {
        var clipboard = new FakeClipboard(new ClipboardSnapshot([], HasImage: true));

        Assert.IsType<PasteContent.Image>(PasteResolution.FromClipboard(clipboard));
    }

    [Fact]
    public void Пустой_буфер_ничего_не_вставляет()
    {
        Assert.IsType<PasteContent.None>(PasteResolution.FromClipboard(new FakeClipboard(ClipboardSnapshot.Empty)));
    }

    [Fact]
    public void Занятый_буфер_ничего_не_вставляет()
    {
        var clipboard = new FakeClipboard(new COMException("CLIPBRD_E_CANT_OPEN", unchecked((int)0x800401D0)));

        Assert.IsType<PasteContent.None>(PasteResolution.FromClipboard(clipboard));
    }

    private sealed class FakeClipboard : IClipboardReader
    {
        private readonly ClipboardSnapshot? _snapshot;
        private readonly Exception? _failure;

        public FakeClipboard(ClipboardSnapshot snapshot) => _snapshot = snapshot;

        public FakeClipboard(Exception failure) => _failure = failure;

        public ClipboardSnapshot Read() => _failure is null ? _snapshot! : throw _failure;
    }
}
