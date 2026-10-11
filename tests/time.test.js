import { test } from 'node:test';
import assert from 'node:assert/strict';
import { roundMinutes, splitByDay, formatReport, formatExact, MIN, shiftDay } from '../src/lib/time.js';

test('rounding to nearest 5, half up', () => {
  assert.equal(roundMinutes(152 * MIN), 150);
  assert.equal(roundMinutes(153 * MIN), 155);
  assert.equal(roundMinutes(152.5 * MIN), 155);
  assert.equal(roundMinutes(2 * MIN), 0);
  assert.equal(roundMinutes(0), 0);
});

test('report format always shows hours and minutes', () => {
  assert.equal(formatReport(155), '2 год 35 хв');
  assert.equal(formatReport(30), '0 год 30 хв');
  assert.equal(formatExact(30_000), '<1 хв');
  assert.equal(formatExact(65 * MIN), '1 год 05 хв');
});

test('midnight split: 23:50-00:20 gives 10 + 20 min', () => {
  const s = new Date(2026, 9, 11, 23, 50).getTime();
  const e = new Date(2026, 9, 12, 0, 20).getTime();
  const parts = splitByDay(s, e);
  assert.deepEqual(parts.map((p) => [p.day, (p.end - p.start) / MIN]), [['2026-10-11', 10], ['2026-10-12', 20]]);
});

test('shiftDay crosses month boundaries', () => {
  assert.equal(shiftDay('2026-11-01', -1), '2026-10-31');
  assert.equal(shiftDay('2026-12-31', 1), '2027-01-01');
});
