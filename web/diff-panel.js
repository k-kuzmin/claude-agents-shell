// Панель diff поверх области терминала (issue #5). Своя на каждую вкладку, в том же WebView2.
// Протокол — раздел M7 docs/PROGRESS.md:
//   из C#:  diff.pending | diff.index | diff.file | diff.error | diff.stale
//   в C#:   diff.refresh | diff.file.request | diff.closed
//
// Разбор, фильтры и бюджет — в diff-model.js (чистый модуль с node-тестом), здесь только DOM.
// Всё, что приходит из C# (пути, note, содержимое), попадает в DOM только через textContent.
(function (root) {
  'use strict';

  var M = root.DiffModel;

  // Строки фиксированной высоты — на этом держится виртуализация: номер строки по scrollTop
  // считается делением, без замеров. Значения совпадают с line-height в diff.css.
  var ROW_H = 20;
  var TOC_ROW_H = 24;

  // Запас строк за краями видимого окна: прокрутка колесом не успевает показать пустоту.
  var OVERSCAN = 40;

  var KIND_LABEL = { M: 'M', A: 'A', D: 'D', R: 'R', U: 'U' };
  var KIND_TITLE = { M: 'изменён', A: 'добавлен', D: 'удалён', R: 'переименован', U: 'новый, не в git' };
  var COLLAPSE_BADGE = { large: 'Большой diff', generated: 'Сгенерированный', binary: 'Бинарный' };

  function el(tag, className, text) {
    var node = document.createElement(tag);
    if (className) {
      node.className = className;
    }
    if (text !== undefined && text !== null) {
      node.textContent = text;
    }
    return node;
  }

  function button(className, text, action) {
    var node = el('button', className, text);
    node.type = 'button';
    if (action) {
      node.setAttribute('data-action', action);
    }
    return node;
  }

  // ---------------------------------------------------------------------------------------
  // Виртуальный список: в DOM только окно видимых строк с запасом. Элементы берутся из кольца
  // по номеру строки (index % размер), поэтому при прокрутке на строку перерисовывается
  // одна строка, а не всё окно. Перерисовка — не чаще кадра.
  // ---------------------------------------------------------------------------------------
  function VirtualList(scroller, rowHeight, renderRow) {
    this.scroller = scroller;
    this.rowHeight = rowHeight;
    this.renderRow = renderRow;
    this.sizer = el('div', 'vl-sizer');
    this.pool = [];
    this.count = 0;
    this.generation = 0;
    this.frame = 0;

    scroller.appendChild(this.sizer);

    var self = this;
    this.onScroll = function () { self.schedule(); };
    scroller.addEventListener('scroll', this.onScroll, { passive: true });
  }

  VirtualList.prototype.setCount = function (count) {
    this.count = count;
    this.sizer.style.height = (count * this.rowHeight) + 'px';
    this.invalidate();
  };

  // Содержимое строк поменялось: всё окно перерисуется в ближайшем кадре.
  VirtualList.prototype.invalidate = function () {
    this.generation++;
    this.schedule();
  };

  VirtualList.prototype.schedule = function () {
    if (this.frame !== 0) {
      return;
    }

    var self = this;
    this.frame = requestAnimationFrame(function () {
      self.frame = 0;
      self.render();
    });
  };

  VirtualList.prototype.render = function () {
    var viewport = this.scroller.clientHeight;
    if (viewport === 0) {
      // Скрыт: рисовать нечего, покажемся — нарисуемся.
      return;
    }

    this.scroller.style.setProperty('--vw', this.scroller.clientWidth + 'px');

    var range = M.visibleRange(this.scroller.scrollTop, viewport, this.rowHeight, this.count, OVERSCAN);
    var needed = Math.ceil(viewport / this.rowHeight) + (2 * OVERSCAN) + 2;

    if (this.pool.length < needed) {
      // Окно выросло (развернули окно): кольцо пересобирается, все строки перерисовываются.
      while (this.pool.length < needed) {
        var row = el('div', 'vl-row');
        row.style.height = this.rowHeight + 'px';
        row._index = -1;
        this.sizer.appendChild(row);
        this.pool.push(row);
      }

      for (var r = 0; r < this.pool.length; r++) {
        this.pool[r]._index = -1;
      }
    }

    var size = this.pool.length;
    var used = new Array(size);

    for (var i = range.first; i < range.last; i++) {
      var node = this.pool[i % size];
      used[i % size] = true;

      if (node._index !== i || node._generation !== this.generation) {
        node._index = i;
        node._generation = this.generation;
        node.style.transform = 'translateY(' + (i * this.rowHeight) + 'px)';
        node.hidden = false;
        this.renderRow(node, i);
      }
    }

    for (var k = 0; k < size; k++) {
      if (!used[k] && !this.pool[k].hidden) {
        this.pool[k].hidden = true;
        this.pool[k]._index = -1;
        this.pool[k].textContent = '';
      }
    }
  };

  VirtualList.prototype.scrollToRow = function (index) {
    this.scroller.scrollTop = index * this.rowHeight;
    this.schedule();
  };

  VirtualList.prototype.destroy = function () {
    if (this.frame !== 0) {
      cancelAnimationFrame(this.frame);
      this.frame = 0;
    }
    this.scroller.removeEventListener('scroll', this.onScroll);
    this.pool = [];
  };

  // ---------------------------------------------------------------------------------------
  // Панель одной вкладки.
  // ---------------------------------------------------------------------------------------
  function Panel(manager, id) {
    this.manager = manager;
    this.id = id;
    this.visible = false;
    this.status = 'pending';
    this.data = null;
    this.entries = [];
    this.byPath = new Map();
    this.expand = new Set();
    this.excluded = new Set();
    this.query = '';
    this.display = [];
    this.layout = M.buildLayout([]);
    this.ws = false;
    this.stale = false;

    this.build();
  }

  Panel.prototype.build = function () {
    var self = this;

    var rootNode = el('div', 'diff-panel hidden');
    rootNode.tabIndex = -1;
    rootNode.setAttribute('data-diff-for', this.id);

    var bar = el('div', 'diff-bar');
    var heading = el('div', 'diff-heading');
    this.titleNode = el('div', 'diff-title', 'Изменения ветки');
    this.infoNode = el('div', 'diff-info', '');
    heading.appendChild(this.titleNode);
    heading.appendChild(this.infoNode);

    var controls = el('div', 'diff-controls');
    var wsLabel = el('label', 'diff-ws');
    this.wsInput = el('input');
    this.wsInput.type = 'checkbox';
    wsLabel.appendChild(this.wsInput);
    wsLabel.appendChild(document.createTextNode(' без пробелов (-w)'));
    this.worktreeSelect = el('select', 'diff-worktree hidden');
    this.worktreeSelect.title = 'Рабочее дерево';
    var refresh = button('diff-btn', 'Обновить', 'refresh');
    var close = button('diff-btn diff-close', '×', 'close');
    close.title = 'Закрыть (Esc)';
    controls.appendChild(wsLabel);
    controls.appendChild(this.worktreeSelect);
    controls.appendChild(refresh);
    controls.appendChild(close);

    bar.appendChild(heading);
    bar.appendChild(controls);

    this.staleNode = button('diff-stale hidden', 'Есть изменения — обновить', 'refresh');

    var body = el('div', 'diff-body');
    var side = el('div', 'diff-side');
    this.searchInput = el('input', 'diff-search');
    this.searchInput.type = 'text';
    this.searchInput.placeholder = 'Фильтр по пути';
    this.searchInput.spellcheck = false;
    this.extsNode = el('div', 'diff-exts');
    this.summaryNode = el('div', 'diff-summary', '');
    var tocScroller = el('div', 'diff-toc');
    side.appendChild(this.searchInput);
    side.appendChild(this.extsNode);
    side.appendChild(this.summaryNode);
    side.appendChild(tocScroller);

    var mainScroller = el('div', 'diff-main');
    body.appendChild(side);
    body.appendChild(mainScroller);

    this.messageNode = el('div', 'diff-message', 'Строится diff…');

    this.overlayNode = el('div', 'diff-overlay hidden');
    var overlayBox = el('div', 'diff-overlay-box');
    var overlayHead = el('div', 'diff-overlay-head', 'Строка целиком');
    this.overlayNote = el('span', 'diff-overlay-note', '');
    this.overlayCopy = button('diff-btn', 'Копировать целиком', 'overlay-copy');
    var overlayClose = button('diff-btn', 'Закрыть', 'overlay-close');
    overlayHead.appendChild(this.overlayNote);
    overlayHead.appendChild(this.overlayCopy);
    overlayHead.appendChild(overlayClose);
    this.overlayText = el('pre', 'diff-overlay-text');
    overlayBox.appendChild(overlayHead);
    overlayBox.appendChild(this.overlayText);
    this.overlayNode.appendChild(overlayBox);

    rootNode.appendChild(bar);
    rootNode.appendChild(this.staleNode);
    rootNode.appendChild(body);
    rootNode.appendChild(this.messageNode);
    rootNode.appendChild(this.overlayNode);

    this.root = rootNode;
    this.bodyNode = body;
    this.toc = new VirtualList(tocScroller, TOC_ROW_H, function (node, index) { self.renderTocRow(node, index); });
    this.main = new VirtualList(mainScroller, ROW_H, function (node, index) { self.renderMainRow(node, index); });

    rootNode.addEventListener('keydown', function (event) { self.onKeyDown(event); });
    rootNode.addEventListener('click', function (event) { self.onClick(event); });
    this.wsInput.addEventListener('change', function () {
      self.requestRefresh(null, self.wsInput.checked);
    });
    this.worktreeSelect.addEventListener('change', function () {
      self.requestRefresh(self.worktreeSelect.value, self.ws);
    });
    this.searchInput.addEventListener('input', function () {
      self.query = self.searchInput.value;
      self.refilter();
    });

    this.manager.host.appendChild(rootNode);
  };

  Panel.prototype.destroy = function () {
    this.toc.destroy();
    this.main.destroy();
    this.entries = [];
    this.display = [];
    this.byPath.clear();
    if (this.root.parentNode) {
      this.root.parentNode.removeChild(this.root);
    }
  };

  Panel.prototype.post = function (message) {
    message.id = this.id;
    this.manager.post(message);
  };

  // ----- состояние панели ------------------------------------------------------------------

  Panel.prototype.setVisible = function (visible) {
    if (this.visible === visible) {
      return;
    }

    this.visible = visible;
    this.root.classList.toggle('hidden', !visible);

    if (!visible) {
      // Обычное содержимое скрытой панели остаётся: иначе каждое переключение вкладки
      // перезапрашивало бы до 50+ файлов. Выгружаются только крупные файлы
      // (M.keepWhenHidden) — желание «раскрыт» у них запоминается, при показе они
      // запрашиваются заново. Начатые загрузки доигрываются и принимаются.
      for (var i = 0; i < this.entries.length; i++) {
        var entry = this.entries[i];
        if (entry.state === 'loaded' && !M.keepWhenHidden(entry.textLength)) {
          unload(entry);
        }
      }
      this.closeOverlay();
      return;
    }

    this.relayout();
    this.requestWanted();
  };

  Panel.prototype.showMessage = function (text) {
    this.messageNode.textContent = text;
    this.messageNode.classList.remove('hidden');
    this.bodyNode.classList.add('hidden');
  };

  Panel.prototype.hideMessage = function () {
    this.messageNode.classList.add('hidden');
    this.bodyNode.classList.remove('hidden');
  };

  Panel.prototype.onPending = function () {
    this.status = 'pending';
    this.stale = false;
    this.staleNode.classList.add('hidden');
    this.clearEntries();
    this.showMessage('Строится diff…');
    this.relayout();
  };

  Panel.prototype.clearEntries = function () {
    this.entries = [];
    this.byPath = new Map();
    this.display = [];
  };

  Panel.prototype.onIndex = function (message) {
    var previous = this.byPath;
    var files = Array.isArray(message.files) ? message.files : [];

    this.status = 'ready';
    this.data = message;
    this.ws = message.ws === true;
    this.expand = new Set(Array.isArray(message.expand) ? message.expand : []);
    this.stale = false;
    this.staleNode.classList.add('hidden');

    this.entries = [];
    this.byPath = new Map();
    for (var i = 0; i < files.length; i++) {
      var f = files[i];
      if (!f || typeof f.p !== 'string') {
        continue;
      }

      var entry = {
        p: f.p,
        o: typeof f.o === 'string' ? f.o : null,
        k: typeof f.k === 'string' ? f.k : 'M',
        a: typeof f.a === 'number' ? f.a : null,
        d: typeof f.d === 'number' ? f.d : null,
        c: typeof f.c === 'string' ? f.c : 'none',
        want: null,
        userCollapsed: false,
        ctx: 'hunks',
        state: 'idle',
        parts: null,
        rows: null,
        maxLen: 0,
        error: ''
      };

      // Ручной выбор человека переживает «обновить»: файл, который он раскрыл или свернул,
      // остаётся таким же, если он всё ещё в оглавлении.
      var old = previous.get(f.p);
      if (old && entry.c !== 'binary') {
        if (old.want === 'user') {
          entry.want = 'user';
          entry.ctx = old.ctx;
        }
        entry.userCollapsed = old.userCollapsed;
      }

      this.entries.push(entry);
      this.byPath.set(entry.p, entry);
    }

    this.hideMessage();
    this.renderBar();
    this.renderExtensions();
    this.applyBudget();
    this.refilter();
    this.requestWanted();
  };

  Panel.prototype.onFile = function (message) {
    var entry = this.byPath.get(message.path);
    if (!entry || entry.state !== 'loading' || entry.ctx !== message.ctx) {
      // Ответ на запрос, который уже не нужен: свернули, переключили контекст.
      // Скрытая панель части принимает — загрузка уже шла, выбрасывать её дороже.
      return;
    }

    var part = message.part;
    if (part === 0) {
      entry.parts = [];
    }

    if (!entry.parts || part !== entry.parts.length || typeof message.text !== 'string') {
      // Часть не по порядку — хвост старого ответа. Ждём нового part:0.
      entry.parts = null;
      return;
    }

    entry.parts.push(message.text);
    if (message.last !== true) {
      return;
    }

    var text = entry.parts.join('');
    entry.parts = null;

    if (message.truncated === true) {
      entry.state = 'truncated';
    } else if (!this.visible && !M.keepWhenHidden(text.length)) {
      // Крупный файл догрузился уже после скрытия вкладки: держать его скрытой панели
      // нельзя (то же правило, что в setVisible). Разбирать не нужно — выгружаем сразу,
      // желание «раскрыт» остаётся, при показе requestWanted запросит файл заново.
      unload(entry);
    } else {
      var parsed = M.parseUnified(text);
      entry.rows = parsed.rows;
      entry.maxLen = parsed.maxLen;
      entry.textLength = text.length;
      entry.state = 'loaded';
    }

    this.relayout();
  };

  Panel.prototype.onError = function (message) {
    var text = typeof message.message === 'string' ? message.message : 'Не удалось построить diff.';

    if (message.path === null || message.path === undefined) {
      this.status = 'error';
      this.clearEntries();
      this.showMessage(text);
      this.relayout();
      return;
    }

    var entry = this.byPath.get(message.path);
    if (!entry) {
      return;
    }

    unload(entry);
    entry.state = 'error';
    entry.error = text;
    this.relayout();
  };

  Panel.prototype.onStale = function () {
    this.stale = true;
    this.staleNode.classList.remove('hidden');
  };

  // Бюджет автораскрытия считается по набору после фильтра по расширению (см. diff-model.js).
  // Ручной выбор человека бюджет не трогает. Снятое расширение выгружает содержимое своих файлов.
  Panel.prototype.applyBudget = function () {
    var visible = M.filterByExtension(this.entries, this.excluded);
    var auto = M.autoExpand(visible, this.expand);

    for (var i = 0; i < this.entries.length; i++) {
      var entry = this.entries[i];
      var excluded = this.excluded.has(M.extensionOf(entry.p));

      if (excluded) {
        unload(entry);
      }

      if (entry.c === 'binary' || entry.want === 'user' || entry.userCollapsed) {
        continue;
      }

      var should = !excluded && auto.has(entry.p);
      if (should && entry.want === null) {
        entry.want = 'auto';
      } else if (!should && entry.want === 'auto') {
        entry.want = null;
        unload(entry);
      }
    }
  };

  Panel.prototype.refilter = function () {
    this.display = M.filterByPath(M.filterByExtension(this.entries, this.excluded), this.query);
    this.relayout();
  };

  Panel.prototype.requestWanted = function () {
    if (!this.visible || this.status !== 'ready') {
      return;
    }

    for (var i = 0; i < this.entries.length; i++) {
      var entry = this.entries[i];
      if (entry.want !== null && entry.state === 'idle' && !this.excluded.has(M.extensionOf(entry.p))) {
        this.requestFile(entry);
      }
    }
  };

  Panel.prototype.requestFile = function (entry) {
    unload(entry);
    entry.state = 'loading';
    entry.parts = [];
    this.post({ type: 'diff.file.request', path: entry.p, ctx: entry.ctx });
  };

  Panel.prototype.requestRefresh = function (dir, ws) {
    this.post({ type: 'diff.refresh', dir: dir || null, base: null, ws: ws === true });
  };

  function unload(entry) {
    entry.state = 'idle';
    entry.parts = null;
    entry.rows = null;
    entry.maxLen = 0;
    entry.textLength = 0;
    entry.error = '';
  }

  // ----- действия человека -------------------------------------------------------------------

  Panel.prototype.toggleFile = function (entry) {
    if (entry.c === 'binary') {
      return;
    }

    if (entry.want !== null) {
      entry.want = null;
      entry.userCollapsed = true;
      unload(entry);
    } else {
      entry.want = 'user';
      entry.userCollapsed = false;
      this.requestFile(entry);
    }

    this.relayout();
  };

  Panel.prototype.toggleFullFile = function (entry) {
    if (entry.c === 'binary') {
      return;
    }

    entry.ctx = entry.ctx === 'full' ? 'hunks' : 'full';
    entry.want = 'user';
    entry.userCollapsed = false;
    this.requestFile(entry);
    this.relayout();
  };

  Panel.prototype.toggleExtension = function (ext) {
    if (this.excluded.has(ext)) {
      this.excluded.delete(ext);
    } else {
      this.excluded.add(ext);
    }

    this.renderExtensions();
    this.applyBudget();
    this.refilter();
    this.requestWanted();
  };

  // Строка может весить мегабайты (minified, base64): в <pre> кладётся не больше
  // M.OVERLAY_LINE символов — раскладка многомегабайтного текста останавливала бы и все
  // терминалы страницы. Полная строка доступна кнопкой «Копировать целиком».
  Panel.prototype.openOverlay = function (text) {
    var shown = M.clipLine(text, M.OVERLAY_LINE);
    this.overlayFull = text;
    this.overlayText.textContent = shown.text;
    this.overlayNote.textContent = shown.cut
      ? 'показаны первые ' + shown.text.length + ' из ' + text.length + ' символов'
      : '';
    this.overlayCopy.textContent = 'Копировать целиком';
    this.overlayNode.classList.remove('hidden');
    this.overlayCopy.focus();
  };

  Panel.prototype.copyOverlay = function () {
    var self = this;
    var text = this.overlayFull;
    if (typeof text !== 'string' || !navigator.clipboard) {
      return;
    }

    navigator.clipboard.writeText(text).then(function () {
      self.overlayCopy.textContent = 'Скопировано';
    }).catch(function () {
      self.overlayCopy.textContent = 'Не удалось скопировать';
    });
  };

  Panel.prototype.closeOverlay = function () {
    if (this.overlayNode.classList.contains('hidden')) {
      return false;
    }

    this.overlayNode.classList.add('hidden');
    this.overlayText.textContent = '';
    this.overlayFull = null;
    this.root.focus();
    return true;
  };

  Panel.prototype.onKeyDown = function (event) {
    if (event.key === 'Escape') {
      // Esc принадлежит панели только пока фокус в ней: слушатель висит на корне панели,
      // а не на документе, поэтому Esc в терминале по-прежнему уходит в терминал.
      event.preventDefault();
      event.stopPropagation();
      if (!this.closeOverlay()) {
        this.manager.closeByUser(this.id);
      }
      return;
    }

    if (event.target !== this.root) {
      return;
    }

    // Фокус на самой панели (не в поле ввода) — клавиши прокрутки двигают diff.
    var scroller = this.main.scroller;
    var page = Math.max(ROW_H, scroller.clientHeight - ROW_H);
    var moves = { PageDown: page, PageUp: -page, ArrowDown: ROW_H * 3, ArrowUp: -ROW_H * 3 };

    if (event.key === 'Home') {
      scroller.scrollTop = 0;
      event.preventDefault();
    } else if (event.key === 'End') {
      scroller.scrollTop = scroller.scrollHeight;
      event.preventDefault();
    } else if (Object.prototype.hasOwnProperty.call(moves, event.key)) {
      scroller.scrollTop += moves[event.key];
      event.preventDefault();
    }
  };

  Panel.prototype.onClick = function (event) {
    var target = event.target;
    var actionNode = target.closest ? target.closest('[data-action]') : null;
    var action = actionNode ? actionNode.getAttribute('data-action') : null;

    if (action === 'close') {
      this.manager.closeByUser(this.id);
      return;
    }

    if (action === 'refresh') {
      this.requestRefresh(null, this.ws);
      return;
    }

    if (action === 'overlay-copy') {
      this.copyOverlay();
      return;
    }

    if (action === 'overlay-close') {
      this.closeOverlay();
      return;
    }

    if (action === 'ext') {
      this.toggleExtension(actionNode.getAttribute('data-ext') || '');
      return;
    }

    var row = target.closest ? target.closest('.vl-row') : null;
    if (!row || row._index < 0) {
      return;
    }

    if (row.parentNode === this.toc.sizer) {
      // Строка оглавления i — блок i раскладки: оба построены по одному display.
      if (row._index < this.display.length) {
        this.main.scrollToRow(this.layout.starts[row._index] + 1);
      }
      return;
    }

    var located = this.locateRow(row._index);
    if (!located) {
      return;
    }

    if (action === 'toggle' || action === 'load') {
      this.toggleFile(located.entry);
    } else if (action === 'full') {
      this.toggleFullFile(located.entry);
    } else if (action === 'more' && located.line) {
      this.openOverlay(located.line.s);
    }
  };

  // ----- отрисовка -----------------------------------------------------------------------------

  Panel.prototype.renderBar = function () {
    var data = this.data || {};
    this.titleNode.textContent = typeof data.note === 'string' && data.note.length > 0
      ? data.note
      : 'Изменения ветки';

    var info = [];
    if (typeof data.base === 'string') {
      info.push('база ' + data.base);
    }
    if (typeof data.mergeBase === 'string' && data.mergeBase.length > 0) {
      info.push(data.mergeBase.slice(0, 8));
    }
    if (typeof data.root === 'string') {
      info.push(data.root);
    }
    this.infoNode.textContent = info.join(' · ');
    this.infoNode.title = this.infoNode.textContent;

    this.wsInput.checked = this.ws;

    var worktrees = Array.isArray(data.worktrees) ? data.worktrees : [];
    this.worktreeSelect.textContent = '';
    if (worktrees.length > 1) {
      for (var i = 0; i < worktrees.length; i++) {
        var tree = worktrees[i];
        if (!tree || typeof tree.path !== 'string') {
          continue;
        }
        var option = el('option', null, tree.path + (typeof tree.branch === 'string' ? ' · ' + tree.branch : ' · detached'));
        option.value = tree.path;
        option.selected = tree.current === true;
        this.worktreeSelect.appendChild(option);
      }
    }
    this.worktreeSelect.classList.toggle('hidden', worktrees.length <= 1);
  };

  Panel.prototype.renderExtensions = function () {
    var counts = M.extensionCounts(this.entries);
    this.extsNode.textContent = '';
    this.extsNode.classList.toggle('hidden', counts.length <= 1);

    for (var i = 0; i < counts.length; i++) {
      var item = counts[i];
      var chip = button('diff-ext', (item.ext || 'без расширения') + ' (' + item.count + ')', 'ext');
      chip.setAttribute('data-ext', item.ext);
      var off = this.excluded.has(item.ext);
      chip.setAttribute('aria-pressed', off ? 'false' : 'true');
      chip.classList.toggle('off', off);
      this.extsNode.appendChild(chip);
    }
  };

  function bodyRows(entry) {
    if (entry.want !== null && entry.state === 'loaded' && entry.rows) {
      return Math.max(1, entry.rows.length);
    }
    return 1;
  }

  Panel.prototype.relayout = function () {
    var sizes = new Array(this.display.length);
    var maxLen = 0;
    var added = 0;
    var deleted = 0;

    for (var i = 0; i < this.display.length; i++) {
      var entry = this.display[i];
      // Разделитель + шапка файла + тело.
      sizes[i] = 2 + bodyRows(entry);
      if (entry.state === 'loaded' && entry.maxLen > maxLen) {
        maxLen = entry.maxLen;
      }
      added += entry.a || 0;
      deleted += entry.d || 0;
    }

    this.layout = M.buildLayout(sizes);

    // Ширина под самую длинную строку (не длиннее обрезки) — для горизонтальной прокрутки.
    // Все строки одной ширины, фон добавленных и удалённых тянется до края.
    var width = Math.min(maxLen, M.LONG_LINE) + 34;
    this.main.sizer.style.minWidth = width + 'ch';

    this.summaryNode.textContent = this.status === 'ready'
      ? this.display.length + ' из ' + this.entries.length + ' файлов · +' + added + ' −' + deleted
      : '';

    if (!this.visible) {
      return;
    }

    this.toc.setCount(this.display.length);
    this.main.setCount(this.layout.total);
  };

  Panel.prototype.locateRow = function (index) {
    var block = M.locate(this.layout, index);
    if (block < 0) {
      return null;
    }

    var entry = this.display[block];
    var offset = index - this.layout.starts[block];
    var line = null;
    if (offset >= 2 && entry.state === 'loaded' && entry.want !== null && entry.rows) {
      line = entry.rows[offset - 2] || null;
    }

    return { entry: entry, offset: offset, line: line };
  };

  function stats(entry) {
    if (entry.a === null || entry.d === null) {
      return entry.c === 'binary' ? 'бинарный' : '';
    }
    return '+' + entry.a + ' −' + entry.d;
  }

  function splitPath(path) {
    var slash = path.lastIndexOf('/');
    return slash >= 0
      ? { dir: path.slice(0, slash + 1), name: path.slice(slash + 1) }
      : { dir: '', name: path };
  }

  Panel.prototype.renderTocRow = function (node, index) {
    var entry = this.display[index];
    node.textContent = '';
    node.className = 'vl-row toc-row';
    if (!entry) {
      return;
    }

    var kind = el('span', 'toc-kind k-' + entry.k, KIND_LABEL[entry.k] || entry.k);
    kind.title = KIND_TITLE[entry.k] || '';
    var parts = splitPath(entry.p);
    var path = el('span', 'toc-path');
    path.appendChild(el('span', 'toc-dir', parts.dir));
    path.appendChild(el('span', 'toc-name', parts.name));
    node.title = entry.o ? entry.o + ' → ' + entry.p : entry.p;

    node.appendChild(kind);
    node.appendChild(path);

    if (COLLAPSE_BADGE[entry.c]) {
      node.appendChild(el('span', 'badge', COLLAPSE_BADGE[entry.c]));
    }

    node.appendChild(el('span', 'toc-stat', stats(entry)));
  };

  Panel.prototype.renderMainRow = function (node, index) {
    node.textContent = '';
    var located = this.locateRow(index);
    if (!located) {
      node.className = 'vl-row';
      return;
    }

    var entry = located.entry;

    if (located.offset === 0) {
      node.className = 'vl-row gap-row';
      return;
    }

    if (located.offset === 1) {
      this.renderFileHeader(node, entry);
      return;
    }

    if (located.line) {
      this.renderLine(node, located.line);
      return;
    }

    this.renderPlaceholder(node, entry);
  };

  Panel.prototype.renderFileHeader = function (node, entry) {
    node.className = 'vl-row file-row';
    var inner = el('div', 'sticky');

    inner.appendChild(el('span', 'toc-kind k-' + entry.k, KIND_LABEL[entry.k] || entry.k));
    inner.appendChild(el('span', 'file-path', entry.o ? entry.o + ' → ' + entry.p : entry.p));
    inner.appendChild(el('span', 'toc-stat', stats(entry)));

    if (COLLAPSE_BADGE[entry.c]) {
      inner.appendChild(el('span', 'badge', COLLAPSE_BADGE[entry.c]));
    }

    if (entry.c !== 'binary') {
      inner.appendChild(button('row-btn', entry.want !== null ? 'Свернуть' : 'Развернуть', 'toggle'));
      inner.appendChild(button('row-btn', entry.ctx === 'full' && entry.want !== null ? 'Только изменения' : 'Весь файл', 'full'));
    }

    node.appendChild(inner);
  };

  Panel.prototype.renderPlaceholder = function (node, entry) {
    node.className = 'vl-row note-row';
    var inner = el('div', 'sticky');

    if (entry.c === 'binary') {
      inner.textContent = 'Бинарный файл — не показывается';
    } else if (entry.want === null) {
      var reason = entry.c === 'large' ? 'Большой diff. '
        : entry.c === 'generated' ? 'Сгенерированный файл. '
          : entry.state === 'error' ? entry.error + ' '
            : entry.userCollapsed ? 'Свёрнут. '
              : 'Не раскрыт автоматически. ';
      inner.appendChild(document.createTextNode(reason));
      inner.appendChild(button('row-btn', 'Загрузить diff', 'load'));
    } else if (entry.state === 'loading' || entry.state === 'idle') {
      inner.textContent = 'Загрузка…';
    } else if (entry.state === 'error') {
      inner.classList.add('error');
      inner.textContent = entry.error;
    } else if (entry.state === 'truncated') {
      inner.textContent = 'Слишком большой для показа.';
    } else {
      inner.textContent = 'Изменений в тексте нет.';
    }

    node.appendChild(inner);
  };

  Panel.prototype.renderLine = function (node, line) {
    node.className = 'vl-row line-row t-' + line.t;

    var oldNo = el('span', 'no', line.o > 0 ? String(line.o) : '');
    var newNo = el('span', 'no', line.n > 0 ? String(line.n) : '');
    var sign = el('span', 'sign', line.t === M.ROW_ADD ? '+' : line.t === M.ROW_DEL ? '−' : '');
    var clipped = M.clipLine(line.s);
    var code = el('span', 'code', clipped.text);

    node.appendChild(oldNo);
    node.appendChild(newNo);
    node.appendChild(sign);
    node.appendChild(code);

    if (clipped.cut) {
      node.appendChild(button('more', '…показать целиком', 'more'));
    }
  };

  // ---------------------------------------------------------------------------------------
  // Все панели страницы. Показ следует за терминалом: видна только панель показанной вкладки.
  // ---------------------------------------------------------------------------------------
  function Manager(options) {
    this.host = options.host;
    this.post = options.post;
    this.onClosed = typeof options.onClosed === 'function' ? options.onClosed : function () {};
    this.panels = new Map();
    this.shownId = null;

    // Окно изменило размер: видимой панели нужно больше (или меньше) строк в окне,
    // а шапкам файлов — новая ширина. Наблюдатель один на все панели.
    var self = this;
    new ResizeObserver(function () {
      self.panels.forEach(function (panel) {
        if (panel.visible) {
          panel.toc.schedule();
          panel.main.invalidate();
        }
      });
    }).observe(this.host);
  }

  Manager.prototype.isOpen = function (id) {
    return this.panels.has(id);
  };

  Manager.prototype.ensure = function (id) {
    var panel = this.panels.get(id);
    if (panel) {
      return panel;
    }

    panel = new Panel(this, id);
    this.panels.set(id, panel);
    if (this.shownId === id) {
      panel.setVisible(true);
      this.focus(panel);
    }
    return panel;
  };

  Manager.prototype.focus = function (panel) {
    var self = this;
    requestAnimationFrame(function () {
      // Пока шёл кадр, панель могли закрыть или вкладку сменить.
      if (self.panels.get(panel.id) === panel && panel.visible) {
        panel.root.focus();
      }
    });
  };

  // Вызывается при каждом show терминала.
  Manager.prototype.show = function (id) {
    this.shownId = id;
    var target = null;

    this.panels.forEach(function (panel) {
      panel.setVisible(panel.id === id);
      if (panel.id === id) {
        target = panel;
      }
    });

    if (target) {
      this.focus(target);
    }
  };

  // Закрытие терминала убирает и панель; C# об этом не сообщаем — он сам закрыл вкладку.
  Manager.prototype.remove = function (id) {
    var panel = this.panels.get(id);
    if (panel) {
      this.panels.delete(id);
      panel.destroy();
    }
  };

  // Закрывает панель по Esc или кнопке и сообщает C#. Со стороны C# панель не закрывается:
  // такого сообщения в протоколе нет.
  Manager.prototype.closeByUser = function (id) {
    var panel = this.panels.get(id);
    if (!panel) {
      return;
    }

    var wasVisible = panel.visible;
    this.panels.delete(id);
    panel.destroy();

    this.post({ type: 'diff.closed', id: id });
    this.onClosed(id, wasVisible);
  };

  Manager.prototype.handle = function (message) {
    var id = message.id;

    // Панель создаёт только diff.pending (координатор всегда шлёт его первым). Всё остальное
    // для несуществующей панели игнорируется: иначе запоздалый diff.index поднял бы панель,
    // которую человек уже закрыл, а C# вкладку уже забыл — «Загрузка…» навсегда.
    if (message.type === 'diff.pending') {
      this.ensure(id).onPending();
      return;
    }

    var panel = this.panels.get(id);
    if (!panel) {
      return;
    }

    switch (message.type) {
      case 'diff.index':
        panel.onIndex(message);
        break;
      case 'diff.error':
        panel.onError(message);
        break;
      case 'diff.file':
        panel.onFile(message);
        break;
      case 'diff.stale':
        panel.onStale();
        break;
      default:
        break;
    }
  };

  root.DiffPanels = {
    create: function (options) {
      return new Manager(options);
    }
  };
})(window);
