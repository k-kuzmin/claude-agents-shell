using System.Windows;
using ClaudeAgentsShell.App.ViewModels;

namespace ClaudeAgentsShell.App.Views;

/// <summary>
/// Модальное окно настроек проекта (раздел 6.5 ТЗ). Разметка и связь окна с ViewModel —
/// больше здесь ничего: правила полей живут в <see cref="ProjectSettingsViewModel"/>.
/// </summary>
public partial class ProjectSettingsWindow : Window
{
    private readonly ProjectSettingsViewModel _viewModel;

    /// <inheritdoc cref="ProjectSettingsWindow" />
    /// <param name="viewModel">Состояние диалога.</param>
    public ProjectSettingsWindow(ProjectSettingsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        viewModel.CloseRequested += OnCloseRequested;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    // Присваивание DialogResult само закрывает модальное окно. Отказ пользователя
    // приходит сюда как отсутствие результата.
    private void OnCloseRequested(object? sender, EventArgs e) =>
        DialogResult = _viewModel.Result is not null;

    private void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;
        Loaded -= OnLoaded;
        _viewModel.CloseRequested -= OnCloseRequested;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // async void допустим только в обработчиках событий — это они и есть.
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        // Фокус на первом поле: диалог открывается готовым к вводу, а не «куда-то».
        PathBox.Focus();
        PathBox.CaretIndex = PathBox.Text.Length;

        await _viewModel.RefreshAsync(CancellationToken.None);
    }

    // Проверка каталога — на потере фокуса, а не на каждом нажатии клавиши:
    // ходить в файловую систему по букве нельзя.
    private async void OnPathLostFocus(object sender, RoutedEventArgs e) =>
        await _viewModel.RefreshAsync(CancellationToken.None);
}
