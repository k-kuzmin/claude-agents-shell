// Чистая часть подсветки синтаксиса: выбор языка по имени файла, разбор HTML-выхода
// highlight.js в токены по строкам, стороны diff для подсветки. Ни DOM, ни hljs, ни моста —
// поэтому проверяется node-скриптом web/tools/highlight-model-test.mjs. Загружается и в поток
// подсветки (highlight-worker.js, importScripts), и на страницу (window.HighlightModel).
//
// Почему не построчно: блочный комментарий или многострочная строка, подсвеченные по строке,
// выглядят неверно. Текст подсвечивается целиком один раз, результат делится на строки —
// открытые на переводе строки span'ы закрываются и продолжаются на следующей строке.
(function (root) {
  'use strict';

  // Порог подсветки: сторона diff или файл крупнее — показываются без подсветки.
  // 1 МБ текста highlight.js разбирает за сотни миллисекунд даже в отдельном потоке.
  var MAX_CHARS = 1024 * 1024;
  var MAX_LINES = 20000;

  // Язык — только по расширению или имени файла, без автоопределения: highlightAuto
  // гоняет все грамматики и на коротком фрагменте часто ошибается.
  var BY_EXTENSION = {
    '.cs': 'csharp', '.csx': 'csharp',
    '.js': 'javascript', '.mjs': 'javascript', '.cjs': 'javascript', '.jsx': 'javascript',
    '.ts': 'typescript', '.tsx': 'typescript', '.mts': 'typescript', '.cts': 'typescript',
    '.json': 'json', '.jsonc': 'json', '.json5': 'json',
    '.xml': 'xml', '.xaml': 'xml', '.axaml': 'xml', '.html': 'xml', '.htm': 'xml', '.xhtml': 'xml',
    '.svg': 'xml', '.csproj': 'xml', '.vbproj': 'xml', '.fsproj': 'xml', '.props': 'xml',
    '.targets': 'xml', '.resx': 'xml', '.config': 'xml', '.nuspec': 'xml', '.manifest': 'xml',
    '.xsd': 'xml', '.xslt': 'xml', '.plist': 'xml',
    '.css': 'css', '.scss': 'scss', '.less': 'less',
    '.py': 'python', '.pyw': 'python', '.pyi': 'python',
    '.ps1': 'powershell', '.psm1': 'powershell', '.psd1': 'powershell',
    '.sh': 'bash', '.bash': 'bash', '.zsh': 'bash',
    '.md': 'markdown', '.markdown': 'markdown',
    '.yml': 'yaml', '.yaml': 'yaml',
    '.sql': 'sql',
    '.go': 'go',
    '.rs': 'rust',
    '.c': 'c', '.h': 'c',
    '.cpp': 'cpp', '.cc': 'cpp', '.cxx': 'cpp', '.hpp': 'cpp', '.hh': 'cpp', '.hxx': 'cpp', '.inl': 'cpp',
    '.java': 'java',
    '.kt': 'kotlin', '.kts': 'kotlin',
    '.swift': 'swift',
    '.rb': 'ruby',
    '.php': 'php',
    '.lua': 'lua',
    '.pl': 'perl', '.pm': 'perl',
    '.r': 'r',
    '.vb': 'vbnet',
    '.graphql': 'graphql', '.gql': 'graphql',
    '.ini': 'ini', '.toml': 'ini', '.cfg': 'ini', '.editorconfig': 'ini',
    '.diff': 'diff', '.patch': 'diff',
    '.mk': 'makefile',
    '.dockerfile': 'dockerfile'
  };

  // Имена без расширения (или где расширение не говорит о языке).
  var BY_NAME = {
    'makefile': 'makefile', 'gnumakefile': 'makefile',
    'dockerfile': 'dockerfile', 'containerfile': 'dockerfile',
    '.editorconfig': 'ini', '.gitconfig': 'ini',
    '.bashrc': 'bash', '.bash_profile': 'bash', '.zshrc': 'bash', '.profile': 'bash',
    'directory.build.props': 'xml', 'directory.build.targets': 'xml',
    'directory.packages.props': 'xml', 'nuget.config': 'xml'
  };

  // Язык файла или null — показывать без подсветки.
  function languageFor(path) {
    if (typeof path !== 'string' || path.length === 0) {
      return null;
    }

    var slash = Math.max(path.lastIndexOf('/'), path.lastIndexOf('\\'));
    var name = (slash >= 0 ? path.slice(slash + 1) : path).toLowerCase();

    if (Object.prototype.hasOwnProperty.call(BY_NAME, name)) {
      return BY_NAME[name];
    }

    var dot = name.lastIndexOf('.');
    if (dot < 0) {
      return null;
    }

    var ext = name.slice(dot);
    return Object.prototype.hasOwnProperty.call(BY_EXTENSION, ext) ? BY_EXTENSION[ext] : null;
  }

  // ----- области hljs → токены палитры Visual Studio (Dark) --------------------------------
  // Единственное место, где решается, каким токеном (tk-*) красится область hljs; цвета
  // токенов — переменные --tk-* в diff.css. Решение зависит от языка и предков области:
  // строка внутри тега XML — значение атрибута, заголовок внутри функции — имя метода и т. п.
  // null — область своего цвета не задаёт, текст наследует цвет ближайшего предка (как в DOM),
  // хотя строка рисуется плоским списком span'ов.
  //
  // Семантическая раскраска VS (тип в месте использования, поля против локальных) лексической
  // библиотеке недоступна: имя типа красится только там, где его помечает грамматика.

  var CSS_LANGUAGES = { css: 1, scss: 1, less: 1 };

  // Управляющие ключевые слова — в VS свой цвет. hljs их не отделяет, поэтому отделяются по тексту.
  var CONTROL_WORDS = new Set([
    'if', 'else', 'elif', 'for', 'foreach', 'while', 'do', 'return', 'switch', 'case', 'default',
    'break', 'continue', 'try', 'catch', 'except', 'finally', 'throw', 'raise', 'await', 'yield', 'goto'
  ]);

  // Языки, где имя вида IFoo — интерфейс по соглашению об именах (цвет интерфейса VS).
  var INTERFACE_LANGUAGES = { csharp: 1, typescript: 1, java: 1 };
  var INTERFACE_RE = /^I[A-Z][A-Za-z0-9_]*$/;

  var DOC_OPEN = '<span class="hljs-doctag">///';

  // parents — первые классы открытых предков (снаружи внутрь), parentToken — действующий
  // токен предка, after — позиция в html сразу за открывающим тегом (для заглядывания вперёд).
  function tokenFor(cls, parents, parentToken, language, html, after) {
    var parts = cls.split(' ');
    var scope = parts[0];
    var css = CSS_LANGUAGES[language] === 1;

    function has(name) {
      return parts.indexOf(name) > 0;
    }

    function inside(name) {
      return parents.indexOf(name) >= 0;
    }

    switch (scope) {
      case 'hljs-keyword':
        return inside('hljs-meta') ? 'tk-preproc' : 'tk-keyword';
      case 'hljs-literal':
        return 'tk-keyword';
      case 'hljs-built_in':
        return css ? 'tk-css-value' : 'tk-keyword';
      case 'hljs-type':
        return 'tk-type';
      case 'hljs-title':
        if (has('function_')) {
          return 'tk-function';
        }
        if (has('class_')) {
          return 'tk-type';
        }
        // C# и родня пишут голый title: в объявлении метода — имя метода, иначе — имя типа.
        return inside('hljs-function') ? 'tk-function' : 'tk-type';
      case 'hljs-string':
        if (inside('hljs-tag')) {
          return 'tk-attr-value';
        }
        return css ? 'tk-css-value' : 'tk-string';
      case 'hljs-regexp':
        return 'tk-string';
      case 'hljs-char':
        return has('escape_') ? 'tk-escape' : 'tk-string';
      case 'hljs-subst':
        return 'tk-escape';
      case 'hljs-number':
        return css ? 'tk-css-value' : 'tk-number';
      case 'hljs-symbol':
      case 'hljs-bullet':
        return 'tk-number';
      case 'hljs-comment':
      case 'hljs-quote':
        // XML-doc C#: комментарий начинается с тега «///».
        return html.startsWith(DOC_OPEN, after) ? 'tk-doc' : 'tk-comment';
      case 'hljs-doctag':
        return parentToken === 'tk-doc' ? 'tk-doctag' : null;
      case 'hljs-meta':
        // #region, #include, <?xml … ?>, <!DOCTYPE> — препроцессор; [Attr], @Decorator — атрибут.
        return html.charCodeAt(after) === 35 /* # */ || html.startsWith('&lt;', after)
          ? 'tk-preproc'
          : 'tk-attribute';
      case 'hljs-tag':
        return 'tk-delim';
      case 'hljs-name':
        return inside('hljs-tag') ? 'tk-tag' : 'tk-keyword';
      case 'hljs-attr':
        return inside('hljs-tag') ? 'tk-attr-name' : 'tk-key';
      case 'hljs-attribute':
        return css ? 'tk-css-prop' : 'tk-key';
      case 'hljs-property':
        return css ? 'tk-css-prop' : null;
      case 'hljs-selector-tag':
      case 'hljs-selector-id':
      case 'hljs-selector-class':
      case 'hljs-selector-attr':
      case 'hljs-selector-pseudo':
        return 'tk-selector';
      case 'hljs-variable':
        return has('language_') ? 'tk-keyword' : null;
      case 'hljs-template-tag':
        return 'tk-keyword';
      case 'hljs-section':
        return 'tk-section';
      case 'hljs-code':
      case 'hljs-formula':
        return 'tk-string';
      case 'hljs-link':
        return 'tk-link';
      case 'hljs-emphasis':
        return 'tk-emphasis';
      case 'hljs-strong':
        return 'tk-strong';
      case 'hljs-addition':
        return 'tk-addition';
      case 'hljs-deletion':
        return 'tk-deletion';
      default:
        // params, variable, property, operator, punctuation, function, class — обычный текст
        // или цвет предка.
        return null;
    }
  }

  // Уточнение по тексту, которого грамматика не различает: управляющие ключевые слова и
  // интерфейсы по соглашению об именах. text — ровно тот текст, что подсвечивался.
  function refineTokens(result, text, classes, language) {
    if (!result) {
      return result;
    }

    var keyword = classes.indexOf('tk-keyword');
    var type = classes.indexOf('tk-type');
    var interfaces = INTERFACE_LANGUAGES[language] === 1 && type > 0;
    if (keyword < 0 && !interfaces) {
      return result;
    }

    var control = -1;
    var iface = -1;
    var tokens = result.tokens;
    var position = 0;

    for (var line = 0; line < result.lines; line++) {
      var end = result.offsets[line + 1];
      for (var p = result.offsets[line]; p < end; p++) {
        var length = tokens[p * 2];
        var cls = tokens[(p * 2) + 1];

        if (cls === keyword && keyword > 0 && CONTROL_WORDS.has(text.substr(position, length))) {
          if (control < 0) {
            control = internClass(classes, 'tk-control');
          }
          tokens[(p * 2) + 1] = control;
        } else if (cls === type && interfaces && INTERFACE_RE.test(text.substr(position, length))) {
          if (iface < 0) {
            iface = internClass(classes, 'tk-interface');
          }
          tokens[(p * 2) + 1] = iface;
        }

        position += length;
      }
      position += 1; // \n
    }

    return result;
  }

  function internClass(classes, name) {
    var index = classes.indexOf(name);
    if (index < 0) {
      index = classes.length;
      classes.push(name);
    }
    return index;
  }

  // Класс span'а годится, только если это слова из букв, цифр, _ и -. hljs других не пишет;
  // что-то иное значит, что выход не тот, на который рассчитан разбор, — подсветку бросаем.
  var CLASS_RE = /^[A-Za-z0-9_\- ]+$/;

  var ENTITIES = { '&lt;': '<', '&gt;': '>', '&amp;': '&', '&quot;': '"', '&#x27;': "'", '&#39;': "'" };
  var SPAN_OPEN = '<span class="';

  // Разбор HTML-выхода highlight.js в токены по строкам. Понимает ровно то, что hljs пишет:
  // <span class="…">, </span>, текст и пять сущностей. Всё иное — null (показ без подсветки):
  // в DOM ничего из этого HTML не попадает, токены рисуются через textContent и className.
  //
  // Результат:
  //   lines   — число строк (переводов строки + 1);
  //   offsets — Int32Array(lines + 1): начало токенов строки i в tokens (в парах);
  //   tokens  — Int32Array пар [длина, класс]: длина в символах UTF-16 исходного текста,
  //             класс — индекс в classes (0 — без подсветки).
  //   length  — длина исходного текста, собранного из токенов (для сверки).
  // classes — общая таблица токенов (массив строк tk-*, classes[0] === ''), дополняется на месте.
  // language — язык подсветки (правила tokenFor зависят от него).
  function splitHighlighted(html, classes, language) {
    if (typeof html !== 'string') {
      return null;
    }

    var classIndex = new Map();
    for (var c = 0; c < classes.length; c++) {
      classIndex.set(classes[c], c);
    }
    if (classes.length === 0) {
      classes.push('');
      classIndex.set('', 0);
    }

    var tokens = [];
    var offsets = [0];
    var stack = [0];       // действующий токен на каждом уровне вложенности, [0] — вне span'ов
    var parents = [];      // первые классы hljs открытых span'ов, снаружи внутрь
    var runClass = 0;
    var runLength = 0;
    var total = 0;
    var i = 0;
    var n = html.length;

    function flush() {
      if (runLength > 0) {
        tokens.push(runLength, runClass);
        runLength = 0;
      }
    }

    function addText(count) {
      var current = stack[stack.length - 1];
      if (current !== runClass) {
        flush();
        runClass = current;
      }
      runLength += count;
      total += count;
    }

    while (i < n) {
      var ch = html.charCodeAt(i);

      if (ch === 60 /* < */) {
        if (html.startsWith(SPAN_OPEN, i)) {
          var close = html.indexOf('">', i + SPAN_OPEN.length);
          if (close < 0) {
            return null;
          }

          var cls = html.slice(i + SPAN_OPEN.length, close);
          if (!CLASS_RE.test(cls)) {
            return null;
          }

          var effective = stack[stack.length - 1];
          var token = tokenFor(cls, parents, classes[effective], language, html, close + 2);
          if (token !== null) {
            var index = classIndex.get(token);
            if (index === undefined) {
              index = classes.length;
              classes.push(token);
              classIndex.set(token, index);
            }
            effective = index;
          }

          stack.push(effective);
          parents.push(cls.split(' ', 1)[0]);
          i = close + 2;
          continue;
        }

        if (html.startsWith('</span>', i)) {
          if (stack.length <= 1) {
            return null;
          }
          stack.pop();
          parents.pop();
          i += 7;
          continue;
        }

        return null;
      }

      if (ch === 38 /* & */) {
        var semi = html.indexOf(';', i);
        var entity = semi > i ? html.slice(i, semi + 1) : '';
        if (!Object.prototype.hasOwnProperty.call(ENTITIES, entity)) {
          return null;
        }
        addText(1);
        i = semi + 1;
        continue;
      }

      if (ch === 10 /* \n */) {
        // Строка кончилась: токены закрываются, открытые span'ы продолжатся на следующей.
        flush();
        runClass = stack[stack.length - 1];
        offsets.push(tokens.length >> 1);
        total += 1;
        i++;
        continue;
      }

      // Обычный текст до ближайшего тега, сущности или перевода строки.
      var j = i + 1;
      while (j < n) {
        var cj = html.charCodeAt(j);
        if (cj === 60 || cj === 38 || cj === 10) {
          break;
        }
        j++;
      }
      addText(j - i);
      i = j;
    }

    if (stack.length !== 1) {
      return null;
    }

    flush();
    offsets.push(tokens.length >> 1);

    return {
      lines: offsets.length - 1,
      offsets: Int32Array.from(offsets),
      tokens: Int32Array.from(tokens),
      length: total
    };
  }

  // Строки текста как их показывает панель: по \n, \r в конце строки отбрасывается,
  // хвост после завершающего \n — не строка. Пустой текст — ноль строк.
  function splitText(text) {
    if (typeof text !== 'string' || text.length === 0) {
      return [];
    }

    var lines = text.split('\n');
    if (lines[lines.length - 1] === '') {
      lines.pop();
    }

    for (var i = 0; i < lines.length; i++) {
      var line = lines[i];
      if (line.length > 0 && line.charCodeAt(line.length - 1) === 13) {
        lines[i] = line.slice(0, -1);
      }
    }

    return lines;
  }

  // Строки → один текст для подсветки. Число строк результата разбора обязано совпасть
  // с lines.length, иначе токены съедут со строк.
  function joinLines(lines) {
    return lines.join('\n');
  }

  // Стороны diff для подсветки: по фрагменту (@@) на сторону. Старая сторона — контекст и
  // удалённые строки, новая — контекст и добавленные. Удалённая строка красится по старой
  // стороне, добавленная и контекстная — по новой.
  // В режиме «только изменения» фрагмент — это не весь файл: конструкция, начатая выше
  // фрагмента (комментарий, строка), подсвечивается неверно. Это принятая неточность;
  // в режиме «весь файл» фрагмент один и покрывает файл целиком.
  //
  // rows — строки parseUnified. Возвращает:
  //   segments — массив { lines: [строки] }, по порядку: фрагмент 0 старая, фрагмент 0 новая, …;
  //   rowLine  — Int32Array(rows.length): номер строки подсветки (сквозной по всем сегментам)
  //              для каждой строки diff или -1 — не подсвечивается (заголовки, служебные);
  //   oldChars, newChars, oldLines, newLines — размеры сторон для порога.
  function diffSides(rows, kinds) {
    var ctx = kinds.context;
    var add = kinds.add;
    var del = kinds.del;
    var hunk = kinds.hunk;

    var segments = [];
    var rowLine = new Int32Array(rows.length).fill(-1);
    var oldLines = null;
    var newLines = null;
    var oldRows = null;
    var newRows = null;
    var sizes = { oldChars: 0, newChars: 0, oldLines: 0, newLines: 0 };

    function close() {
      if (oldLines === null) {
        return;
      }
      segments.push({ lines: oldLines, rows: oldRows });
      segments.push({ lines: newLines, rows: newRows });
      oldLines = newLines = oldRows = newRows = null;
    }

    function open() {
      oldLines = [];
      newLines = [];
      oldRows = [];
      newRows = [];
    }

    for (var r = 0; r < rows.length; r++) {
      var row = rows[r];
      var t = row.t;

      if (t === hunk) {
        close();
        open();
        continue;
      }

      if (t !== ctx && t !== add && t !== del) {
        continue;
      }

      if (oldLines === null) {
        open();
      }

      if (t === ctx || t === del) {
        oldLines.push(row.s);
        oldRows.push(t === del ? r : -1);
        sizes.oldChars += row.s.length + 1;
        sizes.oldLines++;
      }

      if (t === ctx || t === add) {
        newLines.push(row.s);
        newRows.push(r);
        sizes.newChars += row.s.length + 1;
        sizes.newLines++;
      }
    }
    close();

    // Сквозная нумерация строк подсветки и привязка строк diff к ним.
    var global = 0;
    for (var s = 0; s < segments.length; s++) {
      var seg = segments[s];
      for (var k = 0; k < seg.rows.length; k++) {
        if (seg.rows[k] >= 0) {
          rowLine[seg.rows[k]] = global + k;
        }
      }
      global += seg.lines.length;
      seg.rows = null;
    }

    return {
      segments: segments,
      rowLine: rowLine,
      oldChars: sizes.oldChars,
      newChars: sizes.newChars,
      oldLines: sizes.oldLines,
      newLines: sizes.newLines
    };
  }

  // Подсвечивать ли текст такого размера.
  function withinLimit(chars, lines) {
    return chars <= MAX_CHARS && lines <= MAX_LINES;
  }

  // Склейка результатов по сегментам в одну сквозную таблицу строк. parts — результаты
  // splitHighlighted по порядку сегментов; null в части — сегмент без подсветки (его строки
  // получают пустые токены, то есть рисуются простым текстом).
  function concatResults(parts, lineCounts) {
    var totalLines = 0;
    var totalTokens = 0;
    for (var i = 0; i < parts.length; i++) {
      totalLines += lineCounts[i];
      totalTokens += parts[i] ? parts[i].tokens.length : 0;
    }

    var offsets = new Int32Array(totalLines + 1);
    var tokens = new Int32Array(totalTokens);
    var line = 0;
    var pair = 0;

    for (var p = 0; p < parts.length; p++) {
      var part = parts[p];
      var count = lineCounts[p];
      if (!part) {
        for (var e = 0; e < count; e++) {
          offsets[line++] = pair;
        }
        continue;
      }

      tokens.set(part.tokens, pair * 2);
      for (var l = 0; l < count; l++) {
        offsets[line++] = pair + part.offsets[l];
      }
      pair += part.tokens.length >> 1;
    }
    offsets[line] = pair;

    return { lines: totalLines, offsets: offsets, tokens: tokens };
  }

  // Токены строки line, обрезанные по limit символов: [длина, класс, …] как обычный массив.
  // Обрезка совпадает с clipLine: показывается ровно столько символов, сколько в clipLine.
  function lineTokens(result, line, limit) {
    var out = [];
    if (!result || line < 0 || line >= result.lines) {
      return out;
    }

    var left = typeof limit === 'number' ? limit : Infinity;
    var end = result.offsets[line + 1];
    for (var p = result.offsets[line]; p < end && left > 0; p++) {
      var length = Math.min(result.tokens[p * 2], left);
      out.push(length, result.tokens[(p * 2) + 1]);
      left -= length;
    }

    return out;
  }

  var api = {
    MAX_CHARS: MAX_CHARS,
    MAX_LINES: MAX_LINES,
    languageFor: languageFor,
    splitHighlighted: splitHighlighted,
    refineTokens: refineTokens,
    splitText: splitText,
    joinLines: joinLines,
    diffSides: diffSides,
    withinLimit: withinLimit,
    concatResults: concatResults,
    lineTokens: lineTokens
  };

  if (typeof module === 'object' && module.exports) {
    module.exports = api;
  } else {
    root.HighlightModel = api;
  }
})(typeof self !== 'undefined' ? self : this);
