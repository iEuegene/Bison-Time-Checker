import { test } from 'node:test';
import assert from 'node:assert/strict';
import { classify, parseDocTitle, normalizeDomain, registrable, isTrackable } from '../src/lib/classify.js';
import { DEFAULT_SETTINGS } from '../src/lib/tracker.js';

const settings = {
  ...DEFAULT_SETTINGS,
  projects: {
    Inweb: { internal: true, aliases: [] },
    'shop.com.ua': { aliases: ['old-shop.in.ua'] },
    'example.ua': { aliases: [] },
  },
};
const doc = (id, title) => ({ url: `https://docs.google.com/document/d/${id}/edit`, title });

test('doc title convention: project, doc, task; all Google suffix locales', () => {
  for (const sfx of [' - Google Docs', ' - Google Документи', ' – Google Таблиці', ' - Google Slides']) {
    assert.deepEqual(parseDocTitle(`shop.com.ua / ТЗ №123 / Виправлення помилок${sfx}`),
      { first: 'shop.com.ua', doc: 'ТЗ №123', task: 'Виправлення помилок' });
  }
  assert.equal(parseDocTitle('ТЗ фінал 2 - Google Docs'), null);
  assert.equal(parseDocTitle('Google Docs'), null);
});

test('doc with a domain names the project even before it exists', () => {
  const c = classify(doc('abcdefghijk1', 'newclient.com / Аудит / Перший прохід - Google Docs'), settings);
  assert.equal(c.group, 'docs');
  assert.equal(c.project, 'newclient.com');
  assert.equal(c.label, 'Аудит');
  assert.equal(c.task, 'Перший прохід');
});

test('doc domain is normalized: case, www, protocol', () => {
  for (const first of ['SHOP.com.ua', 'www.shop.com.ua', 'https://shop.com.ua/']) {
    assert.equal(classify(doc('abcdefghijk2', `${first} / ТЗ`), settings).project, 'shop.com.ua');
  }
});

test('doc alias domain maps to its project', () => {
  assert.equal(classify(doc('abcdefghijk3', 'old-shop.in.ua / Редиректи'), settings).project, 'shop.com.ua');
});

test('internal project Inweb, case-insensitive; random first segment goes to other', () => {
  assert.equal(classify(doc('abcdefghijk4', 'inweb / Навчання / GEO'), settings).project, 'Inweb');
  const c = classify(doc('abcdefghijk5', 'Нотатки / мітинг'), settings);
  assert.equal(c.project, null);
  assert.equal(c.group, 'other');
});

test('late doc title: docTitles cache wins over the placeholder tab title', () => {
  const c = classify(doc('abcdefghijk6', 'Google Docs'), settings, { abcdefghijk6: 'example.ua / ТЗ №5' });
  assert.equal(c.project, 'example.ua');
  assert.equal(c.rowKey, 'doc:abcdefghijk6');
});

test('subdomain of a client site resolves through the Public Suffix List', () => {
  assert.equal(registrable('test.aqua-room.com.ua'), 'aqua-room.com.ua');
  const c = classify({ url: 'https://test.shop.com.ua/admin', title: 'Admin' }, settings);
  assert.equal(c.group, 'site');
  assert.equal(c.project, 'shop.com.ua');
  assert.equal(c.path, '/admin');
});

test('unknown site never becomes com.ua', () => {
  const c = classify({ url: 'https://test.aqua-room.com.ua/', title: '' }, settings);
  assert.equal(c.project, null);
  assert.equal(c.domain, 'aqua-room.com.ua');
});

test('Search Console resource_id: domain property and URL-prefix property', () => {
  const a = classify({ url: 'https://search.google.com/search-console/performance/search-analytics?resource_id=sc-domain%3Ashop.com.ua', title: '' }, settings);
  assert.deepEqual([a.group, a.project, a.label], ['tools', 'shop.com.ua', 'Search Console']);
  const b = classify({ url: 'https://search.google.com/search-console?resource_id=https%3A%2F%2Fexample.ua%2F', title: '' }, settings);
  assert.equal(b.project, 'example.ua');
});

test('SEO tool on a competitor stays in other, labelled with tool and target', () => {
  const c = classify({ url: 'https://app.ahrefs.com/site-explorer/overview/v2?target=competitor.ua', title: '' }, settings);
  assert.equal(c.project, null);
  assert.equal(c.label, 'Ahrefs · competitor.ua');
  const own = classify({ url: 'https://app.ahrefs.com/site-explorer/overview/v2?target=shop.com.ua', title: '' }, settings);
  assert.equal(own.project, 'shop.com.ua');
});

test('user rule beats everything; domain rule matches subdomains', () => {
  const s = { ...settings, rules: [{ match: 'forum.example.org', project: 'example.ua' }, { match: 'analytics.google.com/analytics/web/#/p123', project: 'shop.com.ua' }] };
  const a = classify({ url: 'https://forum.example.org/t/1', title: '' }, s);
  assert.deepEqual([a.group, a.project], ['linked', 'example.ua']);
  const b = classify({ url: 'https://analytics.google.com/analytics/web/#/p123/reports', title: '' }, s);
  assert.equal(b.project, 'shop.com.ua');
});

test('archived projects are not matched', () => {
  const s = { ...settings, projects: { ...settings.projects, 'example.ua': { archived: true } } };
  assert.equal(classify({ url: 'https://example.ua/', title: '' }, s).project, null);
});

test('trackable: http(s) only, ignore list incl. subdomains', () => {
  const s = { ...settings, ignore: ['youtube.com'] };
  assert.equal(isTrackable('chrome://newtab/', s), false);
  assert.equal(isTrackable('https://m.youtube.com/watch', s), false);
  assert.equal(isTrackable('https://shop.com.ua/', s), true);
});

test('normalizeDomain rejects non-domains', () => {
  assert.equal(normalizeDomain('Нотатки'), '');
  assert.equal(normalizeDomain('192.168.0.1'), '');
  assert.equal(normalizeDomain('sc-domain:Shop.com.ua'), 'shop.com.ua');
  assert.equal(normalizeDomain('сайт.укр'), 'сайт.укр');
});
