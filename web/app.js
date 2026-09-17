// Страница терминалов. Держит N экземпляров xterm.js в одном WebView2:
// переключение вкладки — смена display у контейнера, а не новый контрол (раздел 3.1 ТЗ).
//
// Протокол — раздел 3.2 ТЗ:
//   из C#:      out | create | show | close | exited
//   в C#:       in  | resize  | ready
//   служебное:  ack — подтверждение term.write, без него не посчитать незавершённые записи
//               (раздел 3.3 ТЗ требует этот счётчик).
(function () {
  'use strict';

  // Настройки приходят из C# (TerminalOptions) скриптом, выполняемым до создания документа.
  // Дублировать их здесь константами нельзя: получилось бы два источника правды.
  // Ноль здесь — легальное значение (раздел 3.5 ТЗ: «настраивается»), поэтому проверяем
  // тип, а не правдивость. Проверка выполняется ниже, когда страница уже умеет показать
  // сообщение: бросать исключение в начале скрипта нельзя — обработчик сообщений не будет
  // назначен, C# никогда не получит ready и увидит чёрное окно без всякой диагностики.
  var config = window.__terminalConfig || {};
  var RESIZE_DEBOUNCE_MS = config.resizeDebounceMs;
  var SCROLLBACK = config.scrollback;

  var THEME = {
    background: '#0E0E0D',
    foreground: '#C9C4BB',
    cursor: '#D97757',
    cursorAccent: '#0E0E0D',
    selectionBackground: '#3A3A36'
  };

  var host = document.getElementById('terminals');
  var terminals = new Map();
  var encoder = new TextEncoder();

  // Размер один на всю страницу. Терминалы — соседние элементы одного контейнера, различаются
  // только видимостью, поэтому cols/rows у них общие; 0 означает «ещё не мерили».
  var currentCols = 0;
  var currentRows = 0;
  var resizeTimer = 0;
  var measureAttempts = 0;

  // Сколько раз повторить замер, если рендерер видимой вкладки ещё не померил знакоместо.
  // Он просыпается по IntersectionObserver уже после снятия display:none, и первый замер
  // сразу после показа может не успеть.
  var MAX_MEASURE_ATTEMPTS = 10;

  function post(message) {
    window.chrome.webview.postMessage(JSON.stringify(message));
  }

  // Горячие клавиши браузера, которые ломают окно терминала: перезагрузка страницы,
  // поиск, печать, навигация по истории, зум.
  //
  // Гасится только действие браузера (preventDefault), но НЕ распространение события:
  // Ctrl+R, Ctrl+F, Ctrl+P и Ctrl+G — обычные управляющие символы для readline, и они
  // обязаны дойти до xterm.js. stopPropagation здесь отнял бы их у терминала.
  //
  // Клавиши буфера обмена (Ctrl+C, Ctrl+V, Ctrl+X, Ctrl+A) в список не входят намеренно.
  // Буквы и цифры сравниваются по event.code — физической клавише. event.key отражает
  // раскладку: на ЙЦУКЕН та же клавиша даёт 'м' вместо 'v' и 'к' вместо 'r', и сравнение
  // по key делает все эти ветки мёртвыми для русской раскладки.
  // Enter, F5 и стрелки от раскладки не зависят, их имена в key корректны.
  function codeOf(event) {
    return typeof event.code === 'string' ? event.code : '';
  }

  function isDestructiveBrowserShortcut(event) {
    if (event.key === 'F5') {
      return true;
    }

    if (event.altKey && (event.key === 'ArrowLeft' || event.key === 'ArrowRight')) {
      return true;
    }

    if (!event.ctrlKey) {
      return false;
    }

    var code = codeOf(event);
    if (code === 'KeyR' || code === 'KeyF' || code === 'KeyP' || code === 'KeyG') {
      return true;
    }

    return code === 'Minus' || code === 'Equal' || code === 'Digit0'
      || code === 'NumpadAdd' || code === 'NumpadSubtract' || code === 'Numpad0';
  }

  // Оконные сочетания WPF: новая сессия, закрытие вкладки, переключение вкладок.
  // Их обрабатывает окно (ShellShortcutMap), а не терминал, поэтому в xterm они не
  // доставляются — иначе сочетание сработало бы дважды: как команда окна и как ввод.
  //
  // До WPF они доходят сами: обёртка WebView2 подписана на AcceleratorKeyPressed и заводит
  // акселераторы в систему ввода WPF в процессе хоста, независимо от того, что с событием
  // сделала страница. Поэтому preventDefault здесь безопасен, а stopPropagation не нужен
  // и запрещён — он ничего не даёт WPF и отнимает событие у остальной страницы.
  //
  // Условия повторяют ShellShortcutMap один в один: без Alt и Win, обязательный Ctrl,
  // Ctrl+Shift+T / Ctrl+Shift+W, Ctrl+Tab с любым Shift, Ctrl+цифра только без Shift.
  // Голые Ctrl+T и Ctrl+W сюда не попадают намеренно — они уходят в оболочку.
  function isWindowShortcut(event) {
    if (!event.ctrlKey || event.altKey || event.metaKey) {
      return false;
    }

    var code = codeOf(event);

    if (code === 'Tab') {
      return true;
    }

    if (event.shiftKey) {
      return code === 'KeyT' || code === 'KeyW';
    }

    // Цифровой ряд и цифровая клавиатура — одна и та же физическая цифра.
    return /^(Digit|Numpad)[1-9]$/.test(code);
  }

  document.addEventListener('keydown', function (event) {
    if (isDestructiveBrowserShortcut(event) || isWindowShortcut(event)) {
      event.preventDefault();
    }
  }, true);

  // base64 → байты, побайтно. Никакого TextDecoder: строка на пути вывода порвала бы
  // UTF-8 последовательность на границе пачки.
  function decodeBase64(b64) {
    var binary = atob(b64);
    var length = binary.length;
    var bytes = new Uint8Array(length);
    for (var i = 0; i < length; i++) {
      bytes[i] = binary.charCodeAt(i) & 0xff;
    }
    return bytes;
  }

  function encodeBase64(bytes) {
    var binary = '';
    var chunk = 0x8000;
    for (var i = 0; i < bytes.length; i += chunk) {
      binary += String.fromCharCode.apply(null, bytes.subarray(i, i + chunk));
    }
    return btoa(binary);
  }

  function sendInput(id, bytes) {
    if (bytes.length > 0) {
      post({ type: 'in', id: id, b64: encodeBase64(bytes) });
    }
  }

  function isVisible(element) {
    return element.offsetParent !== null && element.clientWidth > 0 && element.clientHeight > 0;
  }

  function visibleEntry() {
    var found = null;
    terminals.forEach(function (entry) {
      if (found === null && isVisible(entry.element)) {
        found = entry;
      }
    });
    return found;
  }

  // Замер делается ОДИН раз и только по видимому контейнеру: у скрытого элемента нулевые
  // размеры, и fit() у него запрещён (раздел 3.4 ТЗ). Размер контейнера общий для всех
  // вкладок, поэтому померенного хватает на всех.
  function measure() {
    var entry = visibleEntry();
    if (!entry) {
      return null;
    }

    var dims;
    try {
      dims = entry.fit.proposeDimensions();
    } catch (error) {
      return null;
    }

    if (!dims || !(dims.cols > 0) || !(dims.rows > 0)) {
      // Знакоместо ещё не померено — держим прежний размер, повторим на следующем тике.
      return null;
    }

    return dims;
  }

  // Размер применяется ко ВСЕМ терминалам, включая скрытые, и в C# уходит по сообщению
  // resize на каждый идентификатор. Иначе скрытая вкладка вернулась бы со старыми cols/rows
  // и перерисовалась по неверной ширине — это «лесенка» из критерия 5 раздела 7 ТЗ
  // и полная перерисовка вопреки критерию 2.
  function applySize(cols, rows) {
    currentCols = cols;
    currentRows = rows;

    terminals.forEach(function (entry) {
      resizeTerminal(entry, cols, rows);
    });
  }

  // Скрытым размер ставится напрямую term.resize, без fit(): fit() читает размеры элемента,
  // а у невидимого они нулевые. Буфер при этом перекладывается как надо, а рендерер xterm
  // догоняет сам — он приостановлен, пока вкладка скрыта, и просыпается при показе.
  function resizeTerminal(entry, cols, rows) {
    if (entry.cols === cols && entry.rows === rows) {
      // Размер не менялся: ни перекладки буфера, ни сообщения в C#.
      return;
    }

    entry.cols = cols;
    entry.rows = rows;

    try {
      entry.term.resize(cols, rows);
    } catch (error) {
      // Терминал уже освобождён — сообщать о его размере нечему.
      return;
    }

    post({ type: 'resize', id: entry.id, cols: cols, rows: rows });
  }

  function scheduleResize() {
    measureAttempts = 0;
    armResizeTimer();
  }

  function armResizeTimer() {
    if (resizeTimer !== 0) {
      clearTimeout(resizeTimer);
    }

    resizeTimer = setTimeout(onResizeTick, RESIZE_DEBOUNCE_MS);
  }

  function onResizeTick() {
    resizeTimer = 0;

    var dims = measure();
    if (dims) {
      applySize(dims.cols, dims.rows);
      return;
    }

    // Мерить было нечем. Если видимая вкладка есть, её рендерер просто ещё не проснулся —
    // повторяем ограниченное число раз, иначе вкладка осталась бы с размером по умолчанию.
    if (visibleEntry() && ++measureAttempts < MAX_MEASURE_ATTEMPTS) {
      armResizeTimer();
    }
  }

  function attachRenderer(entry) {
    if (typeof WebglAddon === 'undefined') {
      return;
    }

    try {
      var webgl = new WebglAddon.WebglAddon();
      webgl.onContextLoss(function () {
        // Контекст потерян — снимаем аддон, рендер продолжается на DOM-рендерере xterm.
        // Это же и есть защита от лимита контекстов WebGL на страницу: когда вкладок
        // становится больше, чем Chromium держит контекстов (порядка 16), он гасит самый
        // старый, и та вкладка молча переезжает на DOM. Деградация мягкая и не наша.
        try {
          webgl.dispose();
        } catch (error) {
          // Аддон уже освобождён.
        }
        entry.webgl = null;
      });
      entry.term.loadAddon(webgl);
      entry.webgl = webgl;
    } catch (error) {
      // Нет WebGL2 — остаёмся на DOM-рендерере xterm, это штатный откат.
      entry.webgl = null;
    }
  }

  function copySelection(term) {
    var selection = term.getSelection();
    if (!selection) {
      return;
    }

    if (navigator.clipboard && navigator.clipboard.writeText) {
      navigator.clipboard.writeText(selection).catch(function () {
        /* Буфер обмена недоступен — выделение просто остаётся на месте. */
      });
    }
  }

  function pasteFromClipboard(term) {
    if (!navigator.clipboard || !navigator.clipboard.readText) {
      return;
    }

    navigator.clipboard.readText().then(function (text) {
      if (text) {
        // term.paste сам оборачивает текст в bracketed paste, когда оболочка его включила.
        term.paste(text);
      }
    }).catch(function () {
      /* Доступ к буферу обмена не дали — вставки не будет. */
    });
  }

  function installKeyHandler(entry) {
    entry.term.attachCustomKeyEventHandler(function (event) {
      if (event.type !== 'keydown') {
        return true;
      }

      // Сочетание принадлежит окну — в терминал его не отдаём. preventDefault уже сделан
      // общим обработчиком; здесь важен именно false: без него xterm отправил бы в PTY
      // управляющий символ, и, например, Ctrl+Shift+W открыл бы вкладку и заодно послал
      // ввод в оболочку.
      if (isWindowShortcut(event)) {
        return false;
      }

      // Shift+Enter → ESC CR. Обычный терминал не отличает эту комбинацию от Enter, поэтому
      // Claude Code и предлагает /terminal-setup; здесь это работает из коробки.
      // Чистый Enter и Ctrl+Enter не затрагиваются — условие требует именно Shift без Ctrl и Alt.
      if (event.key === 'Enter' && event.shiftKey && !event.ctrlKey && !event.altKey && !event.metaKey) {
        event.preventDefault();
        sendInput(entry.id, encoder.encode('\u001b\r'));
        return false;
      }

      var code = codeOf(event);

      // Ctrl+V и Ctrl+Shift+V — вставка.
      //
      // Сам xterm.js вставку по Ctrl+V не делает: он отображает эту комбинацию в управляющий
      // символ 0x16 и отменяет событие, из-за чего Chromium не выполняет команду Paste и
      // обработчик вставки на скрытой textarea не вызывается никогда. Поэтому вставку
      // выполняем сами, из буфера обмена.
      //
      // preventDefault здесь обязателен: возврат false лишь говорит xterm.js не обрабатывать
      // клавишу, но обработчик вызывается из обычного addEventListener, и на действие
      // браузера его возвращаемое значение не влияет.
      //
      // Цена решения: 0x16 (quoted-insert в readline) через Ctrl+V больше не ввести.
      if (event.ctrlKey && !event.altKey && code === 'KeyV') {
        event.preventDefault();
        pasteFromClipboard(entry.term);
        return false;
      }

      // Ctrl+C: есть выделение — копируем, нет — пусть уходит SIGINT в оболочку.
      // Оговорка: случайное выделение мышью превращает попытку прервать агента в копирование.
      // Так же ведёт себя Windows Terminal, поведение выбрано осознанно.
      if (event.ctrlKey && !event.shiftKey && !event.altKey && code === 'KeyC' && entry.term.hasSelection()) {
        // preventDefault убирает гонку: на команду Copy у xterm.js есть собственный обработчик,
        // и он записал бы выделение в буфер синхронно, наперегонки с нашей асинхронной записью.
        event.preventDefault();
        copySelection(entry.term);
        entry.term.clearSelection();
        return false;
      }

      return true;
    });
  }

  function createTerminal(id, title) {
    if (terminals.has(id)) {
      return;
    }

    var element = document.createElement('div');
    element.className = 'terminal-host hidden';
    element.setAttribute('data-terminal-id', id);
    element.title = title || '';
    host.appendChild(element);

    var term = new Terminal({
      allowProposedApi: true, // нужен для unicode11
      scrollback: SCROLLBACK,
      fontFamily: "Consolas, 'Cascadia Mono', monospace",
      fontSize: 14,
      lineHeight: 1.2,
      cursorBlink: true,
      convertEol: false,
      theme: THEME
    });

    var fit = new FitAddon.FitAddon();
    term.loadAddon(fit);

    // Один loadAddon для unicode11 — no-op: без activeVersion ширина эмодзи остаётся старой.
    if (typeof Unicode11Addon !== 'undefined') {
      term.loadAddon(new Unicode11Addon.Unicode11Addon());
      term.unicode.activeVersion = '11';
    }

    var entry = {
      id: id,
      term: term,
      fit: fit,
      webgl: null,
      element: element,
      cols: 0,
      rows: 0
    };

    // Терминал ОТКРЫВАЕТСЯ СКРЫТЫМ, и на этом держится мгновенное переключение вкладок.
    // addon-fit вычитает из доступной ширины viewport.scrollBarWidth, а xterm 5.5 считает
    // его один раз в конструкторе Viewport как «offsetWidth − offsetWidth || 15»: у скрытого
    // элемента обе величины нулевые, значит у всех терминалов берётся один и тот же
    // запасной 15. Плюс .xterm-viewport { overflow-y: scroll } — полоса всегда занимает
    // место. Отсюда proposeDimensions() даёт одни и те же cols/rows, какая бы вкладка
    // ни была видима, и показ соседней вкладки не перекладывает ни одного буфера.
    //
    // Инвариант держится, пока терминал открывается скрытым И scrollback не равен нулю
    // (при нуле аддон обнуляет scrollBarWidth). Начнут открывать видимым — переключение
    // вкладок станет перекладывать буферы всех N терминалов, и это заметят не сразу.
    term.open(element);
    attachRenderer(entry);
    installKeyHandler(entry);

    term.onData(function (data) {
      sendInput(id, encoder.encode(data));
    });

    // onBinary отдаёт «binary string» — её кодируют по кодам символов, а не через TextEncoder.
    term.onBinary(function (data) {
      var bytes = new Uint8Array(data.length);
      for (var i = 0; i < data.length; i++) {
        bytes[i] = data.charCodeAt(i) & 0xff;
      }
      sendInput(id, bytes);
    });

    terminals.set(id, entry);

    // Сначала ready: помпа в C# ставится именно на него, и resize, пришедший раньше,
    // отбросить некому.
    post({ type: 'ready', id: id });

    if (currentCols > 0) {
      // Размер страницы уже известен по другим вкладкам. Ставим его сразу, до первого байта
      // вывода: иначе оболочка успела бы напечатать приглашение по размеру по умолчанию,
      // и первый же ресайз переложил бы его заново.
      resizeTerminal(entry, currentCols, currentRows);
    }

    scheduleResize();
  }

  function showTerminal(id) {
    if (!terminals.has(id)) {
      // Неизвестный идентификатор скрыл бы на странице все терминалы разом.
      return;
    }

    terminals.forEach(function (entry) {
      entry.element.classList.toggle('hidden', entry.id !== id);
    });

    var target = terminals.get(id);
    requestAnimationFrame(function () {
      target.term.focus();
    });

    // Пересчёт после показа даёт те же cols/rows — размер у вкладок общий, — поэтому
    // переключение не перекладывает буфер и не шлёт ни одного resize. Замер нужен ради
    // самого первого показа: до него мерить было нечего, все вкладки были скрыты.
    scheduleResize();
  }

  function closeTerminal(id) {
    var entry = terminals.get(id);
    if (!entry) {
      return;
    }

    terminals.delete(id);

    releaseRenderer(entry);

    entry.term.dispose();

    if (entry.element.parentNode) {
      entry.element.parentNode.removeChild(entry.element);
    }
  }

  // Снимает аддон WebGL: рендер продолжается на DOM-рендерере xterm.
  //
  // Что именно происходит с контекстом GL: аддон 0.18 в dispose() убирает свои канвасы
  // и подписки, но контекст не теряет принудительно — WEBGL_lose_context он не трогает,
  // и Chromium забирает контекст своим сборщиком мусора, когда захочет. То есть это
  // не «вернуть контекст сразу», а «перестать за него держаться».
  // От лимита контекстов на страницу спасает не это, а onContextLoss на каждом терминале.
  function releaseRenderer(entry) {
    if (entry.webgl) {
      try {
        entry.webgl.dispose();
      } catch (error) {
        // Уже освобождён.
      }
      entry.webgl = null;
    }
  }

  function writeOutput(id, seq, b64) {
    var entry = terminals.get(id);

    if (!entry) {
      // Квитанция уходит в любом случае: C# уже поставил ожидание на эту пачку, и молчание
      // здесь навсегда остановило бы чтение из PTY по достижении MaxPendingWrites.
      post({ type: 'ack', id: id, seq: seq, bytes: 0 });
      return;
    }

    var bytes = decodeBase64(b64);
    entry.term.write(bytes, function () {
      // Колбэк term.write — единственный честный признак, что пачка записана.
      post({ type: 'ack', id: id, seq: seq, bytes: bytes.length });
    });
  }

  function notifyExited(id, code) {
    var entry = terminals.get(id);
    if (entry) {
      entry.term.write('\r\n[33m[процесс завершился с кодом ' + code + '][0m\r\n');

      // Вкладка остаётся на странице с кодом выхода (раздел 8 ТЗ), но вывода в ней больше
      // не будет — ускорение рендера ей ни к чему. Буфер остаётся виден: xterm переходит
      // на DOM-рендерер, как и при потере контекста. Освобождение контекста GL это не
      // гарантирует (см. releaseRenderer), только снимает с него нашу ссылку.
      releaseRenderer(entry);
    }
  }

  function showFatal(text) {
    var banner = document.createElement('div');
    banner.className = 'fatal';
    banner.textContent = text;
    host.appendChild(banner);
  }

  function handleMessage(raw) {
    var message;
    try {
      message = JSON.parse(raw);
    } catch (error) {
      return;
    }

    if (!message || typeof message.id !== 'string') {
      return;
    }

    switch (message.type) {
      case 'out':
        writeOutput(message.id, message.seq, message.b64);
        break;
      case 'create':
        createTerminal(message.id, message.title);
        break;
      case 'show':
        showTerminal(message.id);
        break;
      case 'close':
        closeTerminal(message.id);
        break;
      case 'exited':
        notifyExited(message.id, message.code);
        break;
      default:
        break;
    }
  }

  // Настройки обязаны прийти из C# скриптом, выполняемым до создания документа.
  // Если их нет, это ошибка сборки моста: работать на неизвестном дебаунсе нельзя,
  // но и молчать нельзя — пишем причину прямо в страницу, DevTools отключены.
  if (typeof SCROLLBACK !== 'number' || typeof RESIZE_DEBOUNCE_MS !== 'number') {
    showFatal('Настройки терминала не получены: window.__terminalConfig не задан. '
      + 'Страница открыта мимо моста приложения.');
    return;
  }

  // Наблюдатель один на всю страницу: размер у терминалов общий, и следить за каждым
  // по отдельности незачем — скрытые всё равно отдают нули.
  new ResizeObserver(scheduleResize).observe(host);

  // Приёмник, поставленный до навигации (AddScriptToExecuteOnDocumentCreated), успел собрать
  // сообщения, пришедшие до загрузки app.js. Забираем их и подключаемся сами.
  window.__shellOnMessage = handleMessage;

  var queued = window.__shellInbox || [];
  window.__shellInbox = null;
  for (var i = 0; i < queued.length; i++) {
    handleMessage(queued[i]);
  }
})();
