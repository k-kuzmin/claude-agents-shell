# PROGRESS

Рабочее состояние проекта. Ведёт оркестратор, обновляет после каждого блока.
Это источник правды вместо истории диалога — при потере контекста читать отсюда.

## Текущий этап

**M1 — мост ConPTY ↔ WebView2.** Каркас и контракты готовы, реализация передана агенту.

Ветка: `stage/m1-pty-bridge`. Удалённый репозиторий: https://github.com/k-kuzmin/claude-agents-shell (public).

## Окружение (проверено)

| Что | Факт |
|---|---|
| .NET SDK | 9.0.317, установлены рантаймы 8.0.30 и WindowsDesktop 8.0.30 → целимся в `net8.0` |
| WebView2 Runtime | 153.0.4234.32 установлен |
| `pwsh` | **не найден в PATH** → на машине пользователя сразу работает откат на `powershell.exe` |
| node / npm | 24.13.0 / 11.6.2 — есть чем вендорить xterm.js |

## Зафиксированные контракты

Коммит `a7aea33`. Меняются только оркестратором.

**Domain** (без зависимостей): `TerminalId`, `TerminalSize`, `ShellKind`, `ShellStartCommand`,
`PtyStartInfo`, `SessionLaunch` (`NewSession` / `ResumeSession` / `ContinueLast`), `TabState`,
`ProjectDefinition`, `SessionSummary`, `HookEvent` + `HookKind`.

**Application.Ports**: `IPtySession` (+`IPtySessionFactory`, `PtyStartException`),
`IShellProvider` / `IShellResolver`, `ITerminalBridge` (+ три EventArgs,
`TerminalBridgeUnavailableException`), `IProjectStore`, `ISessionHistoryReader`, `IHookListener`.

**Terminal.Protocol**: `InboundBridgeMessage` (`Input` / `Resize` / `Ready`),
`IBridgeMessageWriter`, `IBridgeMessageParser`, `TerminalOptions` (кадр 16 мс, порог 64 КБ,
буфер чтения 16 КБ, `MaxPendingWrites` = 4, scrollback 5000, дебаунс ресайза 80 мс).

Ключевые решения в контрактах:

- `IPtySession.ReadAsync(Memory<byte>, ct)` — **pull-модель**. Темпом чтения владеет вызывающий,
  поэтому backpressure из раздела 3.3 ТЗ реализуется без дополнительного канала.
- `ITerminalBridge` живёт в `Application`, реализация с WebView2 — в `App`. Проект `Terminal`
  не имеет ссылки на WebView2.
- Протокол моста — ровно 5 + 3 типа сообщений из раздела 3.2 ТЗ, без расширений.
  Поля `cols`/`rows` в `ready` не добавлены: страница шлёт `ready`, затем `fit()`, затем `resize`.

## Блоки

| Блок | Исполнитель | Статус | Ревью | Заметки |
|---|---|---|---|---|
| Каркас решения + контракты | оркестратор | готово | — | коммит `a7aea33`, сборка и тесты зелёные (8 тестов) |
| M1 — мост ConPTY ↔ WebView2 | агент (последовательно) | в работе | — | ConPTY, страница, склейка, backpressure, тесты |

## Принятые замечания

Находки ревью и аудита, которые решено не чинить, с обоснованием.

Пока нет.

## Решения

- **ConPTY напрямую через P/Invoke, без `Pty.Net`.** В NuGet `Pty.Net` доступен только как
  `0.1.16-pre` (предрелиз, без стабильной версии). Раздел 2 ТЗ прямо допускает собственную
  обвязку над `CreatePseudoConsole`. Плюсом получаем полный контроль над сырым чтением и ресайзом.
- **xterm.js 5.5.0 + addon-fit 0.10.0 + addon-webgl 0.18.0 + addon-unicode11 0.8.0.**
  ТЗ требует 5.x; это последние стабильные аддоны, у которых `peerDependencies` указывает
  `@xterm/xterm ^5.0.0`. Более новые аддоны (0.11 / 0.19 / 0.9) выпущены под xterm 6.
- **Целевая платформа `net8.0-windows10.0.17763.0`** для `Terminal`, `App` и тестов —
  ConPTY требует Windows 10 1809. `Domain`, `Application`, `Sessions` — чистый `net8.0`.
- **Централизованные версии пакетов** (`Directory.Packages.props`), чтобы блоки не разъезжались
  по версиям при параллельной работе.
- **Контракты M5 (раскладка, настройки хуков) не фиксировались** — они добавляются в начале
  своего этапа, чтобы не плодить мёртвые типы сейчас.

## Открытые вопросы к пользователю

Пока нет.
