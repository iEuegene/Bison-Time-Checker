// Time helpers: local day keys, midnight splitting, rounding, formatting.

export const MIN = 60_000;

/** Local calendar day of a timestamp as YYYY-MM-DD. */
export function dayKey(ts) {
  const d = new Date(ts);
  const m = String(d.getMonth() + 1).padStart(2, '0');
  const day = String(d.getDate()).padStart(2, '0');
  return `${d.getFullYear()}-${m}-${day}`;
}

/** Timestamp of the local midnight that starts the day after `ts`. */
export function nextMidnight(ts) {
  const d = new Date(ts);
  d.setHours(24, 0, 0, 0);
  return d.getTime();
}

/** Shift a YYYY-MM-DD key by `delta` days (local calendar). */
export function shiftDay(key, delta) {
  const [y, m, d] = key.split('-').map(Number);
  return dayKey(new Date(y, m - 1, d + delta, 12).getTime());
}

/**
 * Split [start, end) at local midnights.
 * Returns [{day, start, end}] — one piece per calendar day touched.
 */
export function splitByDay(start, end) {
  const out = [];
  let s = start;
  while (s < end) {
    const cut = Math.min(nextMidnight(s), end);
    out.push({ day: dayKey(s), start: s, end: cut });
    s = cut;
  }
  return out;
}

/**
 * Round a duration to the nearest `step` minutes, exact half rounds up.
 * 152 min -> 150, 153 min -> 155, 152.5 min -> 155.
 */
export function roundMinutes(ms, step = 5) {
  if (step <= 1) return Math.round(ms / MIN);
  return Math.floor(ms / (step * MIN) + 0.5) * step;
}

/** Split whole minutes into {h, m}. */
export function hm(totalMin) {
  const t = Math.max(0, Math.round(totalMin));
  return { h: Math.floor(t / 60), m: t % 60 };
}

/** "2 год 35 хв" — always both parts, mirrors the two fields of the report form. */
export function formatReport(totalMin) {
  const { h, m } = hm(totalMin);
  return `${h} год ${m} хв`;
}

/** Exact row duration: "<1 хв", "12 хв", "1 год 05 хв". */
export function formatExact(ms) {
  const min = Math.floor(ms / MIN);
  if (min < 1) return '<1 хв';
  const { h, m } = hm(min);
  return h ? `${h} год ${String(m).padStart(2, '0')} хв` : `${m} хв`;
}

/** "14:05" in local time. */
export function clock(ts) {
  const d = new Date(ts);
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
}
