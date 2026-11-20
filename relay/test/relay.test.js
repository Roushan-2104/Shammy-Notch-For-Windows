import { test, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { DatabaseSync } from 'node:sqlite';
import relay, { compareVersions } from '../src/index.js';
import { scrub, parseReport, publicBody } from '../src/report.js';

const schema = readFileSync(new URL('../schema.sql', import.meta.url), 'utf8');

/** Just enough of Cloudflare's D1 API, over a real SQLite database. */
class FakeD1 {
  constructor() { this.db = new DatabaseSync(':memory:'); this.db.exec(schema); }
  prepare(sql) { return new Statement(this.db, sql, []); }
  async batch(statements) { const out = []; for (const s of statements) out.push(await s.run()); return out; }
}
class Statement {
  constructor(db, sql, args) { Object.assign(this, { db, sql, args }); }
  bind(...args) { return new Statement(this.db, this.sql, args); }
  async first() { return this.db.prepare(this.sql).get(...this.args) ?? null; }
  async all() { return { results: this.db.prepare(this.sql).all(...this.args) }; }
  async run() { const r = this.db.prepare(this.sql).run(...this.args); return { meta: { changes: Number(r.changes) } }; }
}

let env, calls, issueState, closedAs, failWhen;
beforeEach(() => {
  env = { DB: new FakeD1(), PUBLIC_REPO: 'me/public', PRIVATE_REPO: 'me/private', GITHUB_TOKEN: 'test' };
  calls = [];
  issueState = 'open';
  closedAs = 'completed';
  failWhen = () => false;
  let next = 1;
  globalThis.fetch = async (url, init) => {
    // The newest release, asked once a day: not part of what a test counts.
    if (url.endsWith('/releases/latest')) return { ok: true, status: 200, json: async () => ({ tag_name: 'v1.5.5' }) };
    const call = { url, method: init.method, body: init.body ? JSON.parse(init.body) : undefined };
    if (failWhen(call)) return { ok: false, status: 502, json: async () => ({}) };
    calls.push(call);
    return { ok: true, status: 200, json: async () => ({ number: next++, state: issueState, state_reason: issueState === 'closed' ? closedAs : null }) };
  };
});

const report = (extra = {}) => ({
  kind: 'crash', signature: 'crash/voice/3f9ac2', version: '1.5.3', error: 'AccessViolationException',
  area: 'Voice › Microphone', where: 'WaveInEvent.DoRecording', windows: 'Windows 11 (26300)',
  when: '2026-11-19T15:02:00Z', note: 'I held Ctrl + Win', log: ['20:31:52 INFO  Talk key held'], ...extra,
});

function post(body, ip = '1.2.3.4') {
  return relay.fetch(new Request('https://relay.test/report', {
    method: 'POST', body: JSON.stringify(body), headers: { 'cf-connecting-ip': ip, 'content-type': 'application/json' },
  }), env);
}

async function runDigest() {
  let work;
  await relay.scheduled({}, env, { waitUntil: p => { work = p; } });
  await work;
}

test('a new problem opens a public summary and a full private issue', async () => {
  const res = await post(report());
  assert.equal(res.status, 200);
  assert.deepEqual(await res.json(), { ok: true, new: true });
  assert.equal(calls.length, 2);
  assert.match(calls[0].url, /repos\/me\/public\/issues$/);
  assert.doesNotMatch(calls[0].body.body, /Ctrl \+ Win|Talk key/, 'no note or log lines in public');
  assert.match(calls[1].url, /repos\/me\/private\/issues$/);
  assert.match(calls[1].body.body, /Ctrl \+ Win/);
  assert.match(calls[1].body.body, /Talk key held/);
  assert.match(calls[1].body.body, /me\/public#1/);
  const row = await env.DB.prepare('SELECT * FROM problems').first();
  assert.equal(row.public_issue, 1);
  assert.equal(row.private_issue, 2);
});

test('a repeat is stored quietly, then the digest comments and updates the counter', async () => {
  await post(report());
  calls.length = 0;
  const res = await post(report({ version: '1.5.4', note: 'Happens when I tap quickly' }), '5.6.7.8');
  assert.deepEqual(await res.json(), { ok: true, new: false });
  // A newer version only looks at whether the issue was closed as fixed.
  assert.deepEqual(calls.map(c => c.method), ['GET'], 'no email for a repeat');

  await runDigest();
  const comment = calls.find(c => c.method === 'POST' && c.url.endsWith('/issues/2/comments'));
  assert.ok(comment, 'digest comment on the private issue');
  assert.match(comment.body.body, /\+1 report/);
  assert.match(comment.body.body, /total 2/);
  assert.match(comment.body.body, /tap quickly/);
  const counter = calls.find(c => c.method === 'PATCH' && c.url.endsWith('/me/public/issues/1'));
  assert.match(counter.body.body, /\*\*2\*\*/);
  assert.match(counter.body.body, /1\.5\.3, 1\.5\.4/);

  calls.length = 0;
  await runDigest();
  assert.equal(calls.length, 0, 'nothing new, nothing sent');
});

test('a fixed problem seen in a newer version reopens both issues', async () => {
  await post(report());
  calls.length = 0;
  issueState = 'closed';
  await post(report({ version: '1.5.5' }));
  const reopened = calls.filter(c => c.method === 'PATCH' && c.body.state === 'open').map(c => c.url);
  assert.equal(reopened.length, 2);
  assert.ok(calls.some(c => c.method === 'POST' && c.body.body === 'Seen again in 1.5.5.'));
});

test('an old version of a fixed problem only counts', async () => {
  await post(report({ version: '1.5.4' }));
  calls.length = 0;
  issueState = 'closed';
  await post(report({ version: '1.5.3' }));
  assert.equal(calls.length, 0);
});

test('one connection can send 20 reports a day', async () => {
  for (let i = 0; i < 20; i++) assert.equal((await post(report())).status, 200);
  assert.equal((await post(report())).status, 429);
  assert.equal((await post(report(), '9.9.9.9')).status, 200);
});

test('at most 15 new problems a day', async () => {
  for (let i = 0; i < 15; i++) assert.equal((await post(report({ signature: `crash/x/${i}aa` }), `10.0.0.${i}`)).status, 200);
  assert.equal((await post(report({ signature: 'crash/x/new' }), '10.0.1.1')).status, 429);
});

test('anything that is not a report is turned away', async () => {
  assert.equal((await post({ hello: 1 })).status, 400);
  assert.equal((await post(report({ signature: '../../etc' }))).status, 400);
  assert.equal((await post(report({ version: 'latest' }))).status, 400);
  assert.equal((await post(report({ kind: 'spam' }))).status, 400);
  const big = await relay.fetch(new Request('https://relay.test/report', { method: 'POST', body: 'x'.repeat(40000) }), env);
  assert.equal(big.status, 413);
  assert.equal(calls.length, 0);
});

test('the relay cleans what an older copy might still send', () => {
  assert.equal(scrub('mail me at a.b@c.com'), 'mail me at ‹email›');
  assert.equal(scrub('see https://x.com/a?b=1 now'), 'see ‹link› now');
  assert.equal(scrub('C:\\Users\\Roushan\\AppData\\x.log failed'), '‹path› failed');
  assert.equal(scrub('call 98765 43210 or OTP 482913'), 'call ‹number› or OTP ‹number›');
  assert.equal(scrub('20:31:52 typed 6 words, 41 KB, v1.5.3 on 2026-11-20'), '20:31:52 typed 6 words, 41 KB, v1.5.3 on 2026-11-20');
  assert.equal(scrub('2026-11-20 20:31:52.120 ok'), '2026-11-20 20:31:52.120 ok');
});

test('a mention cannot ping anyone', () => {
  assert.doesNotMatch(parseReport(report({ note: 'hey @someone' })).note, /@/);
});

test('versions compare as numbers', () => {
  assert.ok(compareVersions('1.5.10', '1.5.9') > 0);
  assert.equal(compareVersions('1.5.3', '1.5.3'), 0);
});

test('the public body never carries notes or logs', () => {
  const md = publicBody({ kind: 'crash', error: 'E', area: 'A', versions: '["1.5.3"]', total: 3, first_seen: '2026-11-19T00:00:00Z', last_seen: '2026-11-20T00:00:00Z' });
  assert.match(md, /\*\*3\*\*/);
  assert.doesNotMatch(md, /```/);
});

test('a version newer than the newest release is turned away', async () => {
  assert.equal((await post(report({ version: '9.9.9' }))).status, 400);
  assert.equal(calls.length, 0);
});

test('an issue closed as not planned stays closed', async () => {
  await post(report());
  calls.length = 0;
  issueState = 'closed';
  closedAs = 'not_planned';
  await post(report({ version: '1.5.5' }));
  assert.equal(calls.filter(c => c.method !== 'GET').length, 0);
});

test('a failed public counter never sends the same reports twice', async () => {
  await post(report());
  await post(report(), '5.6.7.8');
  failWhen = c => c.method === 'PATCH';
  calls.length = 0;
  await runDigest();
  assert.equal(calls.filter(c => c.url.endsWith('/comments')).length, 1);
  failWhen = () => false;
  calls.length = 0;
  await runDigest();
  assert.equal(calls.length, 0, 'already sent on');
});

test('report text cannot become links, HTML or cross-references on GitHub', async () => {
  await post(report({ error: '[fix](//evil) <img src=x> owner/repo#1 **bold** | cell', note: '<a href=//x>hi</a> #5' }));
  const [pub, priv] = calls.map(c => c.body.body);
  for (const md of [pub, priv]) {
    assert.doesNotMatch(md, /\[fix\]\(/);
    assert.doesNotMatch(md, /(^|[^\\])<(img|a )/);
    assert.doesNotMatch(md, /repo#1/);
    assert.doesNotMatch(md, /[^\\]\*\*bold/);
  }
  assert.doesNotMatch(calls[0].body.title, /#1/);
});

test('the connection hash is salted with a secret that changes daily, not the date', async () => {
  await post(report());
  const salt = await env.DB.prepare("SELECT value FROM meta WHERE key = 'salt'").first();
  assert.ok(salt && salt.value.length >= 32);
  const key = await env.DB.prepare("SELECT key FROM limits WHERE key LIKE 'ip:%'").first();
  assert.ok(key);
});

test('a path with spaces goes whole', () => {
  assert.equal(scrub('from C:\\Users\\Jane Doe\\AppData\\x.json now'), 'from ‹path› now');
});
