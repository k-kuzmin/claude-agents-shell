using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using ClaudeAgentsShell.App.ViewModels;

namespace ClaudeAgentsShell.App.Views;

/// <summary>
/// Полоса вкладок. Разметка и мышь: перетаскивание вкладок (раздел 6.3 ТЗ) живёт здесь,
/// потому что это работа с указателем и координатами контейнеров, а не логика приложения.
/// Сам порядок меняет <see cref="TabStripViewModel.Reorder" /> — отсюда ему сообщают
/// только то, какую вкладку и в какой промежуток полосы бросили.
/// </summary>
public partial class TabStrip : UserControl
{
    // Формат перетаскивания. Сама вкладка лежит в поле: перетаскивание всегда внутри одного
    // окна, а формат нужен, чтобы не спутать свой «груз» с файлами и текстом извне.
    private const string TabDragFormat = "ClaudeAgentsShell.Tab";

    // Порог в пикселях, ниже которого нажатие остаётся кликом и перетаскивание не начинается.
    // Взят чуть больше системного SystemParameters.MinimumHorizontalDragDistance: попасть
    // по крестику дрогнувшей рукой важнее, чем поймать самое короткое перетаскивание.
    private const double DragThreshold = 6d;

    // Вкладка под зажатой кнопкой мыши — кандидат на перетаскивание, пока порог не пройден.
    private TabViewModel? _pressedTab;

    // Вкладка, которую тащат прямо сейчас; null — перетаскивания нет.
    private TabViewModel? _draggedTab;

    private Point _pressOrigin;

    /// <inheritdoc cref="TabStrip" />
    public TabStrip() => InitializeComponent();

    // Перетаскивать можно только за саму вкладку: нажатие на крестик или на «+» — это кнопка.
    // Ищем вкладку вверх по дереву от того, куда попал указатель, и отказываемся, если по
    // дороге встретилась кнопка: её содержимое наследует ту же вкладку в DataContext.
    internal static TabViewModel? DraggableTabAt(DependencyObject? source)
    {
        TabViewModel? tab = null;

        while (source is not null)
        {
            if (source is ButtonBase)
            {
                return null;
            }

            if (tab is null && TabOf(source) is { } candidate)
            {
                tab = candidate;
            }

            if (source is ItemsControl)
            {
                break;
            }

            // Шаг наверх делается только через ParentOf: он единственный знает, каким
            // деревом идти, и рано или поздно возвращает null — обход конечен.
            source = ParentOf(source);
        }

        return tab;
    }

    /// <summary>
    /// Шаг вверх по дереву, переживающий любой <see cref="DependencyObject" />. Полоса
    /// перехватывает нажатие на своём корне, поэтому сюда приходит что угодно из окна —
    /// в том числе <see cref="System.Windows.Documents.Run" /> из счётчика «N ждёт ввода».
    /// <see cref="ContentElement" /> не является <see cref="Visual" />, и
    /// <see cref="VisualTreeHelper.GetParent" /> на нём бросает исключение прямо из
    /// обработчика события мыши — ловить его уже некому, процесс умирает.
    /// </summary>
    /// <param name="source">Узел, от которого делается шаг.</param>
    /// <returns>Родитель или <see langword="null" />, если дерево кончилось.</returns>
    internal static DependencyObject? ParentOf(DependencyObject source) => source switch
    {
        Visual or Visual3D => VisualTreeHelper.GetParent(source),

        // Для inline-текста визуального родителя нет. ContentOperations знает про хозяина
        // содержимого, а для Run внутри TextBlock отвечает логическое дерево: оно и выводит
        // обход на сам TextBlock, откуда дальше идёт обычное визуальное дерево.
        ContentElement content => ContentOperations.GetParent(content) ?? LogicalTreeHelper.GetParent(content),

        _ => LogicalTreeHelper.GetParent(source),
    };

    // Вкладку несут и FrameworkElement, и FrameworkContentElement: общего предка с
    // DataContext у них нет, поэтому разбор по двум веткам. Inline-текста внутри вкладок
    // сегодня нет, но появившийся в шаблоне <Run> иначе тихо сломал бы перетаскивание.
    private static TabViewModel? TabOf(DependencyObject source) => source switch
    {
        FrameworkElement element => element.DataContext as TabViewModel,
        FrameworkContentElement element => element.DataContext as TabViewModel,
        _ => null,
    };

    private void OnTabPressed(object sender, MouseButtonEventArgs e)
    {
        // Событие только запоминается: переключение вкладки и закрытие крестиком идут своим
        // чередом, перетаскивание начнётся лишь когда указатель уйдёт дальше порога.
        _pressOrigin = e.GetPosition(this);
        _pressedTab = DraggableTabAt(e.OriginalSource as DependencyObject);
    }

    private void OnTabReleased(object sender, MouseButtonEventArgs e) => _pressedTab = null;

    private void OnTabPointerMoved(object sender, MouseEventArgs e)
    {
        if (_pressedTab is not { } tab)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _pressedTab = null;
            return;
        }

        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _pressOrigin.X) < DragThreshold
            && Math.Abs(position.Y - _pressOrigin.Y) < DragThreshold)
        {
            return;
        }

        _pressedTab = null;
        _draggedTab = tab;

        try
        {
            DragDrop.DoDragDrop(TabItems, new DataObject(TabDragFormat, string.Empty), DragDropEffects.Move);
        }
        finally
        {
            // Бросок мимо полосы, Esc, потеря захвата — каретку убираем в любом случае.
            _draggedTab = null;
            HideCaret();
        }
    }

    private void OnTabDragOver(object sender, DragEventArgs e)
    {
        if (_draggedTab is null || !e.Data.GetDataPresent(TabDragFormat))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        ShowCaret(GapAt(e.GetPosition(TabItems)));
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnTabDragLeave(object sender, DragEventArgs e) => HideCaret();

    private void OnTabDropped(object sender, DragEventArgs e)
    {
        if (_draggedTab is { } tab && DataContext is ShellViewModel shell)
        {
            shell.Tabs.Reorder(tab, GapAt(e.GetPosition(TabItems)));
        }

        HideCaret();
        e.Handled = true;
    }

    private FrameworkElement? ContainerAt(int index) =>
        TabItems.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;

    // Ближайший к указателю промежуток полосы: 0 — перед первой вкладкой, число вкладок —
    // за последней. Границей служит середина вкладки, поэтому вкладка встаёт туда, к чьей
    // половине ближе указатель.
    private int GapAt(Point position)
    {
        var count = TabItems.Items.Count;

        for (var index = 0; index < count; index++)
        {
            if (ContainerAt(index) is not { } container)
            {
                continue;
            }

            var left = container.TranslatePoint(default, TabItems).X;
            if (position.X < left + (container.ActualWidth / 2))
            {
                return index;
            }
        }

        return count;
    }

    // Каретка встаёт на границу между вкладками, поэтому смещается на половину своей толщины.
    private void ShowCaret(int gap)
    {
        var count = TabItems.Items.Count;
        var edge = 0d;

        if (count > 0 && ContainerAt(Math.Min(gap, count - 1)) is { } container)
        {
            edge = container.TranslatePoint(default, TabItems).X;
            if (gap >= count)
            {
                edge += container.ActualWidth;
            }
        }

        DropCaret.Margin = new Thickness(Math.Max(0d, edge - (DropCaret.Width / 2)), 0, 0, 0);
        DropCaret.Visibility = Visibility.Visible;
    }

    private void HideCaret() => DropCaret.Visibility = Visibility.Collapsed;
}
