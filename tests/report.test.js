import { test } from 'node:test';
import assert from 'node:assert/strict';
import { buildReport } from '../src/lib/report.js';
import { DEFAULT_SETTINGS } from '../src/lib/tracker.js';
import { MIN } from '../src/lib/time.js';

const settings = {
  ...DEFAULT_SETTINGS,
  projects: { Inweb: { internal: true, aliases: [] }, 'shop.com.ua': { aliases: [] } },
};
const T0 = new Date(2026, 9, 12, 10, 0).getTime();
const at = (min) => T0 + min * MIN;
const seg = (from, to, url, title = '') => ({ s: at(from), e: at(to), url, title });
const DOC = 'https://docs.google.com/document/d/abcdefghijk1/edit';
const DOC_T = 'shop.com.ua / ТЗ №123 / Виправлення помилок - Google Docs';

test('acceptance: 30 min in a convention doc -> 0 год 30 хв + comment phrase', () => {
  const r = buildReport([seg(0, 30, DOC, DOC_T)], {}, settings);
  assert.equal(r.projects.length, 1);
  const p = r.projects[0];
  assert.equal(p.name, 'shop.com.ua');
  assert.equal(p.rounded, 30);
  assert.equal(p.groups[0].id, 'docs');
  assert.equal(p.groups[0].rows[0].label, 'ТЗ №123');
  assert.equal(p.comment.text, 'ТЗ №123 — виправлення помилок');
});

test('rounding happens on the project total, not on rows', () => {
  // twenty 2-minute visits to the client site: 40 min exact
  const segs = [];
  for (let i = 0; i < 20; i++) segs.push(seg(i * 5, i * 5 + 2, `https://shop.com.ua/p${i}`));
  const p = buildReport(segs, {}, settings).projects[0];
  assert.equal(p.rounded, 40);
  assert.equal(p.groups[0].rows[0].pages.length, 5);
});

test('disabling a row recomputes the rounded total; re-enabling restores it', () => {
  const segs = [seg(0, 33, DOC, DOC_T), seg(33, 45, 'https://shop.com.ua/')];
  const on = buildReport(segs, {}, settings).projects[0];
  assert.equal(on.rounded, 45);
  const off = buildReport(segs, { disabled: { 'shop.com.ua|site:shop.com.ua': true } }, settings).projects[0];
  assert.equal(off.rounded, 35); // 33 -> 35
});

test('other is chronological, merges a domain across short gaps, shows project sessions between', () => {
  const segs = [
    seg(0, 20, DOC, DOC_T),
    seg(20, 25, 'https://forum.example.org/a'),
    seg(26, 30, 'https://forum.example.org/b'),
    seg(30, 50, DOC, DOC_T),
    seg(50, 55, 'https://competitor.ua/'),
  ];
  const r = buildReport(segs, {}, settings);
  assert.deepEqual(r.timeline.map((t) => t.kind === 'session' ? `S:${t.project}` : `O:${t.label}`),
    ['S:shop.com.ua', 'O:example.org', 'S:shop.com.ua', 'O:competitor.ua']);
  assert.equal(r.timeline[1].ms, 9 * MIN);
  assert.equal(r.otherMs, 14 * MIN);
});

test('moving an other entry adds its time to the project and drops it from other', () => {
  const segs = [seg(0, 20, DOC, DOC_T), seg(20, 32, 'https://competitor.ua/')];
  const key = `o:${at(20)}`;
  const r = buildReport(segs, { moved: { [key]: 'shop.com.ua' } }, settings);
  assert.equal(r.projects[0].rounded, 30);
  assert.equal(r.otherMs, 0);
  assert.equal(r.projects[0].groups.find((g) => g.id === 'manual').rows[0].label, 'competitor.ua (перенесено)');
});

test('manual minutes count; comment ignores sources under 5 min and manual rows', () => {
  const segs = [seg(0, 40, DOC, DOC_T), seg(40, 43, 'https://shop.com.ua/')];
  const r = buildReport(segs, { manual: [{ id: 'm1', project: 'shop.com.ua', min: 15, label: 'Дзвінок' }] }, settings);
  const p = r.projects[0];
  assert.equal(p.rounded, 60); // 40 + 3 + 15 = 58 -> 60
  assert.equal(p.comment.text, 'ТЗ №123 — виправлення помилок');
});

test('edited comment is kept; new sources flag it stale', () => {
  const segs = [seg(0, 30, DOC, DOC_T)];
  const base = buildReport(segs, {}, settings).projects[0].comment.auto;
  const st = { comments: { 'shop.com.ua': 'Свій текст' }, commentBase: { 'shop.com.ua': base } };
  const same = buildReport(segs, st, settings).projects[0].comment;
  assert.deepEqual([same.text, same.stale], ['Свій текст', false]);
  const more = buildReport([...segs, seg(30, 40, 'https://shop.com.ua/')], st, settings).projects[0].comment;
  assert.deepEqual([more.text, more.stale], ['Свій текст', true]);
});

test('done mark clears when the total changes', () => {
  const segs = [seg(0, 30, DOC, DOC_T)];
  assert.equal(buildReport(segs, { done: { 'shop.com.ua': 30 } }, settings).projects[0].done.marked, true);
  const d = buildReport([...segs, seg(30, 40, 'https://shop.com.ua/')], { done: { 'shop.com.ua': 30 } }, settings).projects[0].done;
  assert.deepEqual([d.marked, d.stale, d.diff], [false, true, 10]);
});

test('unknown domain from 5 min is suggested, declined never', () => {
  const segs = [seg(0, 7, 'https://newclient.ua/'), seg(7, 11, 'https://youtube.com/')];
  assert.deepEqual(buildReport(segs, {}, settings).suggestions.map((s) => s.domain), ['newclient.ua']);
  assert.deepEqual(buildReport(segs, {}, { ...settings, declined: ['newclient.ua'] }).suggestions, []);
});

test('task manager page without a client goes to the project worked on around it', () => {
  const s = { ...settings, workTools: ['planfix.com'] };
  const PF = 'https://inweb.planfix.com/planner';
  const r = buildReport([
    seg(0, 20, DOC, DOC_T),
    seg(20, 25, PF, 'Bison / Планувальник / inweb'),
    seg(25, 30, DOC, DOC_T),
  ], {}, s);
  const p = r.projects[0];
  assert.equal(p.rounded, 30);
  assert.equal(p.groups.find((g) => g.id === 'linked').rows[0].label, 'planfix.com');
  assert.equal(r.otherMs, 0);
  // first thing in the morning: takes the project that follows
  const r2 = buildReport([seg(0, 5, PF, 'Планувальник'), seg(6, 20, DOC, DOC_T)], {}, s);
  assert.equal(r2.projects[0].rounded, 20);
  // nothing within 15 min around: stays in Інше
  const r3 = buildReport([seg(0, 5, PF, 'Планувальник'), seg(40, 50, DOC, DOC_T)], {}, s);
  assert.equal(r3.otherMs, 5 * MIN);
  // a generic site is never inferred
  const r4 = buildReport([seg(0, 10, DOC, DOC_T), seg(10, 15, 'https://youtube.com/', 'x')], {}, s);
  assert.equal(r4.otherMs, 5 * MIN);
});
