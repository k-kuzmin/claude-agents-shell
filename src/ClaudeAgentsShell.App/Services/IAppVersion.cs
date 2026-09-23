namespace ClaudeAgentsShell.App.Services;

/// <summary>
/// Версия запущенного приложения — та, что CI проставил при сборке релиза
/// (<c>-p:Version</c> из тега), либо дефолт <c>Directory.Build.props</c> в локальной сборке.
/// </summary>
/// <remarks>
/// Порт, а не чтение атрибута во ViewModel: рефлексии по сборке в ViewModel не место
/// (раздел 3 CLAUDE.md), а тесту так проще подставить свою строку.
/// </remarks>
public interface IAppVersion
{
    /// <summary>
    /// Информационная версия как есть, вместе с суффиксом сборки после <c>+</c>
    /// (.NET дописывает туда хэш коммита), например <c>0.2.1+1a2b3c4</c>.
    /// </summary>
    string InformationalVersion { get; }
}
