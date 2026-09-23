using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClaudeAgentsShell.App.History;

namespace ClaudeAgentsShell.App.Views;

/// <summary>
/// Модальное окно истории сессий (раздел 6.4 ТЗ). Разметка, клавиши и связь с ViewModel —
/// больше здесь ничего: поиск и выбор живут в <see cref="SessionHistoryViewModel"/>.
/// </summary>
public partial class SessionHistoryWindow : Window
{
    // Шаг PageUp/PageDown в строках: примерно одна видимая страница списка.
    private const int PageStep = 8;

    private readonly SessionHistoryViewModel _viewModel;

    private string? _scrolledSessionId;

    /// <inheritdoc cref="SessionHistoryWindow" />
    /// <param name="viewModel">Состояние окна.</param>
    public SessionHistoryWindow(SessionHistoryViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        viewModel.CloseRequested += OnCloseRequested;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    // Присваивание DialogResult само закрывает модальное окно.
    private void OnCloseRequested(object? sender, EventArgs e) =>
        DialogResult = _viewModel.Result is not null;

    // Прокрутка к выделению — только когда выделена другая сессия. Пересборка списка по
    // событию наблюдателя даёт новый объект той же строки, и тянуть к нему прокрутку
    // значило бы мешать пользователю листать колесом.
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SessionHistoryViewModel.SelectedRow)
            || _viewModel.SelectedRow is not { } row)
        {
            return;
        }

        if (row.SessionId == _scrolledSessionId)
        {
            return;
        }

        _scrolledSessionId = row.SessionId;
        SessionList.ScrollIntoView(row);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;
        Loaded -= OnLoaded;
        _viewModel.CloseRequested -= OnCloseRequested;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    // Фокус всё время в поле поиска, поэтому клавиши навигации перехватываются окном
    // до того, как их съест TextBox.
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up:
                _viewModel.MoveSelection(-1);
                break;
            case Key.Down:
                _viewModel.MoveSelection(1);
                break;
            case Key.PageUp:
                _viewModel.MoveSelection(-PageStep);
                break;
            case Key.PageDown:
                _viewModel.MoveSelection(PageStep);
                break;
            case Key.Enter:
                _viewModel.Accept();
                break;
            case Key.Escape:
                _viewModel.Cancel();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void OnRowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: SessionHistoryRowViewModel row })
        {
            _viewModel.Select(row);
        }
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: SessionHistoryRowViewModel row })
        {
            _viewModel.Select(row);
            _viewModel.Accept();
            e.Handled = true;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => _viewModel.Cancel();

    // async void допустим только в обработчиках событий — это он и есть.
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        // Окно открывается готовым к вводу: печатать можно сразу, стрелки двигают выделение.
        SearchBox.Focus();

        await _viewModel.LoadAsync(CancellationToken.None);
    }
}
