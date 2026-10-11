import { withDefaults } from '../lib/tracker.js';
import { normalizeDomain } from '../lib/classify.js';

const page = document.getElementById('page');
let settings = null;
let tab = 'projects';
try { tab = localStorage.getItem('bt-tab') || 'projects'; } catch { /* storage blocked */ }

function h(tag, attrs = {}, ...kids) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs || {})) {
    if (v == null || v === false) continue;
    if (k === 'class') el.className = v;
    else if (k.startsWith('on')) el.addEventListener(k.slice(2), v);
    else if (['checked', 'value', 'disabled'].includes(k)) el[k] = v;
    else el.setAttribute(k, v === true ? '' : v);
  }
  for (const c of kids.flat()) if (c != null && c !== false) el.append(c.nodeType ? c : String(c));
  return el;
}

async function load() {
  const { settings: s } = await chrome.storage.local.get(['settings']);
  settings = withDefaults(s);
}
async function save(fn) {
  await load();
  fn(settings);
  await chrome.storage.local.set({ settings });
  render();
}

/** Button that asks "Точно?" on the first click instead of a blocking confirm(). */
function confirmBtn(label, action, cls = 'btn quiet') {
  const b = h('button', { class: cls });
  b.textContent = label;
  b.addEventListener('click', () => {
    if (b.dataset.armed) return action();
    b.dataset.armed = '1';
    b.textContent = 'Точно?';
    b.classList.add('danger');
    setTimeout(() => { delete b.dataset.armed; b.textContent = label; b.classList.remove('danger'); }, 3000);
  });
  return b;
}

function header(title, lead) {
  return [h('h1', {}, title), h('p', { class: 'lead' }, lead)];
}

function projectsTab() {
  const names = Object.keys(settings.projects).sort((a, b) => (a === 'Inweb') - (b === 'Inweb') || a.localeCompare(b));
  const input = h('input', { type: 'text', placeholder: 'домен клієнта (shop.com.ua) або назва внутрішнього проєкту' });
  const add = () => {
    const raw = input.value.trim();
    if (!raw) return;
    const d = normalizeDomain(raw);
    save((s) => { s.projects[d || raw] = d ? { aliases: [] } : { internal: true, aliases: [] }; });
  };
  input.addEventListener('keydown', (e) => { if (e.key === 'Enter') add(); });
  return [
    ...header('Проєкти', 'Проєкт = домен клієнта, як у формі звіту. Нові проєкти з’являються самі з назв документів «домен / документ / задача». Псевдоніми зливають кілька доменів в один проєкт: старий домен після міграції, мовні версії.'),
    h('div', { class: 'panel' }, names.length ? names.map((n) => {
      const p = settings.projects[n];
      const aliases = h('input', { type: 'text', value: (p.aliases || []).join(', '), placeholder: 'псевдоніми через кому: old-domain.com.ua, site.lv',
        onchange: (e) => save((s) => {
          s.projects[n].aliases = e.target.value.split(',').map((x) => normalizeDomain(x) || x.trim()).filter(Boolean);
        }) });
      return h('div', { class: 'item' },
        h('div', { class: 'grow' },
          h('div', { class: 'n' }, n, p.internal ? h('span', { class: 'tag' }, 'внутрішній') : null, p.archived ? h('span', { class: 'tag off' }, 'архів') : null),
          p.internal ? h('div', { class: 'meta' }, 'Документи «' + n + ' / …» ідуть сюди') : aliases),
        h('button', { class: 'btn quiet', onclick: () => save((s) => { s.projects[n].archived = !p.archived; }) }, p.archived ? 'Повернути' : 'В архів'),
        confirmBtn('Видалити', () => save((s) => {
          delete s.projects[n];
          s.rules = s.rules.filter((r) => r.project !== n);
        })));
    }) : h('div', { class: 'emptyp' }, 'Ще немає проєктів.')),
    h('div', { class: 'addrow' }, input, h('button', { class: 'btn primary', onclick: add }, 'Додати проєкт')),
  ];
}

function projectOptions(sel) {
  sel.append(h('option', { value: '' }, 'Проєкт…'));
  for (const n of Object.keys(settings.projects).sort()) sel.append(h('option', { value: n }, n));
  return sel;
}

function rulesTab() {
  const match = h('input', { type: 'text', placeholder: 'домен (forum.example.org) або частина URL (analytics.google.com/…/p123)' });
  const proj = projectOptions(h('select', { 'aria-label': 'Проєкт' }));
  const add = () => {
    const m = match.value.trim();
    if (!m || !proj.value) return;
    save((s) => { s.rules = [...s.rules, { match: normalizeDomain(m) || m, project: proj.value }]; });
  };
  return [
    ...header('Правила', 'Для сторінок без домену клієнта в адресі: GA4, Looker Studio, сайти, що стабільно стосуються одного клієнта. Правило перевіряється першим. Домен підхоплює і піддомени, інше — пошук частини URL.'),
    h('div', { class: 'panel' }, settings.rules.length ? settings.rules.map((r, i) => h('div', { class: 'item' },
      h('div', { class: 'grow' }, h('div', { class: 'n' }, r.match), h('div', { class: 'meta' }, `→ ${r.project}`)),
      h('button', { class: 'btn quiet', onclick: () => save((s) => { s.rules = s.rules.filter((_, j) => j !== i); }) }, 'Видалити'))) :
      h('div', { class: 'emptyp' }, 'Правил немає. Їх зручно створювати з попапу: перенеси запис з «Інше» в проєкт, і BT запропонує правило.')),
    h('div', { class: 'addrow' }, match, proj, h('button', { class: 'btn primary', onclick: add }, 'Додати правило')),
  ];
}

function listEditor(key, placeholder, empty) {
  const input = h('input', { type: 'text', placeholder });
  const add = () => {
    const d = normalizeDomain(input.value);
    if (!d) { input.focus(); return; }
    save((s) => { s[key] = [...new Set([...(s[key] || []), d])]; });
  };
  input.addEventListener('keydown', (e) => { if (e.key === 'Enter') add(); });
  const list = settings[key] || [];
  return [
    h('div', { class: 'panel' }, list.length ? list.map((d) => h('div', { class: 'item' },
      h('div', { class: 'grow n' }, d),
      h('button', { class: 'btn quiet', onclick: () => save((s) => { s[key] = s[key].filter((x) => x !== d); }) }, 'Прибрати'))) :
      h('div', { class: 'emptyp' }, empty)),
    h('div', { class: 'addrow' }, input, h('button', { class: 'btn primary', onclick: add }, 'Додати')),
  ];
}

function ignoreTab() {
  return [
    ...header('Ігнор', 'Домени, які BT не записує взагалі: магазини, YouTube, банк. Піддомени теж.'),
    ...listEditor('ignore', 'youtube.com', 'Список порожній.'),
    h('h1', { style: 'font-size:16px;margin-top:34px' }, 'Не пропонувати як проєкт'),
    h('p', { class: 'lead' }, 'Домени, на яких ти натиснув «Ні, ніколи». Вони записуються в «Інше», але BT більше не питає.'),
    ...listEditor('declined', 'example.org', 'Список порожній.'),
  ];
}

function trackingTab() {
  const saved = h('span', { class: 'saved' }, 'збережено');
  const flash = () => { saved.classList.add('show'); setTimeout(() => saved.classList.remove('show'), 1200); };
  const idle = h('input', { type: 'number', min: '1', max: '30', step: '1', value: String(settings.idleMin),
    onchange: async (e) => {
      const v = Math.min(30, Math.max(1, Math.round(Number(e.target.value) || 3)));
      await save((s) => { s.idleMin = v; });
      flash();
    } });
  const round = h('select', { onchange: async (e) => { await save((s) => { s.roundStep = Number(e.target.value); }); flash(); } },
    [1, 5, 10, 15].map((n) => h('option', { value: String(n) }, n === 1 ? 'до хвилини' : `до ${n} хв`)));
  round.value = String(settings.roundStep);
  return [
    ...header('Облік', 'Коли BT ставить облік на паузу і як округлює підсумок проєкту.'),
    h('div', { class: 'panel' },
      h('div', { class: 'field' }, h('div', {}, h('div', {}, 'Простій, хв'), h('div', { class: 'help' }, 'Без миші й клавіатури довше — облік на паузі, потім питання «додати чи викинути».')), idle),
      h('div', { class: 'field' }, h('div', {}, h('div', {}, 'Округлення підсумку'), h('div', { class: 'help' }, 'Лише підсумок проєкту, рядки завжди точні. Посередині — вгору.')), round)),
    saved,
  ];
}

function dataTab() {
  const file = h('input', { type: 'file', accept: 'application/json', hidden: true });
  const status = h('p', { class: 'lead' });
  file.addEventListener('change', async () => {
    const f = file.files?.[0];
    if (!f) return;
    try {
      const parsed = JSON.parse(await f.text());
      const inc = parsed.settings || parsed;
      if (typeof inc !== 'object' || typeof inc.projects !== 'object') throw new Error('немає списку проєктів');
      await save((s) => {
        for (const [n, p] of Object.entries(inc.projects)) if (!s.projects[n]) s.projects[n] = { aliases: [], ...p };
        const keyOf = (r) => `${r.match}→${r.project}`;
        const have = new Set(s.rules.map(keyOf));
        for (const r of inc.rules || []) if (r?.match && r?.project && !have.has(keyOf(r))) s.rules.push({ match: String(r.match), project: String(r.project) });
        s.ignore = [...new Set([...s.ignore, ...(inc.ignore || []).map(normalizeDomain).filter(Boolean)])];
      });
      status.textContent = 'Імпортовано: нові проєкти, правила й ігнор додано до наявних.';
    } catch (e) {
      status.textContent = `Не вдалося прочитати файл: ${e.message}`;
    }
  });
  const exportBtn = h('button', { class: 'btn primary', onclick: () => {
    const { projects, rules, ignore } = settings;
    const blob = new Blob([JSON.stringify({ app: 'bison-time-checker', settings: { projects, rules, ignore } }, null, 2)], { type: 'application/json' });
    const a = h('a', { href: URL.createObjectURL(blob), download: 'bt-settings.json' });
    a.click();
    setTimeout(() => URL.revokeObjectURL(a.href), 1000);
  } }, 'Експорт налаштувань');
  return [
    ...header('Дані', 'Експорт містить лише проєкти, правила та ігнор — без історії й без адрес сторінок. Ним зручно поділитися з колегою: імпорт додає до наявного, нічого не стирає.'),
    h('div', { class: 'addrow' }, exportBtn, h('button', { class: 'btn', onclick: () => file.click() }, 'Імпорт…'), file),
    status,
    h('h1', { style: 'font-size:16px;margin-top:34px' }, 'Очистити історію'),
    h('p', { class: 'lead' }, 'Видаляє всі записані відрізки, коментарі та позначки. Проєкти, правила й ігнор лишаються.'),
    confirmBtn('Очистити історію', async () => {
      const all = await chrome.storage.local.get(null);
      const keys = Object.keys(all).filter((k) => k.startsWith('seg:') || k.startsWith('day:') || ['open', 'docTitles', 'idlePending'].includes(k));
      await chrome.storage.local.remove(keys);
      status.textContent = 'Історію очищено.';
    }, 'btn danger'),
  ];
}

const TABS = { projects: projectsTab, rules: rulesTab, ignore: ignoreTab, tracking: trackingTab, data: dataTab };

function render() {
  if (!TABS[tab]) tab = 'projects';
  for (const b of document.querySelectorAll('#tabs button')) b.setAttribute('aria-selected', String(b.dataset.tab === tab));
  page.replaceChildren(...TABS[tab]());
}

document.getElementById('tabs').addEventListener('click', (e) => {
  const b = e.target.closest('button[data-tab]');
  if (!b) return;
  tab = b.dataset.tab;
  try { localStorage.setItem('bt-tab', tab); } catch { /* storage blocked */ }
  render();
});
document.getElementById('ver').textContent = `v${chrome.runtime.getManifest().version}`;
chrome.storage.onChanged.addListener((c, area) => {
  if (area === 'local' && c.settings && !document.activeElement?.matches('input, select')) load().then(render);
});

load().then(render);
