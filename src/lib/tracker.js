// Tracking core. All state lives in storage (MV3 service workers die at any moment),
// time comes in through `now()`, so the logic runs unchanged in node tests.
import { classify, docIdOf, isTrackable, parseDocTitle, normalizeDomain } from './classify.js';
import { splitByDay, dayKey, shiftDay, MIN } from './time.js';
import { emptyDayState } from './report.js';

export const GAP = 2 * MIN;            // heartbeat older than this = Chrome was closed or the laptop slept
export const MIN_SEGMENT = 5_000;      // tab flicks shorter than 5 s are not recorded
export const MIN_IDLE_PROMPT = MIN;    // idle under a minute is not worth asking about
export const MIN_OFF = MIN;            // untracked stretches under a minute are not listed
export const KEEP_SEGMENTS_DAYS = 7;
export const KEEP_DAY_STATE_DAYS = 30;
export const LOG_SIZE = 400;

/** Why nothing is being counted. Shown in the popup as "поза обліком". */
export const OFF_REASONS = {
  focus: 'інша програма',
  idle: 'простій',
  lock: 'екран заблоковано',
  pause: 'пауза',
  untracked: 'сторінки без обліку',
};

export const DEFAULT_SETTINGS = {
  idleMin: 3,
  roundStep: 5,
  projects: { Inweb: { internal: true, aliases: [] } },
  rules: [],
  ignore: [],
  declined: [],
  workTools: ['planfix.com'],
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

  async function appendDay(prefix, s, e, item) {
    for (const piece of splitByDay(s, e)) {
      const key = `${prefix}:${piece.day}`;
      const got = await store.get([key]);
      const list = got[key] || [];
      list.push({ ...item, s: piece.start, e: piece.end });
      await store.set({ [key]: list });
    }
  }

  /** Diagnostics ring buffer: domain / doc id only, never full URLs or titles. */
  async function log(ev, open, extra = {}) {
    const { log: list = [] } = await store.get(['log']);
    let where = '';
    if (open?.url) {
      try { const u = new URL(open.url); where = docIdOf(open.url) ? `doc:${docIdOf(open.url).slice(0, 6)}` : u.hostname; } catch { where = '?'; }
    }
    list.push({ t: now(), ev, where, ...extra });
    await store.set({ log: list.slice(-LOG_SIZE) });
  }

  /** Write [open.s, end) into per-day segment lists; auto-create projects named by docs. */
  async function close(open, end, why = '') {
    const e = Math.max(open.s, Math.min(end, now()));
    await log('close', open, { why, sec: Math.round((e - open.s) / 1000) });
    if (e - open.s < MIN_SEGMENT) return;
    await appendDay('seg', open.s, e, { url: open.url, title: open.title });
    const s = await settings();
    const { docTitles = {} } = await store.get(['docTitles']);
    const c = classify(open, s, docTitles);
    if (c.group === 'docs' && c.project && !s.projects[c.project] && normalizeDomain(c.project)) {
      s.projects[c.project] = { aliases: [] };
      await store.set({ settings: s });
    }
  }

  // ---- "off" stretches: why time between segments was not counted ----
  async function startOff(reason, t) {
    const { off } = await store.get(['off']);
    if (off && off.reason === reason) return;
    if (off) await endOff(t);
    await store.set({ off: { s: t, beat: now(), reason } });
  }
  async function endOff(t) {
    const { off } = await store.get(['off']);
    if (!off) return;
    await store.set({ off: null });
    // Overnight and anything under a minute is noise, not information.
    if (t - off.s >= MIN_OFF && dayKey(off.s) === dayKey(t)) {
      await appendDay('off', off.s, t, { reason: off.reason });
    }
  }

  /** Drop a segment whose heartbeat went stale: close it where the heartbeat stopped. */
  async function settleStale(open) {
    if (open && now() - open.beat > GAP) {
      await close(open, open.beat, 'stale');
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
      let { open = null, idleState = 'active', paused = null } = await store.get(['open', 'idleState', 'paused']);
      open = await settleStale(open);
      const s = await settings();
      const trackable = !!tab && !tab.incognito && isTrackable(tab.url, s);
      const want = !paused && idleState === 'active' && trackable ? tab : null;
      if (tab && trackable) await rememberDocTitle(tab);

      if (open && want && sameTarget(open, want)) {
        await store.set({ open: { ...open, url: want.url, title: want.title || open.title, beat: t } });
        return;
      }
      if (open) await close(open, t, want ? 'switch' : 'stop');
      if (want) {
        await endOff(t);
        await log('open', want);
        await store.set({ open: { s: t, url: want.url, title: want.title || '', beat: t } });
      } else {
        await store.set({ open: null });
        const reason = paused ? 'pause' : idleState === 'idle' ? 'idle' : idleState === 'locked' ? 'lock' : !tab ? 'focus' : 'untracked';
        if (open) await log('off', null, { why: reason });
        await startOff(reason, t);
      }
    }),

    heartbeat: () => serial(async () => {
      const t = now();
      let { open = null, idlePending = null, off = null } = await store.get(['open', 'idlePending', 'off']);
      open = await settleStale(open);
      if (open) await store.set({ open: { ...open, beat: t } });
      if (idlePending) {
        // Asleep while idle: the sleep is not idle time worth offering back.
        if (t - idlePending.beat > GAP) idlePending.frozen = true;
        if (!idlePending.frozen) idlePending.beat = t;
        await store.set({ idlePending });
      }
      if (off) {
        if (t - off.beat > GAP) {
          // Slept or Chrome was closed: keep what was seen before, restart after.
          await store.set({ off: { ...off, beat: t } });
          const ended = { ...off };
          if (off.beat - off.s >= MIN_OFF && dayKey(off.s) === dayKey(off.beat)) {
            await appendDay('off', off.s, off.beat, { reason: off.reason });
          }
          await store.set({ off: { s: t, beat: t, reason: ended.reason } });
        } else {
          await store.set({ off: { ...off, beat: t } });
        }
      }
    }),

    /** chrome.idle state change. `idleSec` = the detection interval in force. */
    idle: (state, idleSec) => serial(async () => {
      const t = now();
      await log('idle-state', null, { why: state, sec: idleSec });
      let { open = null, idlePending = null } = await store.get(['open', 'idlePending']);
      open = await settleStale(open);

      if (state === 'idle') {
        if (open) {
          // The minutes before the idle event stay counted: reading a sheet without touching
          // the mouse is work, and Chrome's idle interval is not reliable enough to cut back by.
          await close(open, t, 'idle');
          idlePending = { s: t, beat: t, project: await projectOf(open) };
          await startOff('idle', t);
        }
        await store.set({ open: null, idleState: 'idle', idlePending });
        return;
      }
      if (state === 'locked') {
        if (open) await close(open, t, 'lock');
        await startOff('lock', t);
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

    /** Manual pause: stops counting until resume(), whatever the tabs do. */
    pause: () => serial(async () => {
      const t = now();
      let { open = null, paused = null } = await store.get(['open', 'paused']);
      if (paused) return;
      open = await settleStale(open);
      if (open) await close(open, t, 'pause');
      await store.set({ open: null, paused: { s: t } });
      await startOff('pause', t);
    }),

    resume: () => serial(async () => {
      await store.set({ paused: null });
    }),

    /** Delete old raw segments (7 days) and day states (30 days). */
    cleanup: () => serial(async () => {
      const today = dayKey(now());
      const all = await store.get(null);
      const segLimit = shiftDay(today, -(KEEP_SEGMENTS_DAYS - 1));
      const dayLimit = shiftDay(today, -(KEEP_DAY_STATE_DAYS - 1));
      if (Array.isArray(all.log) && all.log.length > LOG_SIZE) await store.set({ log: all.log.slice(-LOG_SIZE) });
      const drop = Object.keys(all).filter((k) =>
        ((k.startsWith('seg:') || k.startsWith('off:')) && k.slice(4) < segLimit) || (k.startsWith('day:') && k.slice(4) < dayLimit));
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

    /** Untracked stretches of a day by reason, the current one included: {reason: ms}. */
    dayOff: async (day) => {
      const t = now();
      const key = `off:${day}`;
      const got = await store.get([key, 'off']);
      const out = {};
      const add = (r, ms) => { out[r] = (out[r] || 0) + ms; };
      for (const x of got[key] || []) add(x.reason, x.e - x.s);
      const cur = got.off;
      if (cur && dayKey(cur.s) === day && t - cur.beat <= GAP && t - cur.s >= MIN_OFF) add(cur.reason, t - cur.s);
      return out;
    },
  };
}
