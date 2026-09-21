using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using ClaudeAgentsShell.App.ViewModels;
using ClaudeAgentsShell.App.Views;
using ClaudeAgentsShell.Domain;
using Xunit;

namespace ClaudeAgentsShell.Tests.App;

/// <summary>
/// Поиск перетаскиваемой вкладки под указателем (раздел 6.3 ТЗ). Полоса перехватывает
/// нажатие на своём корне, поэтому в обход приходит любой элемент окна — в том числе
/// текстовый <see cref="Run" /> из счётчика «N ждёт ввода». <see cref="Run" /> — это
/// <see cref="ContentElement" />, а не <see cref="Visual" />: шаг вверх через
/// <see cref="VisualTreeHelper.GetParent" /> на нём бросает исключение прямо из
/// обработчика маршрутизируемого события и роняет процесс. Тесты закрывают обе стороны:
/// нажатие на кнопку со встроенным текстом перетаскивания не начинает и не падает,
/// а нажатие на саму вкладку по-прежнему её находит.
/// </summary>
public sealed class TabStripHitTestTests
{
    [Fact]
    public void Нажатие_на_Run_внутри_кнопки_не_падает_и_не_начинает_перетаскивание()
    {
        RunOnUiThread(() =>
        {
            var run = new Run("3");
            var text = new TextBlock();
            text.Inlines.Add(run);
            text.Inlines.Add(new Run(" ждёт ввода"));

            var button = new Button { Content = text };
            Realize(button);

            // Тест обязан доходить до самой кнопки: если шаблон не развернулся и
            // TextBlock висит без визуального родителя, обход вернёт null «бесплатно»,
            // не проверив ни защиту от ButtonBase, ни безопасность шага вверх.
            Assert.Same(button, VisualAncestorButton(text));

            Assert.Null(TabStrip.DraggableTabAt(run));
        });
    }

    [Fact]
    public void Шаг_вверх_от_Run_даёт_содержащий_его_TextBlock()
    {
        RunOnUiThread(() =>
        {
            var run = new Run("3");
            var text = new TextBlock();
            text.Inlines.Add(run);

            Assert.Same(text, TabStrip.ParentOf(run));
        });
    }

    [Fact]
    public void Нажатие_внутри_вкладки_по_прежнему_находит_вкладку()
    {
        RunOnUiThread(() =>
        {
            var tab = new TabViewModel(TerminalId.New(), Guid.NewGuid(), "проект", @"D:\src\alpha");

            // Содержимое вкладки наследует её в DataContext, поэтому обход начинается
            // с самого глубокого элемента шаблона, как и при настоящем нажатии.
            var title = new TextBlock { Text = "сессия" };
            var layout = new StackPanel();
            layout.Children.Add(title);
            var item = new Border { DataContext = tab, Child = layout };
            var strip = new ItemsControl();
            strip.Items.Add(item);
            Realize(strip);

            Assert.Same(tab, TabStrip.DraggableTabAt(title));
        });
    }

    // Развёртывание шаблонов: без обмера и раскладки визуальное дерево контрола не
    // существует и обход упёрся бы в null на первом же шаге.
    private static void Realize(FrameworkElement root)
    {
        root.Measure(new Size(400, 100));
        root.Arrange(new Rect(0, 0, 400, 100));
        root.UpdateLayout();
    }

    private static Button? VisualAncestorButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Button button)
            {
                return button;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    // Объекты WPF создаются только в потоке STA: в репозитории это отдельный поток на
    // тест (см. AppCompositionTests), готовой инфраструктуры вроде [StaFact] здесь нет.
    private static void RunOnUiThread(Action body)
    {
        Exception? captured = null;

        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception exception)
            {
                captured = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Проверка в потоке интерфейса не завершилась за 30 секунд.");

        if (captured is not null)
        {
            throw new InvalidOperationException("Проверка в потоке интерфейса упала.", captured);
        }
    }
}
