// Копирует ассеты xterm.js и highlight.js из node_modules в web/vendor.
// Страница грузит только локальные файлы: CDN запрещён, приложение должно работать без сети.
// Запуск: npm install && npm run vendor (в каталоге web).
//
// highlight.js — готовая браузерная сборка @highlightjs/cdn-assets (версия зафиксирована в
// package.json, лицензия BSD-3-Clause — кладётся рядом): «общий» набор языков одним файлом плюс
// грамматики, которых в общем наборе нет. Работает только в потоке подсветки
// (highlight-worker.js), на страницу не грузится.

import { copyFile, mkdir, access } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const webRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const modules = resolve(webRoot, 'node_modules');
const vendor = resolve(webRoot, 'vendor');

const assets = [
  ['@xterm/xterm/lib/xterm.js', 'xterm.js'],
  ['@xterm/xterm/css/xterm.css', 'xterm.css'],
  ['@xterm/addon-fit/lib/addon-fit.js', 'addon-fit.js'],
  ['@xterm/addon-webgl/lib/addon-webgl.js', 'addon-webgl.js'],
  ['@xterm/addon-unicode11/lib/addon-unicode11.js', 'addon-unicode11.js'],
  ['@highlightjs/cdn-assets/highlight.min.js', 'highlight.min.js'],
  ['@highlightjs/cdn-assets/languages/powershell.min.js', 'hljs-powershell.min.js'],
  ['@highlightjs/cdn-assets/languages/dockerfile.min.js', 'hljs-dockerfile.min.js'],
  ['@highlightjs/cdn-assets/LICENSE', 'highlight.js.LICENSE.txt'],
];

await mkdir(vendor, { recursive: true });

for (const [from, to] of assets) {
  const source = resolve(modules, from);

  try {
    await access(source);
  } catch {
    console.error(`Нет файла ${source}. Сначала выполните npm install.`);
    process.exit(1);
  }

  await copyFile(source, resolve(vendor, to));
  console.log(`vendor/${to}`);
}

console.log(`Готово: ${assets.length} файлов в ${vendor}`);
