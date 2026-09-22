// Проверка чистой модели панели diff без GUI: node web/tools/diff-model-test.mjs
// JS-тестов в проекте нет, поэтому здесь минимальный набор assert'ов без фреймворка.

import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const M = require('../diff-model.js');

const tests = [];
const test = (name, body) => tests.push([name, body]);

const kinds = (rows) => rows.map((r) => r.t).join('');

test('заголовок файла пропускается, фрагмент нумеруется', () => {
  const text = [
    'diff --git a/a.cs b/a.cs',
    'index 111..222 100644',
    '--- a/a.cs',
    '+++ b/a.cs',
    '@@ -10,3 +10,4 @@ class A',
    ' one',
    '-two',
    '+два',
    '+три',
    ' four',
    ''
  ].join('\n');

  const { rows } = M.parseUnified(text);
  assert.equal(kinds(rows), 'hcdaac');
  assert.deepEqual(rows.map((r) => [r.o, r.n]), [[0, 0], [10, 10], [11, 0], [0, 11], [0, 12], [12, 13]]);
  assert.equal(rows[3].s, 'два');
});

test('«--- x» внутри фрагмента — удалённая строка, а не заголовок', () => {
  const text = '--- a/f\n+++ b/f\n@@ -1,2 +1,1 @@\n--- x\n+++ y\n-z\n';
  const { rows } = M.parseUnified(text);
  // «+++ y» тоже содержимое: добавленная строка «++ y».
  assert.equal(kinds(rows), 'hdad');
  assert.equal(rows[1].s, '-- x');
  assert.equal(rows[2].s, '++ y');
});

test('счётчики в @@ необязательны', () => {
  const { rows } = M.parseUnified('@@ -5 +5 @@\n-a\n+b\n');
  assert.equal(kinds(rows), 'hda');
  assert.equal(rows[1].o, 5);
  assert.equal(rows[2].n, 5);
});

test('пустая строка внутри фрагмента — контекст', () => {
  const { rows } = M.parseUnified('@@ -1,3 +1,3 @@\n a\n\n-b\n+c\n');
  assert.equal(kinds(rows), 'hccda');
  assert.deepEqual([rows[2].o, rows[2].n, rows[2].s], [2, 2, '']);
});

test('\\r срезается, хвост после последнего \\n не строка', () => {
  const { rows } = M.parseUnified('@@ -1 +1 @@\r\n-a\r\n+b\r\n');
  assert.equal(rows.length, 3);
  assert.equal(rows[1].s, 'a');
  assert.equal(rows[2].s, 'b');
});

test('\\ No newline at end of file — отдельная строка без номеров', () => {
  const { rows } = M.parseUnified('@@ -1 +1 @@\n-a\n\\ No newline at end of file\n+a\n\\ No newline at end of file\n');
  assert.equal(kinds(rows), 'hdeae');
  assert.equal(rows[3].n, 1);
});

test('нумерация верна в нескольких фрагментах', () => {
  const text = '@@ -1,2 +1,2 @@\n a\n-b\n+B\n@@ -20,2 +20,3 @@\n x\n+y\n z\n';
  const { rows } = M.parseUnified(text);
  assert.equal(kinds(rows), 'hcdahcac');
  assert.deepEqual(rows.slice(5).map((r) => [r.o, r.n]), [[20, 20], [0, 21], [21, 22]]);
});

test('служебные строки до фрагмента остаются, бинарный diff без фрагментов', () => {
  const text = 'diff --git a/p.png b/p.png\nnew file mode 100644\nBinary files /dev/null and b/p.png differ\n';
  const { rows } = M.parseUnified(text);
  assert.equal(kinds(rows), 'mm');
  assert.equal(rows[1].s, 'Binary files /dev/null and b/p.png differ');
});

test('пустой текст — ни одной строки', () => {
  assert.deepEqual(M.parseUnified('').rows, []);
  assert.deepEqual(M.parseUnified(undefined).rows, []);
});

test('maxLen — длина самой длинной строки содержимого', () => {
  const { maxLen } = M.parseUnified('@@ -1 +1 @@\n-abc\n+abcdefg\n');
  assert.equal(maxLen, 7);
});

test('расширение: регистр, точка в начале имени, каталоги с точкой', () => {
  assert.equal(M.extensionOf('src/A.CS'), '.cs');
  assert.equal(M.extensionOf('Assets/x.prefab.meta'), '.meta');
  assert.equal(M.extensionOf('.gitignore'), '');
  assert.equal(M.extensionOf('dir.v2/Makefile'), '');
  assert.equal(M.extensionOf('Папка/файл.txt'), '.txt');
});

test('счётчики расширений: частые сверху, при равенстве по имени', () => {
  const files = ['a.meta', 'b.meta', 'c.meta', 'x.cs', 'y.cs', 'z.js', 'Makefile'].map((p) => ({ p }));
  assert.deepEqual(M.extensionCounts(files), [
    { ext: '.meta', count: 3 },
    { ext: '.cs', count: 2 },
    { ext: '', count: 1 },
    { ext: '.js', count: 1 }
  ]);
});

test('фильтр по расширению и по пути', () => {
  const files = [{ p: 'a.cs' }, { p: 'b.meta' }, { p: 'Src/C.cs', o: 'old/q.cs' }];
  assert.deepEqual(M.filterByExtension(files, new Set(['.meta'])).map((f) => f.p), ['a.cs', 'Src/C.cs']);
  assert.deepEqual(M.filterByPath(files, ' src ').map((f) => f.p), ['Src/C.cs']);
  assert.deepEqual(M.filterByPath(files, 'OLD/').map((f) => f.p), ['Src/C.cs']);
  assert.equal(M.filterByPath(files, '').length, 3);
});

const file = (p, a, d, c = 'none') => ({ p, a, d, c });

test('бюджет: раскрывает по порядку, пока суммарно ≤ 2000 строк', () => {
  const files = [file('a', 1000, 0), file('b', 900, 100), file('c', 1, 0)];
  assert.deepEqual([...M.autoExpand(files, new Set())], ['a', 'b']);
});

test('бюджет: первый невлезший останавливает автораскрытие', () => {
  const files = [file('a', 1500, 0), file('b', 600, 0), file('c', 1, 0)];
  assert.deepEqual([...M.autoExpand(files, new Set())], ['a']);
});

test('бюджет: не больше 50 файлов', () => {
  const files = Array.from({ length: 60 }, (_, i) => file('f' + i, 1, 0));
  const result = M.autoExpand(files, new Set());
  assert.equal(result.size, 50);
  assert.ok(result.has('f49'));
  assert.ok(!result.has('f50'));
});

test('бюджет: свёрнутые C# и неизвестные не раскрываются и не останавливают обход', () => {
  const files = [
    file('big', 5000, 0, 'large'),
    file('lock', 3000, 0, 'generated'),
    file('bin', null, null, 'binary'),
    file('huge-untracked', null, null),
    file('small', 10, 0)
  ];
  assert.deepEqual([...M.autoExpand(files, new Set())], ['small']);
});

test('бюджет: файлы агента раскрываются всегда и бюджет не тратят', () => {
  const files = [file('agent', 1990, 0, 'large'), file('a', 1500, 0), file('b', 500, 0)];
  const result = M.autoExpand(files, new Set(['agent']));
  assert.deepEqual([...result], ['agent', 'a', 'b']);
});

test('бюджет: бинарный файл агента не раскрывается', () => {
  const result = M.autoExpand([file('p.png', null, null, 'binary')], new Set(['p.png']));
  assert.equal(result.size, 0);
});

test('бюджет: снятое расширение уходит из бюджета', () => {
  const files = [file('a.meta', 1900, 0), file('b.cs', 500, 0)];
  assert.deepEqual([...M.autoExpand(files, new Set())], ['a.meta']);
  const visible = M.filterByExtension(files, new Set(['.meta']));
  assert.deepEqual([...M.autoExpand(visible, new Set())], ['b.cs']);
});

test('бюджет: пределы можно передать явно', () => {
  const files = [file('a', 5, 0), file('b', 5, 0)];
  assert.deepEqual([...M.autoExpand(files, new Set(), { maxLines: 5, maxFiles: 10 })], ['a']);
});

test('обрезка длинной строки', () => {
  const long = 'x'.repeat(2001);
  assert.deepEqual(M.clipLine('abc'), { text: 'abc', cut: false });
  const clipped = M.clipLine(long);
  assert.equal(clipped.text.length, 2000);
  assert.equal(clipped.cut, true);
  assert.equal(M.clipLine('x'.repeat(2000)).cut, false);
});

test('раскладка и поиск блока по строке', () => {
  const layout = M.buildLayout([3, 1, 0, 5]);
  assert.deepEqual(layout.starts, [0, 3, 4, 4]);
  assert.equal(layout.total, 9);
  assert.equal(M.locate(layout, 0), 0);
  assert.equal(M.locate(layout, 2), 0);
  assert.equal(M.locate(layout, 3), 1);
  // Пустой блок 2 пропускается: строка 4 принадлежит блоку 3.
  assert.equal(M.locate(layout, 4), 3);
  assert.equal(M.locate(layout, 8), 3);
  assert.equal(M.locate(layout, 9), -1);
  assert.equal(M.locate(M.buildLayout([]), 0), -1);
});

test('окно виртуализации: видимое плюс запас, в пределах списка', () => {
  assert.deepEqual(M.visibleRange(0, 200, 20, 1000, 10), { first: 0, last: 20 });
  assert.deepEqual(M.visibleRange(2000, 200, 20, 1000, 10), { first: 90, last: 120 });
  assert.deepEqual(M.visibleRange(19800, 400, 20, 1000, 10), { first: 980, last: 1000 });
  assert.deepEqual(M.visibleRange(0, 200, 20, 0, 10), { first: 0, last: 0 });
});

test('весь файл на 50 тыс. строк разбирается быстро', () => {
  const lines = ['@@ -1,50000 +1,50000 @@'];
  for (let i = 0; i < 50000; i++) {
    lines.push(' строка ' + i);
  }

  const started = process.hrtime.bigint();
  const { rows } = M.parseUnified(lines.join('\n') + '\n');
  const ms = Number(process.hrtime.bigint() - started) / 1e6;
  assert.equal(rows.length, 50001);
  assert.equal(rows[50000].n, 50000);
  assert.ok(ms < 500, `разбор занял ${ms.toFixed(0)} мс`);
});

let failed = 0;
for (const [name, body] of tests) {
  try {
    body();
    console.log(`ok   ${name}`);
  } catch (error) {
    failed++;
    console.log(`FAIL ${name}\n     ${error.message.split('\n').join('\n     ')}`);
  }
}

console.log(`\n${tests.length - failed}/${tests.length} пройдено`);
process.exit(failed === 0 ? 0 : 1);
