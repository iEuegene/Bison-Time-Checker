// Builds the day report shown in the popup from raw segments + the day's manual state.
import { classify, projectIndex } from './classify.js';
import { MIN, roundMinutes } from './time.js';

export const GROUPS = [
  { id: 'docs', title: 'Документи' },
  { id: 'tools', title: 'Інструменти' },
  { id: 'site', title: 'Сайт' },
  { id: 'linked', title: 'Інші сервіси' },
  { id: 'manual', title: 'Вручну' },
];

const MERGE_GAP = 2 * MIN;        // pieces closer than this read as one visit
const COMMENT_MIN = 5 * MIN;      // a source enters the comment draft from 5 min a day
const SUGGEST_MIN = 15 * MIN;     // an unknown domain is offered as a project from 15 min a day
const TOP_PAGES = 5;

export function emptyDayState() {
  return { disabled: {}, manual: [], moved: {}, comments: {}, commentBase: {}, done: {}, idle: [] };
}

/** "Виправлення помилок" -> "виправлення помилок", leaves "SEO-аудит" alone. */
function lowerFirst(s) {
  if (s.length > 1 && s[1] === s[1].toLowerCase() && s[1] !== s[1].toUpperCase()) return s[0].toLowerCase() + s.slice(1);
  if (s.length === 1) return s.toLowerCase();
  return s;
}
function upperFirst(s) {
  return s ? s[0].toUpperCase() + s.slice(1) : s;
}

function toolPhrase(tool) {
  if (tool === 'Search Console') return 'аналіз даних Search Console';
  return `аналіз сайту в ${tool}`;
}

/** Rule-built comment draft: only facts BT knows, biggest first. */
export function draftComment(project) {
  const parts = [];
  for (const g of project.groups) {
    for (const r of g.rows) {
      if (r.disabled || r.ms < COMMENT_MIN) continue;
      let phrase = '';
      if (g.id === 'docs') phrase = r.task ? `${r.label} — ${lowerFirst(r.task)}` : r.label;
      else if (g.id === 'tools') phrase = toolPhrase(r.tool);
      else if (g.id === 'site') phrase = 'перевірка сайту';
      if (phrase) parts.push({ phrase, ms: r.ms });
    }
  }
  parts.sort((a, b) => b.ms - a.ms);
  const seen = new Set();
  const out = [];
  for (const p of parts) {
    const k = p.phrase.toLowerCase();
    if (!seen.has(k)) { seen.add(k); out.push(out.length ? lowerFirst(p.phrase) : p.phrase); }
  }
  return upperFirst(out.join('; '));
}

/**
 * @param {Array<{s:number,e:number,url:string,title:string}>} segs  the day's segments, open one included
 * @param {object} state     day state (see emptyDayState)
 * @param {object} settings
 * @param {object} docTitles
 * @param {{roundStep?:number}} [opts]
 */
export function buildReport(segs, state, settings, docTitles = {}, opts = {}) {
  const step = opts.roundStep ?? settings.roundStep ?? 5;
  const st = { ...emptyDayState(), ...state };
  const idx = projectIndex(settings);
  const projects = new Map();
  const timeline = [];

  const proj = (name) => {
    if (!projects.has(name)) projects.set(name, { name, rows: new Map() });
    return projects.get(name);
  };
  const addRow = (p, group, rowKey, ms, extra) => {
    const key = `${p.name}|${rowKey}`;
    let r = p.rows.get(key);
    if (!r) {
      r = { key, rowKey, group, ms: 0, disabled: !!st.disabled[key], ...extra, pages: new Map() };
      p.rows.set(key, r);
    }
    r.ms += ms;
    return r;
  };

  const sorted = [...segs].filter((x) => x.e > x.s).sort((a, b) => a.s - b.s);
  for (const seg of sorted) {
    let c;
    try { c = classify(seg, settings, docTitles, idx); } catch { continue; }
    const ms = seg.e - seg.s;
    const last = timeline.at(-1);

    if (c.project) {
      const p = proj(c.project);
      const r = addRow(p, c.group, c.rowKey, ms, { label: c.label, task: c.task, tool: c.tool });
      if (c.group === 'site') r.pages.set(c.path, (r.pages.get(c.path) || 0) + ms);
      if (last && last.kind === 'session' && last.project === c.project && seg.s - last.e <= MERGE_GAP) {
        last.e = Math.max(last.e, seg.e);
      } else {
        timeline.push({ kind: 'session', project: c.project, s: seg.s, e: seg.e });
      }
    } else if (last && last.kind === 'other' && last.rowKey === c.rowKey && seg.s - last.e <= MERGE_GAP) {
      last.e = Math.max(last.e, seg.e);
      last.ms += ms;
      if (seg.title) last.title = seg.title;
    } else {
      timeline.push({ kind: 'other', key: `o:${seg.s}`, rowKey: c.rowKey, s: seg.s, e: seg.e, ms,
        label: c.label, title: seg.title || '', domain: c.domain, tool: c.tool || '' });
    }
  }

  // "Інше" entries moved into a project by hand.
  for (const t of timeline) {
    if (t.kind !== 'other') continue;
    const target = st.moved[t.key];
    if (target && (settings.projects || {})[target]) {
      t.moved = target;
      addRow(proj(target), 'manual', `moved:${t.key}`, t.ms, { label: `${t.label} (перенесено)` });
    }
  }
  for (const m of st.manual) {
    if (!(settings.projects || {})[m.project]) continue;
    addRow(proj(m.project), 'manual', `man:${m.id}`, m.min * MIN, { label: m.label || 'Вручну' });
  }

  const out = [];
  for (const p of projects.values()) {
    const rows = [...p.rows.values()];
    const groups = GROUPS.map((g) => ({
      id: g.id,
      title: g.title,
      rows: rows.filter((r) => r.group === g.id).sort((a, b) => b.ms - a.ms).map((r) => ({
        ...r,
        pages: [...r.pages.entries()].sort((a, b) => b[1] - a[1]).slice(0, TOP_PAGES).map(([path, ms]) => ({ path, ms })),
      })),
    })).filter((g) => g.rows.length);
    const exactMs = rows.filter((r) => !r.disabled).reduce((a, r) => a + r.ms, 0);
    const rounded = roundMinutes(exactMs, step);
    const project = { name: p.name, exactMs, rounded, groups };

    const auto = draftComment(project);
    const edited = typeof st.comments[p.name] === 'string';
    project.comment = {
      text: edited ? st.comments[p.name] : auto,
      auto,
      edited,
      stale: edited && st.commentBase[p.name] !== auto,
    };
    const mark = st.done[p.name];
    project.done = {
      marked: mark != null && rounded === mark,
      stale: mark != null && rounded !== mark,
      diff: mark != null ? rounded - mark : 0,
    };
    out.push(project);
  }
  out.sort((a, b) => b.exactMs - a.exactMs || a.name.localeCompare(b.name));

  const others = timeline.filter((t) => t.kind === 'other');
  const otherMs = others.filter((t) => !t.moved).reduce((a, t) => a + t.ms, 0);

  const byDomain = new Map();
  for (const t of others) {
    if (t.moved || t.tool || !t.domain || !t.domain.includes('.')) continue;
    byDomain.set(t.domain, (byDomain.get(t.domain) || 0) + t.ms);
  }
  const declined = new Set(settings.declined || []);
  const suggestions = [...byDomain.entries()]
    .filter(([d, ms]) => ms >= SUGGEST_MIN && !declined.has(d) && !(settings.projects || {})[d])
    .sort((a, b) => b[1] - a[1])
    .map(([domain, ms]) => ({ domain, ms }));

  return {
    projects: out,
    timeline,
    otherMs,
    dayRounded: out.reduce((a, p) => a + p.rounded, 0),
    suggestions,
  };
}
