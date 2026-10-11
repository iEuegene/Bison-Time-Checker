// Tracking core. All state lives in storage (MV3 service workers die at any moment),
// time comes in through `now()`, so the logic runs unchanged in node tests.
import { classify, docIdOf, isTrackable, parseDocTitle, normalizeDomain } from './classify.js';
import { splitByDay, dayKey, shiftDay, MIN } from './time.js';
import { emptyDayState } from './report.js';

export const GAP = 2 * MIN;            // heartbeat older than this = Chrome was closed or the laptop slept
export const MIN_SEGMENT = 5_000;      // tab flicks shorter than 5 s are not recorded
export const MIN_IDLE_PROMPT = MIN;    // idle under a minute is not worth asking about
export const KEEP_SEGMENTS_DAYS = 7;
export const KEEP_DAY_STATE_DAYS = 30;

export const DEFAULT_SETTINGS = {
  idleMin: 3,
  roundStep: 5,
  projects: { Inweb: { internal: true, aliases: [] } },
  rules: [],
  ignore: [],
  declined: [],
};

export function withDefaults(s) {
  const out = { ...DEFAULT_SETTINGS, ...(s || {}) };
  out.projects = { ...(s?.projects || DEFAULT_SETTINGS.projects) };
  return out;
}

function sameTarget(a, b) {
  const da = docIdOf(a.url);
  const db = docIdOf(b.url);
  if (da || db) return da === db;
  return a.url.split('#')[0] === b.url.split('#')[0];
}

export function createTracker({ store, now = () => Date.now() }) {
  let chain = Promise.resolve();
  const serial = (fn) => (chain = chain.then(fn, fn));

  async function settings() {
    const { settings: s } = await store.get(['settings']);
    return withDefaults(s);
  }

  async function rememberDocTitle(tab) {
    const id = docIdOf(tab.url);
    if (!id || !tab.title) return;
    const { docTitles = {} } = await store.get(['docTitles']);
    const good = !!parseDocTitle(tab.title);
    if (good ? docTitles[id] !== tab.title : !docTitles[id]) {
      docTitles[id] = tab.title;
      await store.set({ docTitles });
    }
  }

  /** Write [open.s, end) into per-day segment lists; auto-create projects named by docs. */
  async function close(open, end) {
    const e = Math.max(open.s, Math.min(end, now()));
    if (e - open.s < MIN_SEGMENT) return;
    for (const piece of splitByDay(open.s, e)) {
      const key = `seg:${piece.day}`;
      const got = await store.get([key]);
      const list = got[key] || [];
      list.push({ s: piece.start, e: piece.end, url: open.url, title: open.title });
      await store.set({ [key]: list });
    }
    const s = await settings();
    const { docTitles = {} } = await store.get(['docTitles']);
    const c = classify(open, s, docTitles);
    if (c.group === 'docs' && c.project && !s.projects[c.project] && normalizeDomain(c.project)) {
      s.projects[c.project] = { aliases: [] };
      await store.set({ settings: s });
    }
  }

  /** Drop a segment whose heartbeat went stale: close it where the heartbeat stopped. */
  async function settleStale(open) {
    if (open && now() - open.beat > GAP) {
      await close(open, open.beat);
      await store.set({ open: null });
      return null;
    }
    return open;
  }

  async function projectOf(tab) {
    const s = await settings();
    const { docTitles = {} } = await store.get(['docTitles']);
    try { return classify(tab, s, docTitles).project; } catch { return null; }
  }

  return {
    /**
     * The tab that should be counted right now, or null (Chrome unfocused, no window, etc.).
     * @param {{url:string,title:string,incognito?:boolean}|null} tab
     */
    observe: (tab) => serial(async () => {
      const t = now();
      let { open = null, idleState = 'active' } = await store.get(['open', 'idleState']);
      open = await settleStale(open);
      const s = await settings();
      const want = idleState === 'active' && tab && !tab.incognito && isTrackable(tab.url, s) ? tab : null;
      if (want) await rememberDocTitle(want);

      if (open && want && sameTarget(open, want)) {
        await store.set({ open: { ...open, url: want.url, title: want.title || open.title, beat: t } });
        return;
      }
      if (open) await close(open, t);
      await store.set({ open: want ? { s: t, url: want.url, title: want.title || '', beat: t } : null });
    }),

    heartbeat: () => serial(async () => {
      const t = now();
      let { open = null, idlePending = null } = await store.get(['open', 'idlePending']);
      open = await settleStale(open);
      if (open) await store.set({ open: { ...open, beat: t } });
      if (idlePending) {
        // Asleep while idle: the sleep is not idle time worth offering back.
        if (t - idlePending.beat > GAP) idlePending.frozen = true;
        if (!idlePending.frozen) idlePending.beat = t;
        await store.set({ idlePending });
      }
    }),

    /** chrome.idle state change. `idleSec` = the detection interval in force. */
    idle: (state, idleSec) => serial(async () => {
      const t = now();
      let { open = null, idlePending = null } = await store.get(['open', 'idlePending']);
      open = await settleStale(open);

      if (state === 'idle') {
        if (open) {
          const end = Math.max(open.s, t - idleSec * 1000);
          await close(open, end);
          idlePending = { s: end, beat: t, project: await projectOf(open) };
        }
        await store.set({ open: null, idleState: 'idle', idlePending });
        return;
      }
      if (state === 'locked') {
        if (open) await close(open, t);
        await store.set({ open: null, idleState: 'locked', idlePending: null });
        return;
      }
      // active
      if (idlePending) {
        const e = idlePending.frozen ? idlePending.beat : t;
        if (e - idlePending.s >= MIN_IDLE_PROMPT) {
          const key = `day:${dayKey(idlePending.s)}`;
          const got = await store.get([key]);
          const day = { ...emptyDayState(), ...(got[key] || {}) };
          day.idle = [...day.idle, { id: `i${idlePending.s}`, s: idlePending.s, e, project: idlePending.project }];
          await store.set({ [key]: day });
        }
      }
      await store.set({ idleState: 'active', idlePending: null });
    }),

    /** Delete old raw segments (7 days) and day states (30 days). */
    cleanup: () => serial(async () => {
      const today = dayKey(now());
      const all = await store.get(null);
      const segLimit = shiftDay(today, -(KEEP_SEGMENTS_DAYS - 1));
      const dayLimit = shiftDay(today, -(KEEP_DAY_STATE_DAYS - 1));
      const drop = Object.keys(all).filter((k) =>
        (k.startsWith('seg:') && k.slice(4) < segLimit) || (k.startsWith('day:') && k.slice(4) < dayLimit));
      if (drop.length) await store.remove(drop);
    }),

    /** Segments of a day including the still-open one, clipped to that day. */
    daySegments: async (day) => {
      const t = now();
      const key = `seg:${day}`;
      const got = await store.get([key, 'open']);
      const list = [...(got[key] || [])];
      const open = got.open;
      if (open && t - open.beat <= GAP) {
        for (const p of splitByDay(open.s, t)) {
          if (p.day === day) list.push({ s: p.start, e: p.end, url: open.url, title: open.title, open: true });
        }
      }
      return list;
    },
  };
}
