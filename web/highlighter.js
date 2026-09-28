// Подсветка синтаксиса со стороны страницы: один поток подсветки (highlight-worker.js) на всю
// страницу, создаётся при первой надобности. Страница сразу рисует текст без подсветки и
// перерисовывает строки, когда приходят токены. Если поток не поднялся (нет Worker, ошибка
// загрузки), подсветка выключается до перезагрузки страницы — текст остаётся как есть.
// На странице доступна как window.Highlighter.
(function (root) {
  'use strict';

  var H = root.HighlightModel;
  var worker = null;
  var broken = !H || typeof root.Worker !== 'function';
  var nextJob = 1;
  var waiting = new Map();

  function fail() {
    broken = true;
    if (worker) {
      try { worker.terminate(); } catch (e) { /* уже остановлен */ }
      worker = null;
    }
    // Ждущим не отвечаем: без токенов они и так показывают простой текст.
    waiting.clear();
  }

  function ensureWorker() {
    if (worker || broken) {
      return worker;
    }

    try {
      worker = new root.Worker('highlight-worker.js');
    } catch (e) {
      fail();
      return null;
    }

    worker.onmessage = function (event) {
      var data = event.data;
      var callback = data ? waiting.get(data.job) : undefined;
      if (!callback) {
        return;
      }

      waiting.delete(data.job);
      if (data.offsets && data.tokens && Array.isArray(data.classes)) {
        callback({ lines: data.lines, offsets: data.offsets, tokens: data.tokens, classes: data.classes });
      }
    };
    worker.onerror = function (event) {
      if (event && typeof event.preventDefault === 'function') {
        event.preventDefault();
      }
      fail();
    };
    return worker;
  }

  // Ставит подсветку сегментов (массивов строк) языком lang. Возвращает номер задания
  // (0 — подсветки не будет: язык не поддержан, порог, поток не работает).
  // callback({ lines, offsets, tokens, classes }) зовётся только при успехе; строки
  // сегментов в результате идут сквозной нумерацией по порядку.
  function request(lang, segments, callback) {
    if (!lang || broken || !Array.isArray(segments) || segments.length === 0) {
      return 0;
    }

    var target = ensureWorker();
    if (!target) {
      return 0;
    }

    var payload = new Array(segments.length);
    for (var i = 0; i < segments.length; i++) {
      payload[i] = { text: H.joinLines(segments[i]), lines: segments[i].length };
    }

    var job = nextJob++;
    waiting.set(job, callback);
    try {
      target.postMessage({ job: job, lang: lang, segments: payload });
    } catch (e) {
      waiting.delete(job);
      return 0;
    }
    return job;
  }

  // Результат задания больше не нужен: свернули файл, закрыли панель, пришёл новый набор.
  function cancel(job) {
    if (!job || !waiting.has(job)) {
      return;
    }

    waiting.delete(job);
    if (worker) {
      worker.postMessage({ cancel: job });
    }
  }

  // Рисует строку кода в node: текст line (уже обрезанный clipLine) и токены из result.
  // Только textContent и className — ни одного разбора HTML.
  function renderCode(node, text, result, line) {
    if (!result || line < 0) {
      node.textContent = text;
      return;
    }

    var tokens = H.lineTokens(result, line, text.length);
    var position = 0;
    for (var i = 0; i < tokens.length; i += 2) {
      var length = tokens[i];
      var cls = tokens[i + 1];
      var piece = text.substr(position, length);
      position += length;

      if (cls === 0) {
        node.appendChild(document.createTextNode(piece));
      } else {
        var span = document.createElement('span');
        span.className = result.classes[cls] || '';
        span.textContent = piece;
        node.appendChild(span);
      }
    }

    if (position < text.length) {
      node.appendChild(document.createTextNode(text.slice(position)));
    }
  }

  root.Highlighter = {
    request: request,
    cancel: cancel,
    renderCode: renderCode,
    languageFor: H ? H.languageFor : function () { return null; }
  };
})(window);
