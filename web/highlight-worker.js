// Поток подсветки синтаксиса. hljs.highlight — один синхронный вызов на весь текст, продолжения
// у него нет: разбить работу на куски между кадрами можно было бы только нарезкой текста, а она
// ломает многострочные конструкции. Поэтому подсветка идёт здесь, а поток страницы, где рисуются
// терминалы, получает готовые токены.
//
// Запрос:  { job, lang, segments: [{ text, lines }] }  — сегменты подсвечиваются независимо.
//          { cancel: job }                              — результат больше не нужен.
// Ответ:   { job, lines, offsets, tokens, classes }     — буферы передаются, а не копируются;
//          { job, lines: 0, offsets: null }             — подсветить не удалось.
/* global importScripts, hljs, HighlightModel */
'use strict';

importScripts(
  'vendor/highlight.min.js',
  'vendor/hljs-powershell.min.js',
  'vendor/hljs-dockerfile.min.js',
  'highlight-model.js'
);

var H = HighlightModel;
var queue = [];
var cancelled = new Set();
var scheduled = false;

self.onmessage = function (event) {
  var data = event.data;
  if (!data) {
    return;
  }

  if (typeof data.cancel === 'number') {
    cancelled.add(data.cancel);
    return;
  }

  queue.push(data);
  schedule();
};

// Задания разбираются по одному через setTimeout: отмены, пришедшие, пока шла долгая
// подсветка, успевают дойти до того, как начнётся следующее (уже ненужное) задание.
function schedule() {
  if (scheduled) {
    return;
  }
  scheduled = true;
  setTimeout(pump, 0);
}

function pump() {
  scheduled = false;
  var job = queue.shift();
  if (!job) {
    cancelled.clear();
    return;
  }

  if (cancelled.has(job.job)) {
    cancelled.delete(job.job);
  } else {
    run(job);
  }

  if (queue.length > 0) {
    schedule();
  } else {
    cancelled.clear();
  }
}

function run(job) {
  var classes = [''];
  var parts = [];
  var counts = [];
  var any = false;
  var language = typeof job.lang === 'string' && hljs.getLanguage(job.lang) ? job.lang : null;
  var segments = Array.isArray(job.segments) ? job.segments : [];

  for (var i = 0; i < segments.length; i++) {
    var segment = segments[i];
    var count = segment && typeof segment.lines === 'number' ? segment.lines : 0;
    var part = null;

    if (language && count > 0 && typeof segment.text === 'string') {
      try {
        var html = hljs.highlight(segment.text, { language: language, ignoreIllegals: true }).value;
        part = H.refineTokens(H.splitHighlighted(html, classes, language), segment.text, classes, language);
        // Сверка: токены обязаны лечь ровно на строки текста, иначе раскраска съедет.
        if (part && (part.lines !== count || part.length !== segment.text.length)) {
          part = null;
        }
      } catch (e) {
        part = null;
      }
    }

    if (part) {
      any = true;
    }
    parts.push(part);
    counts.push(count);
  }

  if (!any) {
    self.postMessage({ job: job.job, lines: 0, offsets: null, tokens: null, classes: null });
    return;
  }

  var result = H.concatResults(parts, counts);
  self.postMessage({
    job: job.job,
    lines: result.lines,
    offsets: result.offsets,
    tokens: result.tokens,
    classes: classes
  }, [result.offsets.buffer, result.tokens.buffer]);
}
