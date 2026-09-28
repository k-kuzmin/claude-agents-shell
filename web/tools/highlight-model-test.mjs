// Проверка чистых частей подсветки и режима «файл» без GUI: node web/tools/highlight-model-test.mjs
// Настоящий highlight.js берётся из node_modules (npm install в web/), как его кладёт vendor.mjs.

import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const H = require('../highlight-model.js');
const M = require('../diff-model.js');
const hljs = require('../node_modules/@highlightjs/cdn-assets/highlight.min.js');

const tests = [];
const test = (name, body) => tests.push([name, body]);

// Строки результата как [[текст, класс], …] — для сравнения глазами.
function render(result, classes, lines) {
  return lines.map((text, index) => {
    const tokens = H.lineTokens(result, index);
    const out = [];
    let at = 0;
    for (let i = 0; i < tokens.length; i += 2) {
      out.push([text.substr(at, tokens[i]), classes[tokens[i + 1]]]);
      at += tokens[i];
    }
    return out;
  });
}

function highlight(text, language) {
  const classes = [''];
  const html = hljs.highlight(text, { language, ignoreIllegals: true }).value;
  // Ровно то, что делает поток подсветки.
  return { result: H.refineTokens(H.splitHighlighted(html, classes, language), text, classes, language), classes };
}

// Токены, которыми покрашен каждый кусок текста: { 'текст': 'tk-…' } по первому вхождению.
function paint(code, language) {
  const lines = code.split('\n');
  const { result, classes } = highlight(code, language);
  assert.ok(result, language);
  const out = {};
  for (const row of render(result, classes, lines)) {
    for (const [text, cls] of row) {
      const key = text.trim();
      if (key && !(key in out)) {
        out[key] = cls;
      }
    }
  }
  return out;
}

// ----- выбор языка -----------------------------------------------------------------------

test('язык по расширению, без учёта регистра', () => {
  assert.equal(H.languageFor('src/App/MainWindow.xaml.cs'), 'csharp');
  assert.equal(H.languageFor('web/app.JS'), 'javascript');
  assert.equal(H.languageFor('a/b.tsx'), 'typescript');
  assert.equal(H.languageFor('x.json'), 'json');
  assert.equal(H.languageFor('Views/TabStrip.xaml'), 'xml');
  assert.equal(H.languageFor('src/A/A.csproj'), 'xml');
  assert.equal(H.languageFor('build.ps1'), 'powershell');
  assert.equal(H.languageFor('run.sh'), 'bash');
  assert.equal(H.languageFor('docs/TZ.md'), 'markdown');
  assert.equal(H.languageFor('.github/workflows/ci.yml'), 'yaml');
  assert.equal(H.languageFor('q.sql'), 'sql');
  assert.equal(H.languageFor('m.go'), 'go');
  assert.equal(H.languageFor('lib.rs'), 'rust');
  assert.equal(H.languageFor('a.hpp'), 'cpp');
  assert.equal(H.languageFor('A.java'), 'java');
  assert.equal(H.languageFor('s.py'), 'python');
  assert.equal(H.languageFor('diff.css'), 'css');
});

test('язык по имени файла и Windows-разделителям', () => {
  assert.equal(H.languageFor('Makefile'), 'makefile');
  assert.equal(H.languageFor('docker/Dockerfile'), 'dockerfile');
  assert.equal(H.languageFor('C:\\r\\.editorconfig'), 'ini');
  assert.equal(H.languageFor('Directory.Build.props'), 'xml');
});

test('неизвестное и без расширения — без подсветки (автоопределения нет)', () => {
  assert.equal(H.languageFor('LICENSE'), null);
  assert.equal(H.languageFor('.gitignore'), null);
  assert.equal(H.languageFor('a.unknownext'), null);
  assert.equal(H.languageFor(''), null);
  assert.equal(H.languageFor(null), null);
});

test('каждый выбираемый язык есть в сборке (общий набор + powershell + dockerfile)', () => {
  // Отдельные грамматики регистрируются в глобальный hljs — как после importScripts в потоке.
  globalThis.hljs = hljs;
  require('../node_modules/@highlightjs/cdn-assets/languages/powershell.min.js');
  require('../node_modules/@highlightjs/cdn-assets/languages/dockerfile.min.js');
  const paths = ['a.cs', 'a.js', 'a.ts', 'a.json', 'a.xml', 'a.css', 'a.scss', 'a.less', 'a.py', 'a.ps1',
    'a.sh', 'a.md', 'a.yml', 'a.sql', 'a.go', 'a.rs', 'a.c', 'a.cpp', 'a.java', 'a.kt', 'a.swift', 'a.rb',
    'a.php', 'a.lua', 'a.pl', 'a.r', 'a.vb', 'a.graphql', 'a.ini', 'a.diff', 'Makefile', 'Dockerfile'];
  for (const path of paths) {
    const language = H.languageFor(path);
    assert.ok(language, path);
    assert.ok(hljs.getLanguage(language), `${path} → ${language}`);
  }
});

// ----- разбор HTML highlight.js ------------------------------------------------------------

test('span через перевод строки закрывается и продолжается на следующей строке', () => {
  const classes = [''];
  const html = 'a <span class="hljs-comment">/* x\ny */</span> b';
  const result = H.splitHighlighted(html, classes);
  assert.equal(result.lines, 2);
  assert.deepEqual(render(result, classes, ['a /* x', 'y */ b']), [
    [['a ', ''], ['/* x', 'tk-comment']],
    [['y */', 'tk-comment'], [' b', '']]
  ]);
  assert.equal(result.length, 'a /* x\ny */ b'.length);
});

test('сущности раскрываются в один символ, длины совпадают с исходным текстом', () => {
  const classes = [''];
  const html = '<span class="hljs-string">&quot;a&lt;b&gt;&amp;&#x27;&quot;</span>';
  const result = H.splitHighlighted(html, classes);
  assert.equal(result.length, '"a<b>&\'"'.length);
  assert.deepEqual(render(result, classes, ['"a<b>&\'"']), [[['"a<b>&\'"', 'tk-string']]]);
});

test('цвет — от ближайшего стилизованного span, нестилизованный наследует внешний', () => {
  const classes = [''];
  const html = '<span class="hljs-string">`a<span class="hljs-subst">${x}</span>b`</span>'
    + '<span class="hljs-function"><span class="hljs-title function_">f</span>(x)</span>';
  const result = H.splitHighlighted(html, classes);
  assert.deepEqual(render(result, classes, ['`a${x}b`f(x)']), [[
    ['`a', 'tk-string'], ['${x}', 'tk-escape'], ['b`', 'tk-string'],
    ['f', 'tk-function'], ['(x)', '']
  ]]);
});

test('смежные куски одного токена склеиваются в один узел', () => {
  const classes = [''];
  const html = '<span class="hljs-keyword">a</span><span class="hljs-keyword">b</span>'
    + '<span class="hljs-params">c</span>d';
  const result = H.splitHighlighted(html, classes, 'javascript');
  // «c» в params без своего токена — простой текст, как и «d»: один токен на два куска.
  assert.deepEqual(Array.from(result.tokens), [2, 1, 2, 0]);
});

// ----- палитра Visual Studio (Dark) ------------------------------------------------------------

test('VS: C# — ключевые, управляющие, типы, интерфейсы, методы, строки, doc, препроцессор, атрибуты', () => {
  const t = paint([
    '#region Поля',
    '/// <summary>Документация</summary>',
    '[Obsolete("x")]',
    'public sealed class Foo : IDisposable, Bar',
    '{',
    '    private readonly int _x = 42;',
    '    public async Task<string> Run(string name)',
    '    {',
    '        if (name == null) return $"a{name}";',
    '        foreach (var c in name) { await Task.Yield(); }',
    '        // обычный',
    '        return "s";',
    '    }',
    '}',
    '#endregion'
  ].join('\n'), 'csharp');

  assert.equal(t['#region Поля'], 'tk-preproc', 'смежные куски препроцессора склеены');
  assert.equal(t['///'], 'tk-doctag');
  assert.equal(t['<summary>'], 'tk-doctag');
  assert.equal(t['Документация'], 'tk-doc');
  assert.equal(t['Obsolete('], 'tk-attribute');
  assert.equal(t['"x"'], 'tk-string');
  assert.equal(t['public'], 'tk-keyword');
  assert.equal(t['Foo'], 'tk-type');
  assert.equal(t['IDisposable'], 'tk-interface');
  assert.equal(t['Bar'], 'tk-type');
  assert.equal(t['int'], 'tk-keyword');
  assert.equal(t['42'], 'tk-number');
  assert.equal(t['Run'], 'tk-function');
  assert.equal(t['if'], 'tk-control');
  assert.equal(t['return'], 'tk-control');
  assert.equal(t['foreach'], 'tk-control');
  assert.equal(t['await'], 'tk-control');
  assert.equal(t['null'], 'tk-keyword');
  assert.equal(t['var'], 'tk-keyword');
  assert.equal(t['{name}'], 'tk-escape');
  assert.equal(t['// обычный'], 'tk-comment');
});

test('VS: XAML — теги, имена и значения атрибутов, разделители', () => {
  const t = paint('<Grid x:Name="Root"><!-- c --></Grid>', 'xml');
  assert.equal(t['<'], 'tk-delim');
  assert.equal(t['Grid'], 'tk-tag');
  assert.equal(t['x:Name'], 'tk-attr-name');
  assert.equal(t['"Root"'], 'tk-attr-value');
  assert.equal(t['<!-- c -->'], 'tk-comment');
});

test('VS: JSON — ключи и строки-значения', () => {
  const t = paint('{"key": "value", "n": 1, "b": true}', 'json');
  assert.equal(t['"key"'], 'tk-key');
  assert.equal(t['"value"'], 'tk-string');
  assert.equal(t['1'], 'tk-number');
  assert.equal(t['true'], 'tk-keyword');
});

test('VS: CSS — селекторы, свойства, значения', () => {
  const t = paint('.a > #b:hover { color: #fff; margin: 0 4px; }', 'css');
  assert.equal(t['.a'], 'tk-selector');
  assert.equal(t['#b:hover'], 'tk-selector');
  assert.equal(t['color'], 'tk-css-prop');
  assert.equal(t['#fff'], 'tk-css-value');
  assert.equal(t['4px'], 'tk-css-value');
});

test('VS: JS — функции и вызовы, классы, управляющие слова', () => {
  const t = paint('function f(a) { if (a) return console.log(a); } class K {}', 'javascript');
  assert.equal(t['f'], 'tk-function');
  assert.equal(t['log'], 'tk-function');
  assert.equal(t['K'], 'tk-type');
  assert.equal(t['if'], 'tk-control');
  assert.equal(t['function'], 'tk-keyword');
  assert.equal(t['console'], 'tk-keyword');
});

test('пустые строки дают строки без токенов', () => {
  const classes = [''];
  const result = H.splitHighlighted('a\n\n<span class="hljs-comment">b</span>\n', classes);
  assert.equal(result.lines, 4);
  assert.deepEqual(H.lineTokens(result, 1), []);
  assert.deepEqual(H.lineTokens(result, 3), []);
});

test('непонятный HTML — отказ, а не догадка', () => {
  const classes = [''];
  assert.equal(H.splitHighlighted('<b>x</b>', classes), null);
  assert.equal(H.splitHighlighted('<span class="x" onclick="y">a</span>', classes), null);
  assert.equal(H.splitHighlighted('<span class="a">x', classes), null);
  assert.equal(H.splitHighlighted('x</span>', classes), null);
  assert.equal(H.splitHighlighted('&nbsp;', classes), null);
  assert.equal(H.splitHighlighted('a & b', classes), null);
  assert.equal(H.splitHighlighted('<span class="a<b">x</span>', classes), null);
  assert.equal(H.splitHighlighted(null, classes), null);
});

test('таблица классов общая: повторный класс не дублируется', () => {
  const classes = [''];
  H.splitHighlighted('<span class="hljs-keyword">a</span>', classes);
  H.splitHighlighted('<span class="hljs-keyword">b</span><span class="hljs-number">1</span>', classes);
  assert.deepEqual(classes, ['', 'tk-keyword', 'tk-number']);
});

test('настоящий hljs: блочный комментарий C# красится на всех своих строках', () => {
  const lines = ['int a = 1; /* начало', '  середина "не строка"', '  конец */ var s = "x";'];
  const { result, classes } = highlight(lines.join('\n'), 'csharp');
  assert.ok(result);
  assert.equal(result.lines, 3);
  const rows = render(result, classes, lines);
  assert.deepEqual(rows[1], [['  середина "не строка"', 'tk-comment']]);
  assert.deepEqual(rows[2][0], ['  конец */', 'tk-comment']);
  assert.ok(rows[2].some(([text, cls]) => text === '"x"' && cls === 'tk-string'));
  // Склейка токенов каждой строки даёт ровно строку.
  rows.forEach((tokens, i) => assert.equal(tokens.map((t) => t[0]).join(''), lines[i]));
});

test('настоящий hljs: многострочная строка Python и XAML не рвут раскладку', () => {
  const py = ['x = """one', 'two', 'three"""', 'y = 2'];
  const a = highlight(py.join('\n'), 'python');
  assert.equal(a.result.lines, 4);
  assert.deepEqual(render(a.result, a.classes, py)[1], [['two', 'tk-string']]);

  const xaml = ['<Grid x:Name="Root">', '  <!-- a', '  b -->', '</Grid>'];
  const b = highlight(xaml.join('\n'), 'xml');
  assert.equal(b.result.lines, 4);
  assert.deepEqual(render(b.result, b.classes, xaml)[2], [['  b -->', 'tk-comment']]);
});

test('обрезка токенов совпадает с clipLine', () => {
  const classes = [''];
  const result = H.splitHighlighted('<span class="hljs-keyword">abcdef</span>ghij', classes);
  assert.deepEqual(H.lineTokens(result, 0, 8), [6, 1, 2, 0]);
  assert.deepEqual(H.lineTokens(result, 0, 3), [3, 1]);
  assert.deepEqual(H.lineTokens(result, 5), []);
  assert.deepEqual(H.lineTokens(null, 0), []);
});

// ----- строки текста ------------------------------------------------------------------------

test('splitText: CRLF, хвостовой перевод строки, пустой текст', () => {
  assert.deepEqual(H.splitText('a\r\nb\r\n'), ['a', 'b']);
  assert.deepEqual(H.splitText('a\n\nb'), ['a', '', 'b']);
  assert.deepEqual(H.splitText('\n'), ['']);
  assert.deepEqual(H.splitText(''), []);
  // Число строк разбора совпадает с числом строк текста — токены не съезжают.
  const lines = H.splitText('x\r\n/* a\r\nb */\r\n');
  const { result } = highlight(H.joinLines(lines), 'javascript');
  assert.equal(result.lines, lines.length);
});

// ----- стороны diff -------------------------------------------------------------------------

const KINDS = { context: M.ROW_CONTEXT, add: M.ROW_ADD, del: M.ROW_DEL, hunk: M.ROW_HUNK };

test('diffSides: старая сторона — контекст и удалённые, новая — контекст и добавленные', () => {
  const { rows } = M.parseUnified([
    '@@ -1,3 +1,3 @@',
    ' a',
    '-b',
    '+B',
    ' c',
    '@@ -10,1 +10,2 @@',
    ' x',
    '+y',
    '\\ No newline at end of file',
    ''
  ].join('\n'));

  const sides = H.diffSides(rows, KINDS);
  assert.deepEqual(sides.segments.map((s) => s.lines), [
    ['a', 'b', 'c'], ['a', 'B', 'c'],
    ['x'], ['x', 'y']
  ]);

  // rows: h a -b +B c h x +y \ ; сквозная нумерация: 0..2 старая, 3..5 новая, 6 старая, 7..8 новая.
  assert.deepEqual(Array.from(sides.rowLine), [-1, 3, 1, 4, 5, -1, 7, 8, -1]);
  assert.equal(sides.oldLines, 4);
  assert.equal(sides.newLines, 5);
});

test('diffSides + concatResults: удалённая строка внутри комментария красится по старой стороне', () => {
  const { rows } = M.parseUnified([
    '@@ -1,3 +1,2 @@',
    ' /* begin',
    '-removed',
    ' end */',
    ''
  ].join('\n'));
  const sides = H.diffSides(rows, KINDS);
  const classes = [''];
  const parts = sides.segments.map((s) => H.splitHighlighted(
    hljs.highlight(H.joinLines(s.lines), { language: 'javascript' }).value, classes));
  const all = H.concatResults(parts, sides.segments.map((s) => s.lines.length));
  assert.equal(all.lines, 5);

  const removed = sides.rowLine[2];
  const tokens = H.lineTokens(all, removed);
  assert.equal(classes[tokens[1]], 'tk-comment');
  assert.equal(tokens[0], 'removed'.length);
});

test('concatResults: сегмент без подсветки даёт пустые строки, соседи не съезжают', () => {
  const classes = [''];
  const a = H.splitHighlighted('<span class="hljs-keyword">a</span>\nb', classes);
  const c = H.splitHighlighted('<span class="hljs-number">1</span>', classes);
  const all = H.concatResults([a, null, c], [2, 3, 1]);
  assert.equal(all.lines, 6);
  assert.deepEqual(H.lineTokens(all, 0), [1, 1]);
  assert.deepEqual(H.lineTokens(all, 1), [1, 0]);
  assert.deepEqual(H.lineTokens(all, 3), []);
  assert.deepEqual(H.lineTokens(all, 5), [1, 2]);
});

test('порог подсветки', () => {
  assert.equal(H.withinLimit(H.MAX_CHARS, H.MAX_LINES), true);
  assert.equal(H.withinLimit(H.MAX_CHARS + 1, 1), false);
  assert.equal(H.withinLimit(1, H.MAX_LINES + 1), false);
});

// ----- модель режима «файл» -------------------------------------------------------------------

test('fileEntries: фокус, проблема, индекс исходного массива', () => {
  const entries = M.fileEntries({
    files: [
      { p: 'a.cs', focus: { from: 10, to: 20 }, problem: null },
      { focus: null },
      { p: 'b.cs', focus: { from: 5, to: 2 }, problem: 'none' },
      { p: 'c.bin', focus: null, problem: 'binary' }
    ]
  });

  assert.equal(entries.length, 3);
  assert.deepEqual(entries.map((e) => e.i), [0, 2, 3]);
  assert.deepEqual(entries[0].focus, { from: 10, to: 20 });
  assert.deepEqual(entries[1].focus, { from: 5, to: 5 });
  assert.equal(entries[1].problem, null);
  assert.equal(entries[1].state, 'loading');
  assert.equal(entries[2].problem, 'binary');
  assert.equal(entries[2].state, 'problem');
});

test('clampFocus: хвост за концом файла обрезается, начало за концом — фокуса нет', () => {
  assert.deepEqual(M.clampFocus({ from: 3, to: 9 }, 5), { from: 3, to: 5 });
  assert.deepEqual(M.clampFocus({ from: 5, to: 5 }, 5), { from: 5, to: 5 });
  assert.equal(M.clampFocus({ from: 6, to: 8 }, 5), null);
  assert.equal(M.clampFocus(null, 5), null);
  assert.equal(M.clampFocus({ from: 1, to: 1 }, 0), null);
});

test('acceptContent: части по порядку, чужой хвост сбрасывает сборку', () => {
  const [entry] = M.fileEntries({ files: [{ p: 'a' }] });
  assert.equal(M.acceptContent(entry, { part: 0, last: false, text: 'ab' }), false);
  assert.equal(M.acceptContent(entry, { part: 1, last: true, text: 'cd' }), true);
  assert.equal(entry.text, 'abcd');
  assert.equal(entry.state, 'loaded');
  // Повтор после сборки не принимается.
  assert.equal(M.acceptContent(entry, { part: 0, last: true, text: 'x' }), false);

  const [other] = M.fileEntries({ files: [{ p: 'b' }] });
  assert.equal(M.acceptContent(other, { part: 1, last: true, text: 'x' }), false);
  assert.equal(M.acceptContent(other, { part: 0, last: true, text: 'ok' }), true);
  assert.equal(other.text, 'ok');

  const [problem] = M.fileEntries({ files: [{ p: 'c', problem: 'notFound' }] });
  assert.equal(M.acceptContent(problem, { part: 0, last: true, text: 'x' }), false);
});

test('цель прокрутки: первый с фокусом, иначе первый; ждёт файлы до цели', () => {
  const entries = M.fileEntries({
    files: [{ p: 'a' }, { p: 'b', problem: 'binary' }, { p: 'c', focus: { from: 3, to: 4 } }, { p: 'd' }]
  });
  assert.equal(M.focusTarget(entries), 2);
  assert.equal(M.readyToScroll(entries, 2), false);
  M.acceptContent(entries[0], { part: 0, last: true, text: 'x' });
  assert.equal(M.readyToScroll(entries, 2), false);
  M.acceptContent(entries[2], { part: 0, last: true, text: 'x' });
  assert.equal(M.readyToScroll(entries, 2), true, 'd после цели не ждём');

  assert.equal(M.focusTarget(M.fileEntries({ files: [{ p: 'a' }, { p: 'b' }] })), 0);
  assert.equal(M.focusTarget([]), -1);
  assert.equal(M.readyToScroll([], -1), false);
});

test('focusRow: к шапке файла, к фокусу — с запасом, но не выше шапки', () => {
  assert.equal(M.focusRow(10, null, 3), 11);
  assert.equal(M.focusRow(10, { from: 1, to: 2 }, 3), 11);
  assert.equal(M.focusRow(10, { from: 50, to: 60 }, 3), 10 + 2 + 49 - 3);
});

test('problemText: каждая причина по-русски, неизвестная — общая фраза', () => {
  for (const code of ['notFound', 'outsideRoot', 'tooLarge', 'binary', 'unreadable']) {
    assert.match(M.problemText(code), /[а-я]/);
    assert.notEqual(M.problemText(code), M.problemText('что-то'));
  }
});

let failed = 0;
for (const [name, body] of tests) {
  try {
    body();
    console.log(`ok   ${name}`);
  } catch (error) {
    failed++;
    console.log(`FAIL ${name}`);
    console.log(error && error.stack ? error.stack : error);
  }
}

console.log(`\n${tests.length - failed}/${tests.length} пройдено`);
process.exit(failed === 0 ? 0 : 1);
