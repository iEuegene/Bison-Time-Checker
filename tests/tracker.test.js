import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createTracker } from '../src/lib/tracker.js';
import { MIN, dayKey } from '../src/lib/time.js';

function memStore(init = {}) {
  const data = structuredClone(init);
  return {
    data,
    async get(keys) {
      if (keys == null) return structuredClone(data);
      const out = {};
      for (const k of keys) if (k in data) out[k] = structuredClone(data[k]);
      return out;
    },
    async set(obj) { Object.assign(data, structuredClone(obj)); },
    async remove(keys) { for (const k of keys) delete data[k]; },
  };
}

function setup(start = new Date(2026, 9, 12, 10, 0).getTime()) {
  const clock = { t: start };
  const store = memStore();
  const tr = createTracker({ store, now: () => clock.t });
  const tick = async (min) => {
    // advance minute by minute, heartbeat like chrome.alarms would
    for (let i = 0; i < min; i++) { clock.t += MIN; await tr.heartbeat(); }
  };
  return { clock, store, tr, tick, day: dayKey(start) };
}
const DOC = { url: 'https://docs.google.com/document/d/abcdefghijk1/edit', title: 'shop.com.ua / ТЗ №5 - Google Docs' };
const SITE = { url: 'https://shop.com.ua/', title: 'Shop' };
const total = (list) => (list || []).reduce((a, x) => a + x.e - x.s, 0) / MIN;

test('switching tabs closes and opens segments; doc names a new project', async () => {
  const { store, tr, tick, day } = setup();
  await tr.observe(DOC);
  await tick(30);
  await tr.observe(SITE);
  await tick(10);
  await tr.observe(null); // Chrome lost focus
  const segs = store.data[`seg:${day}`];
  assert.deepEqual(segs.map((s) => (s.e - s.s) / MIN), [30, 10]);
  assert.ok(store.data.settings.projects['shop.com.ua']);
  assert.equal(store.data.open, null);
});

test('time while Chrome is unfocused is not counted', async () => {
  const { store, tr, tick, day } = setup();
  await tr.observe(SITE);
  await tick(5);
  await tr.observe(null);
  await tick(10);
  await tr.observe(SITE);
  await tick(5);
  await tr.observe(null);
  assert.equal(total(store.data[`seg:${day}`]), 10);
});

test('idle: segment closes at last activity, prompt appears with the idle minutes', async () => {
  const { store, tr, tick, clock, day } = setup();
  await store.set({ settings: { projects: { 'shop.com.ua': { aliases: [] } } } });
  await tr.observe(SITE);
  await tick(13);               // 10 min work + 3 min until idle fires
  await tr.idle('idle', 180);
  await tick(7);
  await tr.idle('active', 180);
  assert.equal(total(store.data[`seg:${day}`]), 10);
  const idle = store.data[`day:${day}`].idle;
  assert.equal(idle.length, 1);
  assert.equal((idle[0].e - idle[0].s) / MIN, 10);
  assert.equal(idle[0].project, 'shop.com.ua');
  assert.equal(idle[0].e, clock.t);
});

test('locked screen: no prompt', async () => {
  const { store, tr, tick, day } = setup();
  await tr.observe(SITE);
  await tick(5);
  await tr.idle('locked', 180);
  await tick(30);
  await tr.idle('active', 180);
  assert.equal(total(store.data[`seg:${day}`]), 5);
  assert.equal(store.data[`day:${day}`], undefined);
});

test('Chrome closed / laptop asleep: segment ends at the last heartbeat', async () => {
  const { store, tr, tick, clock, day } = setup();
  await tr.observe(SITE);
  await tick(20);
  clock.t += 120 * MIN; // no heartbeats for 2 hours
  await tr.observe(SITE);
  await tick(5);
  await tr.observe(null);
  assert.deepEqual(store.data[`seg:${day}`].map((s) => (s.e - s.s) / MIN), [20, 5]);
});

test('sleep while idle is not offered back as idle time', async () => {
  const { store, tr, tick, clock, day } = setup();
  await tr.observe(SITE);
  await tick(13);
  await tr.idle('idle', 180);
  await tick(2);
  clock.t += 8 * 60 * MIN;
  await tr.heartbeat();
  await tr.idle('active', 180);
  const idle = store.data[`day:${day}`].idle;
  assert.equal((idle[0].e - idle[0].s) / MIN, 5);
});

test('flicks under 5 s are dropped; incognito and ignored domains never recorded', async () => {
  const { store, tr, tick, clock, day } = setup();
  await store.set({ settings: { ignore: ['shop.com.ua'] } });
  await tr.observe({ url: 'https://news.example.org/', title: '' });
  clock.t += 3000;
  await tr.observe({ ...DOC, incognito: true });
  await tick(5);
  await tr.observe(SITE);
  await tick(5);
  await tr.observe(null);
  assert.equal(store.data[`seg:${day}`], undefined);
});

test('doc title arriving late is stored and keeps one segment', async () => {
  const { store, tr, tick, day } = setup();
  await tr.observe({ url: DOC.url, title: 'Google Docs' });
  await tick(1);
  await tr.observe(DOC);
  await tick(9);
  await tr.observe(null);
  assert.equal(store.data[`seg:${day}`].length, 1);
  assert.equal(store.data.docTitles.abcdefghijk1, DOC.title);
});

test('segment across midnight is split into two days', async () => {
  const { store, tr, tick } = setup(new Date(2026, 9, 12, 23, 50).getTime());
  await tr.observe(SITE);
  await tick(30);
  await tr.observe(null);
  assert.equal(total(store.data['seg:2026-10-12']), 10);
  assert.equal(total(store.data['seg:2026-10-13']), 20);
});

test('cleanup keeps 7 days of segments and 30 days of day state', async () => {
  const { store, tr } = setup();
  await store.set({ 'seg:2026-10-06': [], 'seg:2026-10-05': [], 'day:2026-09-13': {}, 'day:2026-09-12': {} });
  await tr.cleanup();
  assert.deepEqual(Object.keys(store.data).sort(), ['day:2026-09-13', 'seg:2026-10-06']);
});
