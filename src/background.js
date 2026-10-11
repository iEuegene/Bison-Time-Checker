// Service worker: feeds browser events into the tracker. No network, no content scripts.
import { createTracker, withDefaults } from './lib/tracker.js';
import { dayKey } from './lib/time.js';
import { classify } from './lib/classify.js';

const store = {
  get: (keys) => chrome.storage.local.get(keys),
  set: (obj) => chrome.storage.local.set(obj),
  remove: (keys) => chrome.storage.local.remove(keys),
};
const tracker = createTracker({ store });
const ORANGE = '#EF583D';

async function settings() {
  const { settings: s } = await store.get(['settings']);
  return withDefaults(s);
}

/** Active tab of the focused Chrome window, or null when Chrome is not in focus. */
async function focusedTab() {
  try {
    const win = await chrome.windows.getLastFocused({ populate: false });
    if (!win || !win.focused || !['normal', 'app', 'popup'].includes(win.type)) return null;
    const [tab] = await chrome.tabs.query({ active: true, windowId: win.id });
    if (!tab || !tab.url) return null;
    return { url: tab.url, title: tab.title || '', incognito: tab.incognito };
  } catch {
    return null;
  }
}

async function sync() {
  await tracker.observe(await focusedTab());
  await refreshBadge();
}

async function refreshBadge() {
  const day = `day:${dayKey(Date.now())}`;
  const got = await store.get([day, 'open', 'docTitles']);
  const pending = (got[day]?.idle || []).length;
  await chrome.action.setBadgeBackgroundColor({ color: ORANGE });
  await chrome.action.setBadgeText({ text: pending ? '•' : '' });
  let title = 'Bison Time Checker';
  if (got.open) {
    try {
      const c = classify(got.open, await settings(), got.docTitles || {});
      title += c.project ? ` · ${c.project}` : ' · Інше';
    } catch { /* unparsable url: keep plain title */ }
  }
  if (pending) title += ' · є нерозібраний простій';
  await chrome.action.setTitle({ title });
}

async function applyIdleInterval() {
  const s = await settings();
  const sec = Math.max(60, Math.round(s.idleMin * 60));
  chrome.idle.setDetectionInterval(sec);
  return sec;
}

async function init() {
  const { settings: s } = await store.get(['settings']);
  if (!s) await store.set({ settings: withDefaults(null) });
  await chrome.alarms.create('beat', { periodInMinutes: 1 });
  await chrome.alarms.create('cleanup', { periodInMinutes: 360, delayInMinutes: 1 });
  const sec = await applyIdleInterval();
  const state = await chrome.idle.queryState(sec);
  const { idleState = 'active' } = await store.get(['idleState']);
  if (state !== idleState) await tracker.idle(state, sec);
  await sync();
}

chrome.runtime.onInstalled.addListener(init);
chrome.runtime.onStartup.addListener(init);

chrome.tabs.onActivated.addListener(sync);
chrome.tabs.onUpdated.addListener((_id, info, tab) => {
  if (tab.active && (info.url || info.title || info.status === 'complete')) sync();
});
chrome.tabs.onRemoved.addListener(sync);
chrome.windows.onFocusChanged.addListener(sync);
chrome.windows.onRemoved.addListener(sync);

chrome.idle.onStateChanged.addListener(async (state) => {
  const sec = await applyIdleInterval();
  await tracker.idle(state, sec);
  await sync();
});

chrome.alarms.onAlarm.addListener(async (a) => {
  if (a.name === 'beat') {
    await tracker.heartbeat();
    await sync();
  } else if (a.name === 'cleanup') {
    await tracker.cleanup();
  }
});

chrome.storage.onChanged.addListener((changes, area) => {
  if (area !== 'local') return;
  if (changes.settings) applyIdleInterval();
  if (Object.keys(changes).some((k) => k.startsWith('day:'))) refreshBadge();
});

chrome.runtime.onMessage.addListener((msg, _sender, reply) => {
  if (msg?.type === 'sync') {
    sync().then(() => reply({ ok: true }), (e) => reply({ ok: false, error: String(e) }));
    return true;
  }
  return false;
});
