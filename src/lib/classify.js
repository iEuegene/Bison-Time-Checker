// Maps a tab (url + title) to a project and a report row.
// Pure: no chrome.* calls, so it runs the same in the service worker, the popup and tests.
import psl from '../vendor/psl.mjs';

const DOC_SUFFIX =
  /\s+[-–—]\s+Google\s+(?:Docs|Sheets|Slides|Forms|Drawings|Документи|Таблиці|Презентації|Форми|Документы|Таблицы|Презентации|Формы)\s*$/iu;
const DOC_URL = /^\/(document|spreadsheets|presentation)\/(?:u\/\d+\/)?d\/([\w-]{10,})/;
const DOMAIN_RE = /^(?:[\p{L}\p{N}](?:[\p{L}\p{N}-]*[\p{L}\p{N}])?\.)+\p{L}{2,}$/u;
const IPV4 = /^\d{1,3}(?:\.\d{1,3}){3}$/;

/** SEO tools: host test + query params that carry the analysed domain. */
const TOOLS = [
  { name: 'Search Console', test: (u) => u.hostname === 'search.google.com' && u.pathname.startsWith('/search-console'), params: ['resource_id'] },
  { name: 'Ahrefs', test: (u) => hostIs(u.hostname, 'ahrefs.com'), params: ['target', 'input', 'url', 'domain'] },
  { name: 'Serpstat', test: (u) => hostIs(u.hostname, 'serpstat.com'), params: ['query', 'domain', 'url', 'q', 'target'] },
];

export function hostIs(host, domain) {
  return host === domain || host.endsWith('.' + domain);
}

/** "https://WWW.Ti.ua/path" -> "ti.ua"; "sc-domain:ti.ua" -> "ti.ua". Returns '' when not a domain. */
export function normalizeDomain(raw) {
  if (!raw) return '';
  let s = String(raw).trim().toLowerCase();
  s = s.replace(/^sc-domain:/, '').replace(/^[a-z][a-z0-9+.-]*:\/\//, '');
  s = s.split(/[/?#]/)[0].replace(/:\d+$/, '').replace(/^www\./, '').replace(/\.$/, '');
  return isDomainLike(s) ? s : '';
}

export function isDomainLike(s) {
  return !!s && !/\s/.test(s) && !IPV4.test(s) && DOMAIN_RE.test(s);
}

/** Registrable domain via the Public Suffix List: test.aqua-room.com.ua -> aqua-room.com.ua. */
export function registrable(host) {
  if (!host || IPV4.test(host) || !host.includes('.')) return host || '';
  return psl.get(host) || host;
}

/** lowercase key (project name, its domain, each alias) -> canonical project name. */
export function projectIndex(settings) {
  const idx = new Map();
  for (const [name, p] of Object.entries(settings.projects || {})) {
    if (p.archived) continue;
    idx.set(name.toLowerCase(), name);
    for (const a of p.aliases || []) {
      const d = normalizeDomain(a) || a.trim().toLowerCase();
      if (d) idx.set(d, name);
    }
  }
  return idx;
}

/** Known project for a host: exact host, then each parent up to the registrable domain. */
export function resolveDomain(host, idx) {
  const h = normalizeDomain(host);
  if (!h) return null;
  const labels = h.split('.');
  for (let i = 0; i < labels.length - 1; i++) {
    const hit = idx.get(labels.slice(i).join('.'));
    if (hit) return hit;
  }
  return null;
}

/**
 * Google Docs title "ti.ua / ТЗ №123 / Виправлення помилок - Google Docs"
 * -> { first: 'ti.ua', doc: 'ТЗ №123', task: 'Виправлення помилок' }. Needs at least two segments.
 */
export function parseDocTitle(title) {
  if (!title) return null;
  const clean = title.replace(DOC_SUFFIX, '').trim();
  const parts = clean.split(/\s+\/\s+/).map((s) => s.trim()).filter(Boolean);
  if (parts.length < 2) return null;
  return { first: parts[0], doc: parts[1], task: parts.slice(2).join(' / ') };
}

export function stripDocSuffix(title) {
  return (title || '').replace(DOC_SUFFIX, '').trim();
}

/** Project named by a doc's first segment: a domain, or one of the internal (non-domain) projects. */
export function projectFromSegment(first, settings, idx) {
  const d = normalizeDomain(first);
  if (d) return resolveDomain(d, idx) || d;
  const internal = idx.get(first.trim().toLowerCase());
  if (internal && settings.projects?.[internal]?.internal) return internal;
  return null;
}

export function docIdOf(url) {
  try {
    const u = new URL(url);
    if (u.hostname !== 'docs.google.com') return null;
    const m = u.pathname.match(DOC_URL);
    return m ? m[2] : null;
  } catch {
    return null;
  }
}

/** Tab is never recorded: not http(s), or on the ignore list. */
export function isTrackable(url, settings) {
  let u;
  try { u = new URL(url); } catch { return false; }
  if (u.protocol !== 'http:' && u.protocol !== 'https:') return false;
  const host = u.hostname.toLowerCase().replace(/^www\./, '');
  return !(settings.ignore || []).some((d) => hostIs(host, d));
}

function toolTarget(u, tool) {
  for (const p of tool.params) {
    const d = normalizeDomain(u.searchParams.get(p));
    if (d) return d;
  }
  return '';
}

function ruleFor(url, host, settings) {
  for (const r of settings.rules || []) {
    const m = (r.match || '').trim().toLowerCase();
    if (!m) continue;
    const whole = isDomainLike(m) && !m.includes('/');
    const hit = whole ? hostIs(host, m) : url.toLowerCase().includes(m);
    if (hit) return { ...r, whole };
  }
  return null;
}

/**
 * Classify one tab.
 * @param {{url:string,title:string}} tab
 * @param {object} settings
 * @param {object} docTitles  docId -> best known title (titles arrive late while a doc loads)
 * @returns {{group:'docs'|'tools'|'site'|'linked'|'other', project:string|null, rowKey:string,
 *            label:string, task:string, host:string, domain:string, path:string, tool?:string}}
 */
export function classify(tab, settings, docTitles = {}, idx = projectIndex(settings)) {
  const u = new URL(tab.url);
  const host = u.hostname.toLowerCase().replace(/^www\./, '');
  const domain = registrable(host);
  const base = { host, domain, path: u.pathname, task: '' };

  const docId = docIdOf(tab.url);
  const rule = ruleFor(tab.url, host, settings);
  const ruleProject = rule && idx.get(rule.project.toLowerCase());

  if (docId) {
    const title = docTitles[docId] || tab.title;
    const parsed = parseDocTitle(title);
    const project = ruleProject || (parsed && projectFromSegment(parsed.first, settings, idx))
      || projectInTitle(stripDocSuffix(title), idx)?.project;
    if (project) {
      return { ...base, group: 'docs', project, rowKey: `doc:${docId}`,
        label: parsed ? parsed.doc : stripDocSuffix(title), task: parsed ? parsed.task : '' };
    }
    return { ...base, group: 'other', project: null, rowKey: `doc:${docId}`, label: stripDocSuffix(title) || 'Google Docs' };
  }

  const tool = TOOLS.find((t) => t.test(u));
  if (tool) {
    const target = toolTarget(u, tool);
    const project = ruleProject || (target && resolveDomain(target, idx));
    if (project) return { ...base, group: 'tools', project, rowKey: `tool:${tool.name}`, label: tool.name, tool: tool.name };
    return { ...base, group: 'other', project: null, rowKey: `tool:${tool.name}:${target}`,
      label: target ? `${tool.name} · ${target}` : tool.name, tool: tool.name };
  }

  const own = resolveDomain(host, idx);
  if (own && (!ruleProject || ruleProject === own)) {
    return { ...base, group: 'site', project: own, rowKey: `site:${own}`, label: host };
  }
  const linked = () => ({ ...base, group: 'linked', project: ruleProject, rowKey: `link:${domain}`, label: domain });
  // A URL-part rule is precise: it wins. A whole-domain rule (planfix.com -> Inweb) must not
  // swallow pages that name a client in their title, so the title goes first.
  if (ruleProject && !rule.whole) return linked();
  // A project domain in the page title (task trackers, CRMs, search for the client) = work on it.
  let named = projectInTitle(tab.title, idx);
  // In a task manager every task is named after its client, known or not: "covere2.eu - SEO-комплекс".
  if (!named && isWorkTool(host, settings)) {
    const d = firstDomainIn(tab.title);
    if (d) named = { project: resolveDomain(d, idx) || d, match: d, fresh: !resolveDomain(d, idx) };
  }
  if (named) {
    const label = cleanTitle(tab.title, named.match) || domain;
    return { ...base, group: 'linked', project: named.project, rowKey: `title:${domain}|${label}`, label, task: domain,
      autoProject: !!named.fresh };
  }
  if (ruleProject) return linked();
  return { ...base, group: 'other', project: null, rowKey: `dom:${domain}`, label: domain };
}

const TOKEN_EDGE = '[^\\p{L}\\p{N}.-]';

/** Task managers serving every project (Planfix…): settings.workTools. */
export function isWorkTool(host, settings) {
  return (settings.workTools || []).some((d) => hostIs(host, String(d).toLowerCase()));
}

/** First domain-looking token of a title: "covere2.eu - SEO-комплекс" -> "covere2.eu". */
export function firstDomainIn(title) {
  for (const raw of (title || '').split(/[\s/|,()«»"'·•:]+/u)) {
    const d = normalizeDomain(raw.replace(/[.\-–—]+$/u, ''));
    if (d) return d;
  }
  return '';
}

/** First known project domain (or domain alias) written in a title, as a whole token. */
export function projectInTitle(title, idx) {
  if (!title) return null;
  const low = title.toLowerCase();
  let best = null;
  for (const [key, project] of idx) {
    if (!key.includes('.')) continue; // internal names like "Inweb" are too common in titles
    let from = 0;
    while (from <= low.length) {
      const i = low.indexOf(key, from);
      if (i < 0) break;
      const before = i === 0 ? '' : low[i - 1];
      const after = low[i + key.length] || '';
      const edge = new RegExp(`^${TOKEN_EDGE}$`, 'u');
      const okBefore = !before || edge.test(before);
      const okAfter = !after || edge.test(after) || (after === '.' && !/[\p{L}\p{N}]/u.test(low[i + key.length + 1] || ''));
      if (okBefore && okAfter) {
        if (!best || i < best.index || (i === best.index && key.length > best.match.length)) best = { index: i, match: key, project };
        break;
      }
      from = i + 1;
    }
  }
  return best;
}

/** "paramedic.ua - SEO-комплекс / ТЗ" -> "SEO-комплекс / ТЗ". */
function cleanTitle(title, match) {
  const i = title.toLowerCase().indexOf(match);
  const cut = i < 0 ? title : title.slice(0, i) + title.slice(i + match.length);
  return cut.replace(/^[\s\-–—|/:·•,]+|[\s\-–—|/:·•,]+$/g, '').replace(/\s{2,}/g, ' ').trim();
}
