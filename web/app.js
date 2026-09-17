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

  function post(message) {
    window.chrome.webview.postMessage(JSON.stringify(message));
  }

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

  // У скрытых терминалов fit() не вызывается: нулевые размеры дали бы мусорный resize.
  function fitAndReport(entry) {
    if (!isVisible(entry.element)) {
      return;
    }

    try {
      entry.fit.fit();
    } catch (error) {
      return;
    }

    var cols = entry.term.cols;
    var rows = entry.term.rows;
    if (cols > 0 && rows > 0 && (cols !== entry.cols || rows !== entry.rows)) {
      entry.cols = cols;
      entry.rows = rows;
      post({ type: 'resize', id: entry.id, cols: cols, rows: rows });
    }
  }

  function scheduleFit(entry) {
    if (entry.resizeTimer !== 0) {
      clearTimeout(entry.resizeTimer);
    }

    entry.resizeTimer = setTimeout(function () {
      entry.resizeTimer = 0;
      fitAndReport(entry);
    }, RESIZE_DEBOUNCE_MS);
  }

  function attachRenderer(entry) {
    if (typeof WebglAddon === 'undefined') {
      return;
    }

    try {
      var webgl = new WebglAddon.WebglAddon();
      webgl.onContextLoss(function () {
        // Контекст потерян — снимаем аддон, рендер продолжается на canvas.
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
      // Нет WebGL2 — остаёмся на canvas-рендерере, это штатный откат.
      entry.webgl = null;
    }
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
      observer: null,
      resizeTimer: 0,
      cols: 0,
      rows: 0
    };

    term.open(element);
    attachRenderer(entry);

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

    entry.observer = new ResizeObserver(function () {
      scheduleFit(entry);
    });
    entry.observer.observe(element);

    terminals.set(id, entry);

    // ready → fit() → resize с реальными размерами.
    post({ type: 'ready', id: id });
    requestAnimationFrame(function () {
      fitAndReport(entry);
    });
  }

  function showTerminal(id) {
    terminals.forEach(function (entry) {
      var visible = entry.id === id;
      entry.element.classList.toggle('hidden', !visible);
    });

    var target = terminals.get(id);
    if (target) {
      requestAnimationFrame(function () {
        fitAndReport(target);
        target.term.focus();
      });
    }
  }

  function closeTerminal(id) {
    var entry = terminals.get(id);
    if (!entry) {
      return;
    }

    terminals.delete(id);

    if (entry.resizeTimer !== 0) {
      clearTimeout(entry.resizeTimer);
      entry.resizeTimer = 0;
    }

    if (entry.observer) {
      entry.observer.disconnect();
      entry.observer = null;
    }

    if (entry.webgl) {
      try {
        entry.webgl.dispose();
      } catch (error) {
        // Уже освобождён.
      }
      entry.webgl = null;
    }

    entry.term.dispose();

    if (entry.element.parentNode) {
      entry.element.parentNode.removeChild(entry.element);
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

  // Приёмник, поставленный до навигации (AddScriptToExecuteOnDocumentCreated), успел собрать
  // сообщения, пришедшие до загрузки app.js. Забираем их и подключаемся сами.
  window.__shellOnMessage = handleMessage;

  var queued = window.__shellInbox || [];
  window.__shellInbox = null;
  for (var i = 0; i < queued.length; i++) {
    handleMessage(queued[i]);
  }
})();
