// Одна сцена при загрузке: лампы переходят из «работает» в итоговое состояние,
// счётчик растёт, приходит уведомление. Итоговое состояние уже записано в разметке,
// поэтому без JS и при prefers-reduced-motion страница выглядит так же, но статично.
(function () {
  "use strict";

  var board = document.querySelector(".board");
  if (!board) return;
  if (window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches) return;

  var LABELS = { working: "работает", waiting: "ждёт ввода", background: "работа в фоне" };
  var steps = Array.prototype.slice.call(board.querySelectorAll("[data-step]"))
    .sort(function (a, b) { return a.dataset.step - b.dataset.step; });
  var count = board.querySelector("[data-count]");
  var counterLamp = board.querySelector("[data-counter] .lamp");

  function setState(tab, state) {
    var lamp = tab.querySelector(".lamp");
    lamp.className = "lamp lamp--" + state + (state === "waiting" ? " lamp--lit" : "");
    tab.querySelector(".sr").textContent = LABELS[state];
  }

  function setCount(n) {
    count.textContent = String(n);
    counterLamp.className = "lamp " + (n > 0 ? "lamp--waiting lamp--lit" : "lamp--off");
  }

  // Стартовое состояние: все сессии работают, никто не ждёт.
  var finals = steps.map(function (tab) { return tab.dataset.state; });
  steps.forEach(function (tab) { setState(tab, "working"); });
  setCount(0);
  board.classList.add("is-intro");

  var waiting = 0;
  var t = 900;
  steps.forEach(function (tab, i) {
    setTimeout(function () {
      setState(tab, finals[i]);
      if (finals[i] === "waiting") setCount(++waiting);
    }, t);
    t += 1100;
  });

  // Уведомление — о последней сессии, перешедшей в «ждёт ввода».
  setTimeout(function () { board.classList.remove("is-intro"); }, t - 400);
})();
