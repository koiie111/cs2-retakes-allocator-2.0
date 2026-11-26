"use strict";

const stage = document.querySelector("#stage");
const layer = document.querySelector("#modal-layer");
const panel = document.querySelector("#panel");
const indicator = document.querySelector("#indicator");
const welcomePreference = "retake-17-welcome-dismissed";

const weaponNames = {
  m4a1: "M4A4", m4a1_silencer: "M4A1-S", ak47: "AK-47", famas: "FAMAS",
  galilar: "Galil AR", mp9: "MP9", mac10: "MAC-10", usp_silencer: "USP-S",
  glock: "Glock-18", p250: "P250", deagle: "Desert Eagle", awp: "AWP",
  ssg08: "SSG 08", none: "Выкл.", random: "Случайно", default: "По умолчанию"
};
const pistols = { CT: ["usp_silencer", "p250", "deagle"], T: ["glock", "p250", "deagle"] };
const slots = {
  primary: { title: "Основное", note: "Полная закупка", CT: ["m4a1_silencer", "m4a1", "famas"], T: ["ak47", "galilar"] },
  half: { title: "Малая закупка", note: "Раунд с меньшим бюджетом", CT: ["mp9", "famas"], T: ["mac10", "galilar"] },
  secondary: { title: "Пистолет", note: "С основным оружием", ...pistols },
  pistol: { title: "Пистолетный раунд", note: "Стартовое оружие", ...pistols },
  sniper: { title: "Снайперская винтовка", note: "Если выпадет по правилам сервера", common: ["none", "awp", "ssg08", "random"] }
};
const preferences = {
  CT: { primary: "m4a1_silencer", half: "mp9", secondary: "usp_silencer", pistol: "usp_silencer", sniper: "none" },
  T: { primary: "ak47", half: "mac10", secondary: "glock", pistol: "glock", sniper: "none" }
};
const state = {
  role: "new", scenario: "join", side: "CT", gearSide: "CT", screen: "", slot: "primary",
  activePoint: "auto", nextRound: "auto", spawnMarkers: false, enemy: "Выкл.", feedback: "",
  optionalFeatures: false, zeus: false, vote: "", skipWelcome: readWelcome(), previousFocus: null, roundTimer: 0, pending: null
};

function readWelcome() {
  try { return localStorage.getItem(welcomePreference) === "1"; }
  catch { return false; }
}

function setWelcome(skip) {
  state.skipWelcome = skip;
  try {
    if (skip) localStorage.setItem(welcomePreference, "1");
    else localStorage.removeItem(welcomePreference);
  } catch {
    // The current preview session still remembers the choice.
  }
}

function action(label, command, primary = false) {
  return `<button class="action ${primary ? "primary" : ""}" data-action="${command}">${label}</button>`;
}

function tile(title, note, command) {
  return `<button class="tile" data-action="${command}"><strong>${title}</strong><small>${note}</small></button>`;
}

function row(title, command, selected) {
  const pressed = selected === undefined ? "" : `aria-pressed="${selected}"`;
  return `<button class="row" data-action="${command}" ${pressed}>${title}<span aria-hidden="true">${selected ? "✓" : "›"}</span></button>`;
}

function weapon(key, label, command, selected) {
  const image = ["none", "random", "default"].includes(key) ? "" :
    `<img src="assets/equipment/${key}.svg" alt="">`;
  const pressed = selected === undefined ? "" : `aria-pressed="${selected}"`;
  return `<button class="tile weapon" data-action="${command}" ${pressed}>${image}${label}<b>${weaponNames[key]}</b></button>`;
}

function heading(title, copy = "", eyebrow = "RETAKE #17") {
  return `<div class="eyebrow">${eyebrow}</div><h2 id="panel-title">${title}</h2>${copy ? `<p class="panel-lede">${copy}</p>` : ""}`;
}

function footer(...buttons) {
  return `<div class="panel-footer">${buttons.join("")}</div>`;
}

function status() {
  return `<div class="panel-status" role="status">${state.feedback}</div>`;
}

function welcome() {
  return `<div class="intro">
      <div>${heading("Как играть в ретейк", "Бомба уже на точке A или B.")}</div>
      <figure class="hero" aria-label="Точка A на Mirage и агенты обеих сторон">
        <img class="agent agent-ct" src="assets/agent-ct.webp" alt="Агент защиты">
        <img class="agent agent-t" src="assets/agent-t.webp" alt="Агент атаки">
      </figure>
    </div>
    <div class="teams">
      <div class="team"><span class="team-icon" aria-hidden="true">◆</span><div><b>Защита · CT</b><p>Обезвредь бомбу.</p></div></div>
      <div class="team attack"><span class="team-icon" aria-hidden="true">▲</span><div><b>Атака · T</b><p>Защити бомбу до взрыва.</p></div></div>
    </div>
    ${footer(action("Понятно", "close", true), action("Команды", "commands"),
      `<label class="remember"><input type="checkbox" id="skip-welcome" ${state.skipWelcome ? "checked" : ""}>Больше не показывать</label>`)}`;
}

function help() {
  return heading("Ретейк") + `<div class="panel-body choices">
    ${tile("Выбрать оружие", "!guns", "gear")}
    ${tile("Как играть", "Задачи атаки и защиты", "welcome")}
    ${tile("Команды", "Список доступных команд", "commands")}
    ${state.role === "admin" ? tile("Админ-панель", "Раунд и настройки карты", "admin") : ""}
  </div>` + footer(action("В игру", "close", true));
}

function commands() {
  const entries = [
    ["!guns", "Выбрать оружие", "gear"],
    ["!retake", "Открыть справку", "help"],
    ["!gun", "Выбрать оружие по названию", ""],
    ["!awp / !ssg", "Настроить выбор снайперки", "sniper"],
    ["!enemy", "Выбрать оружие другой стороны", "extra"]
  ];
  if (state.optionalFeatures) entries.push(["!nextround", "Проголосовать за тип раунда", "vote"], ["!zeus", "Включить или выключить Zeus", "extra"]);
  return heading("Команды") + `<div class="panel-body">${entries.map(([cmd, label, target]) => {
    const body = `<kbd>${cmd}</kbd><span>${label}</span>`;
    return target ? `<button class="row" data-action="${target}">${body}</button>` : `<div class="row">${body}</div>`;
  }).join("")}</div>` + footer(action("Назад", "help"), action("В игру", "close", true));
}

function teamCaption() {
  return state.gearSide === "CT" ? '<span class="is-ct">◆ Защита · CT</span>' : '<span class="is-t">▲ Атака · T</span>';
}

function gear() {
  const other = state.gearSide === "CT" ? "T" : "CT";
  return heading("Снаряжение", teamCaption()) + `<div class="panel-body choices">${["primary", "secondary", "half", "pistol"].map(key =>
    weapon(preferences[state.gearSide][key], `<small>${slots[key].title}</small>`, `slot:${key}`)
  ).join("")}</div>` + (state.feedback ? status() : "") +
    footer(action("Дополнительно", "extra"), action(`Снаряжение ${other}`, "other-gear"), action("В игру", "close", true));
}

function choices() {
  const slot = slots[state.slot];
  const options = slot.common || [...slot[state.gearSide], "default", "random"];
  const current = preferences[state.gearSide][state.slot];
  return heading(slot.title, slot.note) + `<div class="panel-body choices">${options.map(key =>
    weapon(key, "", `set-weapon:${key}`, key === current)
  ).join("")}</div>` + status() + footer(action("Назад", state.slot === "sniper" ? "extra" : "gear-back"), action("В игру", "close", true));
}

function extra() {
  return heading("Дополнительно", teamCaption()) + `<div class="panel-body">
    ${row(`Снайперская винтовка · ${weaponNames[preferences[state.gearSide].sniper]}`, "sniper")}
    ${row(`Оружие другой стороны · ${state.enemy}`, "enemy")}
    ${state.optionalFeatures ? row("Zeus", "zeus", state.zeus) : ""}
  </div>` + (state.feedback ? status() : "") + footer(action("Назад", "gear-back"), action("В игру", "close", true));
}

function enemy() {
  return heading("Оружие другой стороны", "Когда разрешают правила сервера.") +
    `<div class="panel-body">${["Выкл.", "Только T", "Только CT", "Обе стороны"].map((option, i) =>
      row(option, `enemy:${i}`, state.enemy === option)
    ).join("")}</div>` + status() + footer(action("Назад", "extra"), action("В игру", "close", true));
}

function admin() {
  const round = { auto: "По правилам сервера", pistol: "Пистолетный", half: "Малая закупка", full: "Полная закупка" };
  return heading("Управление ретейком", "", "АДМИН · RETAKE #17") + `<div class="panel-body choices">
    ${tile("Точка следующего раунда", state.activePoint === "auto" ? "Авто" : state.activePoint.toUpperCase(), "point")}
    ${tile("Тип следующего раунда", round[state.nextRound], "round-type")}
    ${tile("Перемешать команды", "Перед следующим раундом", "scramble")}
    ${tile("Служебные настройки", "Карты, спавны, очередь", "service")}
  </div>` + status() + footer(action("В игру", "close", true));
}

function adminOptions(title, records, selected, prefix) {
  return heading(title, "", "АДМИН · RETAKE #17") + `<div class="panel-body">${Object.entries(records).map(([key, label]) =>
    row(label, `${prefix}:${key}`, key === selected)
  ).join("")}</div>` + status() + footer(action("Назад", "admin"), action("В игру", "close", true));
}

function service(title, rows, back = "admin") {
  return heading(title, "", "АДМИН · RETAKE #17") + `<div class="panel-body">${rows.join("")}</div>` +
    status() + footer(action("Назад", back), action("В игру", "close", true));
}

const screens = {
  welcome, help, commands, gear, choices, extra, enemy, admin,
  vote: () => heading("Следующий раунд") + `<div class="panel-body">${[
    ["pistol", "Пистолетный"], ["half", "Малая закупка"], ["full", "Полная закупка"]
  ].map(([key, label]) => row(label, `vote-for:${key}`, state.vote === key)).join("")}</div>` + status() +
    footer(action("Назад", "commands"), action("В игру", "close", true)),
  point: () => adminOptions("Точка следующего раунда", { a: "Точка A", b: "Точка B", auto: "Авто" }, state.activePoint, "set-point"),
  "round-type": () => adminOptions("Тип следующего раунда", { pistol: "Пистолетный", half: "Малая закупка", full: "Полная закупка" }, state.nextRound, "set-round"),
  service: () => service("Служебные настройки", [
    row("Конфигурации карт", "maps"), row("Редактор спавнов", "spawns"), row("Диагностика очереди", "queue-debug")
  ]),
  maps: () => service("Конфигурации карт", [
    '<div class="row"><span>Текущая карта</span><kbd>de_mirage</kbd></div>',
    row("Загрузить стандартную конфигурацию Mirage", "map-load"),
    row("Посмотреть точки спавна", "spawns")
  ], "service"),
  spawns: () => service("Редактор спавнов", [
    row(state.spawnMarkers ? "Скрыть спавны" : "Показать спавны", "spawn-markers"),
    row("Найти ближайший спавн", "spawn-nearest"),
    row("Добавить спавн в текущей позиции", "spawn-add"),
    row("Удалить ближайший спавн", "spawn-delete"),
    row("Завершить редактирование", "spawn-end")
  ], "service"),
  "queue-debug": () => service("Диагностика очереди", [
    '<div class="row"><span>Играют</span><kbd>10 / 10</kbd></div>',
    '<div class="row"><span>Ждут</span><kbd>2</kbd></div>',
    '<div class="row"><span>Наблюдают</span><kbd>1</kbd></div>'
  ], "service"),
  confirm: () => heading(state.pending.title, "", "АДМИН · RETAKE #17") +
    footer(action("Подтвердить", "confirm-action", true), action("Отмена", state.pending.from))
};
const adminScreens = new Set(["admin", "point", "round-type", "service", "maps", "spawns", "queue-debug", "confirm"]);

function renderPanel(focusAction) {
  panel.classList.toggle("welcome", state.screen === "welcome");
  panel.innerHTML = '<button class="close" aria-label="Закрыть" data-action="close">×</button>' + screens[state.screen]();
  panel.tabIndex = -1;
  const sameAction = [...panel.querySelectorAll("button")].find(button => button.dataset.action === focusAction);
  (sameAction || panel).focus({ preventScroll: true });
}

function show(name) {
  if (adminScreens.has(name) && state.role !== "admin") return;
  if (name === "vote" && !state.optionalFeatures) return;
  if (layer.hidden) {
    state.previousFocus = document.activeElement;
    layer.hidden = false;
  }
  state.screen = name;
  state.feedback = "";
  renderPanel();
}

function closePanel() {
  if (layer.hidden) return;
  layer.hidden = true;
  state.screen = "";
  const target = state.previousFocus;
  if (target instanceof HTMLElement && target.isConnected && !target.closest("[hidden]")) {
    target.focus({ preventScroll: true });
  }
}

function redraw(message, actionName) {
  state.feedback = message;
  renderPanel(actionName);
}

function setScenario(next) {
  state.scenario = next;
  clearTimeout(state.roundTimer);
  indicator.replaceChildren();
  closePanel();
  markSelected("scenario", next);
  document.querySelector("#game-clock").textContent = next === "join" || next === "queue" ? "—" : "0:29";
  if (next === "join" && state.role === "new" && !state.skipWelcome) show("welcome");
  if (next === "queue") {
    indicator.innerHTML = '<span class="point" aria-hidden="true">2</span><span>Ты второй в очереди</span>';
  }
  if (next === "a" || next === "b") showRound();
}

function showRound() {
  clearTimeout(state.roundTimer);
  const goal = state.side === "CT" ? "Обезвредь бомбу" : "Защити бомбу до взрыва";
  const site = state.activePoint === "auto" ? state.scenario : state.activePoint;
  indicator.innerHTML = `<span class="point" aria-label="Точка ${site.toUpperCase()}">${site.toUpperCase()}</span><span>${goal}</span>`;
  state.roundTimer = setTimeout(() => indicator.replaceChildren(), 6500);
  state.nextRound = "auto";
}

function markSelected(type, value) {
  document.querySelectorAll(`[data-${type}]`).forEach(button =>
    button.setAttribute("aria-pressed", String(button.dataset[type] === value)));
}

function openCommand(name) {
  if (name === "gear") state.gearSide = state.side;
  show(name);
}

function confirm(title, from, result) {
  state.pending = { title, from, result };
  show("confirm");
}

function activate(command) {
  const [name, value] = command.split(":");
  if (!value && Object.hasOwn(screens, name) && !["confirm", "choices"].includes(name)) return openCommand(name);
  switch (name) {
    case "close": closePanel(); break;
    case "gear-back": show("gear"); break;
    case "other-gear":
      state.gearSide = state.gearSide === "CT" ? "T" : "CT";
      show("gear");
      break;
    case "sniper": showSlot("sniper"); break;
    case "slot": showSlot(value); break;
    case "set-weapon":
      preferences[state.gearSide][state.slot] = value;
      redraw(`Выбрано · ${weaponNames[value]}`, command);
      break;
    case "enemy":
      state.enemy = ["Выкл.", "Только T", "Только CT", "Обе стороны"][Number(value)];
      redraw("Настройка сохранена", command);
      break;
    case "zeus":
      state.zeus = !state.zeus;
      redraw(state.zeus ? "Zeus включён" : "Zeus выключен", command);
      break;
    case "vote-for":
      state.vote = value;
      redraw("Голос принят", command);
      break;
    case "set-point":
      state.activePoint = value;
      redraw(value === "auto" ? "Точку выбирает сервер" : `Выбрана точка ${value.toUpperCase()}`, command);
      break;
    case "set-round":
      state.nextRound = value;
      redraw("Тип следующего раунда выбран", command);
      break;
    case "scramble": confirm("Перемешать команды?", "admin", "Команды перемешаются перед следующим раундом"); break;
    case "map-load": confirm("Загрузить стандартную конфигурацию?", "maps", "Конфигурация Mirage загружена"); break;
    case "spawn-delete": confirm("Удалить ближайший спавн?", "spawns", "Ближайший спавн удалён"); break;
    case "confirm-action": {
      const { from, result } = state.pending;
      show(from);
      redraw(result);
      state.pending = null;
      break;
    }
    case "spawn-markers":
      state.spawnMarkers = !state.spawnMarkers;
      redraw(state.spawnMarkers ? "Спавны показаны" : "Спавны скрыты", command);
      break;
    case "spawn-nearest": redraw("Ближайший спавн · точка A · защита", command); break;
    case "spawn-add": redraw("Добавлен спавн · точка A · защита", command); break;
    case "spawn-end":
      state.spawnMarkers = false;
      show("service");
      redraw("Редактирование завершено");
      break;
  }
}

function showSlot(slot) {
  state.slot = slot;
  show("choices");
}

panel.addEventListener("change", event => {
  if (event.target.id === "skip-welcome") setWelcome(event.target.checked);
});
panel.addEventListener("click", event => {
  const button = event.target.closest("[data-action]");
  if (!button) return;
  activate(button.dataset.action);
});

document.querySelector(".workspace").addEventListener("click", event => {
  const button = event.target.closest("button");
  if (!button || panel.contains(button) || button.dataset.action) return;
  const { role, scenario, ratio, light, open } = button.dataset;
  if (role) {
    state.role = role;
    markSelected("role", role);
    document.querySelector('[data-open="admin"]').hidden = role !== "admin";
    document.querySelector("[data-features]").hidden = role !== "admin";
    setScenario("join");
    if (role === "admin") show("admin");
  }
  if (scenario) setScenario(scenario);
  if (ratio) {
    stage.classList.toggle("classic", ratio === "classic");
    markSelected("ratio", ratio);
  }
  if (light) {
    stage.classList.toggle("night", light === "night");
    markSelected("light", light);
  }
  if (open) openCommand(open);
  if (button.hasAttribute("data-side")) {
    state.side = state.side === "CT" ? "T" : "CT";
    button.textContent = `Сторона: ${state.side}`;
    if (state.scenario === "a" || state.scenario === "b") showRound();
  }
  if (button.hasAttribute("data-features")) {
    state.optionalFeatures = !state.optionalFeatures;
    button.setAttribute("aria-pressed", String(state.optionalFeatures));
    button.textContent = state.optionalFeatures ? "Вернуть конфиг #17" : "Показать выключенные функции";
    show("commands");
  }
  if (button.hasAttribute("data-reset")) {
    setWelcome(false);
    document.querySelector('[data-role="new"]').click();
  }
});

document.addEventListener("keydown", event => {
  if (event.key === "Escape" && !layer.hidden) {
    event.preventDefault();
    closePanel();
  }
  if (event.key === "Tab" && !layer.hidden) {
    const controls = [...panel.querySelectorAll("button, input")];
    const first = controls[0];
    const last = controls.at(-1);
    if (event.shiftKey && (document.activeElement === first || document.activeElement === panel)) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault();
      first.focus();
    }
  }
});

setScenario("join");

