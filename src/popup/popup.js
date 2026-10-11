import { buildReport, emptyDayState } from '../lib/report.js';
import { classify, isDomainLike } from '../lib/classify.js';
import { createTracker, withDefaults, KEEP_SEGMENTS_DAYS, OFF_REASONS } from '../lib/tracker.js';
import { dayKey, shiftDay, formatReport, formatExact, clock, MIN } from '../lib/time.js';

const store = {
  get: (keys) => chrome.storage.local.get(keys),
  set: (obj) => chrome.storage.local.set(obj),
  remove: (keys) => chrome.storage.local.remove(keys),
};
const tracker = createTracker({ store });

const ui = {
  day: dayKey(Date.now()),
  open: new Set(),          // expanded project cards
  pages: new Set(),         // expanded "Сайт" rows
  otherOpen: false,
  showShort: false,       // show <1 min visits in Інше
  ruleOffer: null,          // {domain, project} after a manual move
  copied: null,
};
let data = null;

// ---------- tiny DOM helper (textContent only: tab titles are untrusted) ----------
function h(tag, attrs = {}, ...kids) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs || {})) {
    if (v == null || v === false) continue;
    if (k === 'class') el.className = v;
    else if (k.startsWith('on')) el.addEventListener(k.slice(2), v);
    else if (k === 'checked' || k === 'value' || k === 'disabled' || k === 'hidden') el[k] = v;
    else el.setAttribute(k, v === true ? '' : v);
  }
  for (const c of kids.flat()) if (c != null && c !== false) el.append(c.nodeType ? c : String(c));
  return el;
}
const svgNS = 'http://www.w3.org/2000/svg';
function chevron() {
  const s = document.createElementNS(svgNS, 'svg');
  s.setAttribute('viewBox', '0 0 12 12');
  s.setAttribute('class', 'chev');
  s.setAttribute('aria-hidden', 'true');
  const p = document.createElementNS(svgNS, 'path');
  p.setAttribute('d', 'M2 4.5 6 8l4-3.5');
  s.append(p);
  return s;
}

// ---------- storage ----------
async function load() {
  const today = dayKey(Date.now());
  const dk = `day:${ui.day}`;
  const got = await store.get(['settings', 'docTitles', 'open', 'paused', dk]);
  const settings = withDefaults(got.settings);
  const state = { ...emptyDayState(), ...(got[dk] || {}) };
  const segs = await tracker.daySegments(ui.day);
  const report = buildReport(segs, state, settings, got.docTitles || {});
  let current = null;
  if (ui.day === today && got.open) {
    try { current = classify(got.open, settings, got.docTitles || {}).project; } catch { /* ignore */ }
  }
  const off = await tracker.dayOff(ui.day);
  data = { settings, state, report, current, today, paused: got.paused || null, off };
}

async function updateDay(fn) {
  const dk = `day:${ui.day}`;
  const got = await store.get([dk]);
  const state = { ...emptyDayState(), ...(got[dk] || {}) };
  fn(state);
  await store.set({ [dk]: state });
  await refresh();
}

async function updateSettings(fn) {
  const got = await store.get(['settings']);
  const s = withDefaults(got.settings);
  fn(s);
  await store.set({ settings: s });
  await refresh();
}

function toast(text) {
  let t = document.querySelector('.toast');
  if (!t) document.body.append((t = h('div', { class: 'toast' })));
  t.textContent = text;
  t.classList.add('show');
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => t.classList.remove('show'), 1400);
}

// ---------- render ----------
function dayLabel(key, today) {
  if (key === today) return 'Сьогодні';
  if (key === shiftDay(today, -1)) return 'Вчора';
  const [y, m, d] = key.split('-').map(Number);
  return new Intl.DateTimeFormat('uk', { weekday: 'short', day: 'numeric', month: 'short' }).format(new Date(y, m - 1, d));
}

function projectNames() {
  return Object.entries(data.settings.projects).filter(([, p]) => !p.archived).map(([n]) => n)
    .sort((a, b) => (a === 'Inweb') - (b === 'Inweb') || a.localeCompare(b));
}

function projectSelect(selected, onchange) {
  const sel = h('select', { onchange: (e) => onchange(e.target.value), 'aria-label': 'Проєкт' },
    h('option', { value: '' }, 'Проєкт…'),
    projectNames().map((n) => h('option', { value: n, selected: n === selected ? 'selected' : null }, n)));
  if (selected) sel.value = selected;
  return sel;
}

async function setPaused(on) {
  try { await chrome.runtime.sendMessage({ type: on ? 'pause' : 'resume' }); } catch { /* worker restarting */ }
  await refresh();
}

function pauseBar() {
  if (ui.day !== data.today) return null;
  if (data.paused) {
    const min = Math.floor((Date.now() - data.paused.s) / MIN);
    return h('div', { class: 'banner paused' },
      h('div', { class: 'msg' }, h('b', {}, 'На паузі'), ` з ${clock(data.paused.s)} · ${formatExact(min * MIN)}`,
        h('div', { class: 'keys' }, 'Час не рахується, доки не продовжиш')),
      h('button', { class: 'btn primary big', onclick: () => setPaused(false) }, '▶  Продовжити'));
  }
  return h('div', { class: 'runbar' },
    h('span', { class: 'live' }),
    h('span', { class: 'what' }, data.current ? `Облік іде · ${data.current}` : 'Облік іде'),
    h('button', { class: 'btn pausebtn', title: 'Пауза (Alt+Shift+P)', onclick: () => setPaused(true) }, '❚❚  Пауза'));
}

function offLine() {
  const parts = Object.entries(data.off).filter(([, ms]) => ms >= MIN).sort((a, b) => b[1] - a[1])
    .map(([r, ms]) => `${OFF_REASONS[r] || r} ${formatExact(ms)}`);
  if (!parts.length) return null;
  return h('div', { class: 'offline', title: 'Час, коли BT нічого не рахував, і чому' }, 'Поза обліком: ', parts.join(' · '));
}

function idleBanners() {
  return data.state.idle.map((it) => {
    const min = Math.round((it.e - it.s) / MIN);
    let target = it.project && data.settings.projects[it.project] ? it.project : '';
    const add = h('button', { class: 'btn primary', disabled: !target, onclick: () => updateDay((st) => {
      st.idle = st.idle.filter((x) => x.id !== it.id);
      st.manual.push({ id: `m${it.id}`, project: target, min, label: `Простій ${clock(it.s)}–${clock(it.e)}` });
    }) }, target ? `Додати до ${target}` : 'Додати');
    const acts = h('div', { class: 'acts' },
      !it.project ? projectSelect('', (v) => { target = v; add.disabled = !v; add.textContent = v ? `Додати до ${v}` : 'Додати'; }) : null,
      add,
      h('button', { class: 'btn quiet', onclick: () => updateDay((st) => { st.idle = st.idle.filter((x) => x.id !== it.id); }) }, 'Викинути'));
    return h('div', { class: 'banner idle' },
      h('div', { class: 'msg' }, h('b', {}, `${min} хв простою`), ` о ${clock(it.s)}`),
      acts);
  });
}

function suggestionBanners() {
  return data.report.suggestions.map((s) => h('div', { class: 'banner' },
    h('div', { class: 'msg' }, h('b', {}, s.domain), ` — ${formatExact(s.ms)} сьогодні. Додати як проєкт?`),
    h('div', { class: 'acts' },
      h('button', { class: 'btn primary', onclick: () => updateSettings((st) => { st.projects[s.domain] = { aliases: [] }; }) }, 'Додати'),
      h('button', { class: 'btn quiet', onclick: () => updateSettings((st) => { st.declined = [...new Set([...(st.declined || []), s.domain])]; }) }, 'Ні, ніколи'))));
}

function rowEl(p, r, groupId) {
  const off = r.disabled;
  const sw = h('input', { type: 'checkbox', class: 'switch', checked: !off, title: off ? 'Не враховується' : 'Враховується',
    'aria-label': `Враховувати: ${r.label}`,
    onchange: () => updateDay((st) => { if (off) delete st.disabled[r.key]; else st.disabled[r.key] = true; }) });
  const what = h('div', { class: 'what' }, h('div', { class: 'l', title: r.label }, r.label));
  if (r.task) what.append(h('div', { class: 't', title: r.task }, r.task));
  if (groupId === 'site' && r.pages.length) {
    const open = ui.pages.has(r.key);
    what.append(h('button', { class: 'more', onclick: () => { open ? ui.pages.delete(r.key) : ui.pages.add(r.key); render(); } },
      open ? 'сховати сторінки' : `топ-${r.pages.length} сторінок`));
  }
  const row = h('div', { class: `row${off ? ' off' : ''}` }, what, h('div', { class: 'dur' }, formatExact(r.ms)), sw);
  if (groupId === 'site' && ui.pages.has(r.key)) {
    return [row, h('div', { class: 'pages' }, r.pages.map((pg) => h('div', {}, h('span', { title: pg.path }, pg.path), h('span', {}, formatExact(pg.ms)))))];
  }
  return row;
}

function commentEl(p) {
  const c = p.comment;
  const ta = h('textarea', { 'aria-label': `Коментар для ${p.name}`, placeholder: 'Що зроблено — допиши, що BT не бачить', value: c.text,
    oninput: (e) => {
      clearTimeout(commentEl.t);
      const text = e.target.value;
      commentEl.t = setTimeout(async () => {
        const dk = `day:${ui.day}`;
        const got = await store.get([dk]);
        const st = { ...emptyDayState(), ...(got[dk] || {}) };
        st.comments[p.name] = text;
        st.commentBase[p.name] = c.auto;
        await store.set({ [dk]: st });
      }, 400);
    } });
  const hint = c.stale
    ? h('span', { class: 'hint warn' }, 'є нове — ',
      h('button', { class: 'more', onclick: () => updateDay((st) => { delete st.comments[p.name]; delete st.commentBase[p.name]; }) }, 'оновити'))
    : h('span', { class: 'hint' }, c.edited ? 'відредаговано' : 'чернетка');
  const done = p.done.marked;
  return h('div', { class: 'comment' }, ta, h('div', { class: 'bar' }, hint,
    h('button', { class: 'btn', onclick: async () => {
      await navigator.clipboard.writeText(ta.value);
      toast('Коментар скопійовано');
    } }, 'Копіювати'),
    h('button', { class: `btn ${done ? '' : 'primary'}`, onclick: () => updateDay((st) => {
      if (done) delete st.done[p.name]; else st.done[p.name] = p.rounded;
    }) }, done ? 'Внесено ✓' : 'Внесено')));
}

function cardEl(p) {
  const open = ui.open.has(p.name);
  const cls = ['card', open && 'open', p.rounded === 0 && 'zero', p.done.marked && 'done'].filter(Boolean).join(' ');
  const head = h('button', { class: 'head', 'aria-expanded': String(open), onclick: () => { open ? ui.open.delete(p.name) : ui.open.add(p.name); render(); } },
    p.name === data.current ? h('span', { class: 'now', title: 'Зараз працюєш тут' }) : null,
    h('span', { class: 'name', title: p.name }, p.name),
    p.done.marked ? h('span', { class: 'chip' }, 'внесено') : null,
    p.done.stale ? h('span', { class: 'chip warn' }, `змінилось ${p.done.diff > 0 ? '+' : ''}${p.done.diff} хв`) : null,
    h('span', { class: 'plate' }, h('span', {}, formatReport(p.rounded))),
    chevron());
  const body = h('div', { class: 'body' },
    p.groups.map((g) => h('div', { class: 'group' }, h('h4', {}, g.title), g.rows.map((r) => rowEl(p, r, g.id)))),
    h('div', { class: 'addtime' }, h('span', { class: 'lbl' }, `Точно: ${formatExact(p.exactMs)}`),
      [5, 15].map((n) => h('button', { class: 'btn', onclick: () => updateDay((st) => {
        st.manual.push({ id: `m${Date.now()}`, project: p.name, min: n, label: `Вручну +${n} хв` });
      }) }, `+${n} хв`))),
    commentEl(p));
  return h('section', { class: cls }, head, body);
}

function otherEl() {
  const tl = data.report.timeline;
  if (!tl.some((t) => t.kind === 'other')) return null;
  // Visits under a minute are noise for the memory aid: fold them away by default,
  // and glue project sessions that only they separated.
  const visible = [];
  let hidden = 0;
  for (const t of tl) {
    if (t.kind === 'other' && !t.moved && t.ms < MIN && !ui.showShort) { hidden++; continue; }
    const last = visible.at(-1);
    if (t.kind === 'session' && last?.kind === 'session' && last.project === t.project) {
      visible[visible.length - 1] = { ...last, e: Math.max(last.e, t.e) };
      continue;
    }
    visible.push(t);
  }
  const items = [];
  for (const t of visible) {
    if (t.kind === 'session') {
      items.push(h('div', { class: 'sess', title: `Робота над ${t.project}, ${clock(t.s)}–${clock(t.e)} — уже в проєкті` },
        h('span', { class: 'tm' }, clock(t.s)),
        h('span', { class: 'proj' }, h('i', {}), t.project, h('small', {}, ' · у проєкті')),
        h('span', { class: 'dur' }, `до ${clock(t.e)}`)));
      continue;
    }
    const domain = t.domain;
    const acts = h('div', { class: 'acts' });
    if (t.moved) {
      acts.append(h('span', { class: 'to' }, `→ ${t.moved}`),
        h('button', { class: 'btn quiet', onclick: () => updateDay((st) => { delete st.moved[t.key]; }) }, 'скасувати'));
      if (ui.ruleOffer && ui.ruleOffer.key === t.key) {
        const { project } = ui.ruleOffer;
        acts.append(h('span', { class: 't' }, `Завжди відносити ${domain} до ${project}?`),
          h('button', { class: 'btn primary', onclick: () => { ui.ruleOffer = null; updateSettings((s) => { s.rules = [...s.rules, { match: domain, project }]; }); } }, 'Так'),
          h('button', { class: 'btn quiet', onclick: () => { ui.ruleOffer = null; render(); } }, 'Ні'));
      }
    } else {
      acts.append(projectSelect('', (v) => {
        if (!v) return;
        if (isDomainLike(domain) && !t.tool) ui.ruleOffer = { key: t.key, project: v };
        updateDay((st) => { st.moved[t.key] = v; });
      }));
      if (isDomainLike(domain) && !t.tool) {
        acts.append(h('button', { class: 'btn quiet', title: `Ніколи не записувати ${domain}`, onclick: () =>
          updateSettings((s) => { s.ignore = [...new Set([...s.ignore, domain])]; }) }, 'Ігнорувати домен'));
      }
    }
    items.push(h('div', { class: `ent${t.moved ? ' moved' : ''}` },
      h('span', { class: 'tm' }, clock(t.s)),
      h('span', { class: 'l', title: t.label }, t.label),
      h('span', { class: 'dur' }, formatExact(t.ms)),
      t.title && t.title !== t.label ? h('span', { class: 't', title: t.title }, t.title) : null,
      acts));
  }
  const det = h('details', { class: 'other', open: ui.otherOpen ? true : null,
    ontoggle: (e) => { ui.otherOpen = e.target.open; } },
    h('summary', {}, 'Інше', h('span', { class: 'n' }, formatExact(data.report.otherMs)), chevron()),
    h('div', { class: 'tl' }, items,
      hidden || ui.showShort ? h('button', { class: 'more short', onclick: () => { ui.showShort = !ui.showShort; render(); } },
        ui.showShort ? 'сховати короткі відвідування' : `ще ${hidden} коротких відвідувань (<1 хв)`) : null));
  return det;
}

function render() {
  const { report, today } = data;
  document.getElementById('dayLabel').textContent = dayLabel(ui.day, today);
  document.getElementById('total').textContent = formatReport(report.dayRounded);
  document.getElementById('next').disabled = ui.day >= today;
  document.getElementById('prev').disabled = ui.day <= shiftDay(today, -(KEEP_SEGMENTS_DAYS - 1));

  const app = document.getElementById('app');
  const scroll = document.scrollingElement.scrollTop;
  const kids = [pauseBar(), ...idleBanners(), ...suggestionBanners(), ...report.projects.map(cardEl)];
  if (!report.projects.length) {
    kids.push(h('div', { class: 'empty' },
      h('div', { class: 'display' }, 'Поки порожньо'),
      'Відкрий документ з назвою ', h('code', {}, 'домен / документ / задача'), ' — BT підхопить проєкт сам.'));
  }
  kids.push(offLine(), otherEl());
  app.replaceChildren(...kids.filter(Boolean));
  document.scrollingElement.scrollTop = scroll;
}

async function refresh() {
  await load();
  render();
}

document.getElementById('prev').addEventListener('click', () => { ui.day = shiftDay(ui.day, -1); ui.open.clear(); refresh(); });
document.getElementById('next').addEventListener('click', () => { ui.day = shiftDay(ui.day, 1); ui.open.clear(); refresh(); });
document.getElementById('settings').addEventListener('click', () => chrome.runtime.openOptionsPage());

(async () => {
  try { await chrome.runtime.sendMessage({ type: 'sync' }); } catch { /* worker asleep: storage is still the truth */ }
  await load();
  if (data.current) ui.open.add(data.current);
  render();
  setInterval(() => {
    if (document.activeElement?.tagName === 'TEXTAREA' || document.activeElement?.tagName === 'SELECT') return;
    if (ui.day === dayKey(Date.now())) refresh();
  }, 30_000);
})();
