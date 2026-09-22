// Чистая модель панели diff: разбор unified diff, фильтр по расширению, бюджет автораскрытия,
// раскладка строк для виртуализации. Ни DOM, ни моста — поэтому проверяется node-скриптом
// web/tools/diff-model-test.mjs без GUI. На странице доступна как window.DiffModel.
(function (root) {
  'use strict';

  // Стартовые числа issue #5, уточняются на приёмке.
  var AUTO_MAX_LINES = 2000;
  var AUTO_MAX_FILES = 50;
  var LONG_LINE = 2000;

  // «Показать целиком»: потолок символов в окне строки. Мегабайтный <pre> — сотни мс раскладки.
  var OVERLAY_LINE = 256 * 1024;

  // Содержимое файла длиннее этого (в символах diff) при скрытии вкладки выгружается,
  // остальное остаётся, чтобы переключение вкладок не перезапрашивало файлы.
  var HIDDEN_KEEP_CHARS = 512 * 1024;

  // Виды строк разобранного diff.
  var ROW_HUNK = 'h';    // @@ -a,b +c,d @@
  var ROW_CONTEXT = 'c'; // ' '
  var ROW_ADD = 'a';     // '+'
  var ROW_DEL = 'd';     // '-'
  var ROW_META = 'm';    // new file mode, Binary files differ, rename from …
  var ROW_NOEOL = 'e';   // \ No newline at end of file

  var HUNK_RE = /^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@/;

  // Строки заголовка, которые панель не показывает: путь и так в шапке файла.
  function isSkippedHeader(line) {
    return line.lastIndexOf('diff --git ', 0) === 0
      || line.lastIndexOf('index ', 0) === 0
      || line.lastIndexOf('--- ', 0) === 0
      || line.lastIndexOf('+++ ', 0) === 0;
  }

  // Разбор unified diff одного файла в плоский список строк с номерами.
  // Внутри фрагмента строки считаются по счётчикам из @@: иначе удалённая строка «-- x»
  // выглядела бы как заголовок «--- x». Счётчики в @@ необязательны (по умолчанию 1).
  // Пустая строка внутри фрагмента — контекст: некоторые инструменты срезают пробел.
  function parseUnified(text) {
    var rows = [];
    var maxLen = 0;

    if (typeof text !== 'string' || text.length === 0) {
      return { rows: rows, maxLen: 0 };
    }

    var lines = text.split('\n');
    var count = lines.length;
    if (lines[count - 1] === '') {
      // Хвост после завершающего \n — не строка.
      count--;
    }

    var oldNo = 0;
    var newNo = 0;
    var oldLeft = 0;
    var newLeft = 0;
    var seenHunk = false;

    for (var i = 0; i < count; i++) {
      var line = lines[i];
      if (line.length > 0 && line.charCodeAt(line.length - 1) === 13) {
        line = line.slice(0, -1);
      }

      var inHunk = oldLeft > 0 || newLeft > 0;

      if (!inHunk) {
        var header = HUNK_RE.exec(line);
        if (header) {
          oldNo = parseInt(header[1], 10);
          oldLeft = header[2] === undefined ? 1 : parseInt(header[2], 10);
          newNo = parseInt(header[3], 10);
          newLeft = header[4] === undefined ? 1 : parseInt(header[4], 10);
          seenHunk = true;
          rows.push({ t: ROW_HUNK, o: 0, n: 0, s: line });
          continue;
        }

        if (line.charCodeAt(0) === 92 /* \ */ && seenHunk) {
          rows.push({ t: ROW_NOEOL, o: 0, n: 0, s: line });
          continue;
        }

        if (line.length === 0 || isSkippedHeader(line)) {
          continue;
        }

        rows.push({ t: ROW_META, o: 0, n: 0, s: line });
        maxLen = Math.max(maxLen, line.length);
        continue;
      }

      var mark = line.length === 0 ? ' ' : line.charAt(0);
      var body = line.length === 0 ? '' : line.slice(1);

      if (mark === '+' && newLeft > 0) {
        rows.push({ t: ROW_ADD, o: 0, n: newNo++, s: body });
        newLeft--;
      } else if (mark === '-' && oldLeft > 0) {
        rows.push({ t: ROW_DEL, o: oldNo++, n: 0, s: body });
        oldLeft--;
      } else if (mark === ' ') {
        rows.push({ t: ROW_CONTEXT, o: oldNo++, n: newNo++, s: body });
        if (oldLeft > 0) { oldLeft--; }
        if (newLeft > 0) { newLeft--; }
      } else if (mark === '\\') {
        rows.push({ t: ROW_NOEOL, o: 0, n: 0, s: line });
        continue;
      } else {
        // Счётчики и содержимое разошлись — показываем как есть и выходим из фрагмента.
        rows.push({ t: ROW_META, o: 0, n: 0, s: line });
        oldLeft = 0;
        newLeft = 0;
      }

      maxLen = Math.max(maxLen, body.length);
    }

    return { rows: rows, maxLen: maxLen };
  }

  // Расширение для фильтра: «.cs», «.meta»; без расширения — ''. Регистр не важен.
  // Точка в начале имени (.gitignore) — не расширение.
  function extensionOf(path) {
    var slash = path.lastIndexOf('/');
    var name = slash >= 0 ? path.slice(slash + 1) : path;
    var dot = name.lastIndexOf('.');
    return dot > 0 ? name.slice(dot).toLowerCase() : '';
  }

  // Счётчики по всему оглавлению, как «File filter» на GitHub: самые частые сверху.
  function extensionCounts(files) {
    var counts = new Map();
    for (var i = 0; i < files.length; i++) {
      var ext = extensionOf(files[i].p);
      counts.set(ext, (counts.get(ext) || 0) + 1);
    }

    var list = [];
    counts.forEach(function (count, ext) {
      list.push({ ext: ext, count: count });
    });
    list.sort(function (a, b) {
      return b.count - a.count || (a.ext < b.ext ? -1 : a.ext > b.ext ? 1 : 0);
    });
    return list;
  }

  // Убирает файлы снятых расширений. Этот набор и идёт в бюджет автораскрытия.
  function filterByExtension(files, excluded) {
    if (!excluded || excluded.size === 0) {
      return files.slice();
    }

    return files.filter(function (file) {
      return !excluded.has(extensionOf(file.p));
    });
  }

  // Фильтр по пути — подстрока без учёта регистра, по новому и прежнему пути.
  // Только для показа: в бюджет не входит, иначе каждое нажатие клавиши слало бы запросы.
  function filterByPath(files, query) {
    var needle = (query || '').trim().toLowerCase();
    if (needle.length === 0) {
      return files;
    }

    return files.filter(function (file) {
      return file.p.toLowerCase().indexOf(needle) >= 0
        || (typeof file.o === 'string' && file.o.toLowerCase().indexOf(needle) >= 0);
    });
  }

  // Бюджет автораскрытия по видимому (после фильтра по расширению) набору, по порядку:
  // - файлы из expand раскрываются всегда и бюджет не тратят;
  // - large/generated/binary не раскрываются, но и не останавливают обход;
  // - файл c:none с неизвестным числом строк не раскрывается и обход не останавливает;
  // - первый c:none, который не влезает (строк > maxLines или файлов > maxFiles), останавливает
  //   автораскрытие: «пока суммарно ≤ 2000 строк и ≤ 50 файлов».
  // Возвращает множество путей к раскрытию.
  function autoExpand(files, expand, limits) {
    var maxLines = limits && typeof limits.maxLines === 'number' ? limits.maxLines : AUTO_MAX_LINES;
    var maxFiles = limits && typeof limits.maxFiles === 'number' ? limits.maxFiles : AUTO_MAX_FILES;
    var result = new Set();
    var lines = 0;
    var taken = 0;
    var open = true;

    for (var i = 0; i < files.length; i++) {
      var file = files[i];

      if (expand && expand.has(file.p) && file.c !== 'binary') {
        result.add(file.p);
        continue;
      }

      if (!open || file.c !== 'none') {
        continue;
      }

      if (typeof file.a !== 'number' || typeof file.d !== 'number') {
        continue;
      }

      var size = file.a + file.d;
      if (lines + size > maxLines || taken + 1 > maxFiles) {
        open = false;
        continue;
      }

      lines += size;
      taken++;
      result.add(file.p);
    }

    return result;
  }

  // Обрезка длинной строки для показа: строка фиксированной высоты не переносится,
  // а мегабайтная строка minified-кода убила бы раскладку.
  function clipLine(text, max) {
    var limit = typeof max === 'number' ? max : LONG_LINE;
    if (text.length <= limit) {
      return { text: text, cut: false };
    }

    return { text: text.slice(0, limit), cut: true };
  }

  // Оставить ли загруженное содержимое файла, когда вкладку скрыли.
  function keepWhenHidden(textLength) {
    return typeof textLength === 'number' && textLength <= HIDDEN_KEEP_CHARS;
  }

  // Раскладка для виртуализации: блоки подряд идущих строк (по блоку на файл).
  // starts[i] — номер первой строки блока i, total — всего строк.
  function buildLayout(sizes) {
    var starts = new Array(sizes.length);
    var total = 0;
    for (var i = 0; i < sizes.length; i++) {
      starts[i] = total;
      total += sizes[i];
    }

    return { starts: starts, total: total };
  }

  // Блок, которому принадлежит строка index: бинарный поиск по starts.
  function locate(layout, index) {
    var starts = layout.starts;
    var lo = 0;
    var hi = starts.length - 1;

    if (hi < 0 || index < 0 || index >= layout.total) {
      return -1;
    }

    while (lo < hi) {
      var mid = (lo + hi + 1) >> 1;
      if (starts[mid] <= index) {
        lo = mid;
      } else {
        hi = mid - 1;
      }
    }

    return lo;
  }

  // Окно отрисовки виртуального списка с запасом по краям.
  function visibleRange(scrollTop, viewportHeight, rowHeight, total, overscan) {
    if (total <= 0 || rowHeight <= 0) {
      return { first: 0, last: 0 };
    }

    var first = Math.max(0, Math.floor(scrollTop / rowHeight) - overscan);
    var last = Math.min(total, Math.ceil((scrollTop + viewportHeight) / rowHeight) + overscan);
    return { first: first, last: Math.max(first, last) };
  }

  var api = {
    AUTO_MAX_LINES: AUTO_MAX_LINES,
    AUTO_MAX_FILES: AUTO_MAX_FILES,
    LONG_LINE: LONG_LINE,
    OVERLAY_LINE: OVERLAY_LINE,
    HIDDEN_KEEP_CHARS: HIDDEN_KEEP_CHARS,
    keepWhenHidden: keepWhenHidden,
    ROW_HUNK: ROW_HUNK,
    ROW_CONTEXT: ROW_CONTEXT,
    ROW_ADD: ROW_ADD,
    ROW_DEL: ROW_DEL,
    ROW_META: ROW_META,
    ROW_NOEOL: ROW_NOEOL,
    parseUnified: parseUnified,
    extensionOf: extensionOf,
    extensionCounts: extensionCounts,
    filterByExtension: filterByExtension,
    filterByPath: filterByPath,
    autoExpand: autoExpand,
    clipLine: clipLine,
    buildLayout: buildLayout,
    locate: locate,
    visibleRange: visibleRange
  };

  if (typeof module === 'object' && module.exports) {
    module.exports = api;
  } else {
    root.DiffModel = api;
  }
})(typeof window !== 'undefined' ? window : this);
