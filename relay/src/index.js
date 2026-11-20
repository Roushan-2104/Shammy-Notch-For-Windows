// Shammy's report relay: takes a report a user chose to send, files it on GitHub, and once a day posts what came in since.
//
// A new problem: a full issue in the private repo (GitHub emails it to the owner) and a summary issue in the public one.
// The same problem again: stored here, then the daily digest comments on the private issue and updates the public counter.

import { Limits, parseReport, issueTitle, privateBody, publicBody, digestComment } from './report.js';

// Anyone can reach this address, so it caps what one connection, and everyone together, can file in a day.
const REPORTS_PER_IP_PER_DAY = 20;
const NEW_PROBLEMS_PER_DAY = 15;

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (request.method === 'GET' && url.pathname === '/') return new Response('Shammy relay\n');
    if (request.method !== 'POST' || url.pathname !== '/report') return new Response('Not found\n', { status: 404 });
    try {
      return await receive(request, env);
    } catch (e) {
      console.error('report failed', e && e.stack || e);
      return json({ ok: false, error: 'server' }, 500);
    }
  },

  async scheduled(_event, env, ctx) {
    ctx.waitUntil(digest(env));
  },
};

async function receive(request, env) {
  const length = Number(request.headers.get('content-length') || 0);
  if (length > Limits.bodyBytes) return json({ ok: false, error: 'too-big' }, 413);
  const raw = await request.text();
  if (raw.length > Limits.bodyBytes) return json({ ok: false, error: 'too-big' }, 413);
  let body;
  try { body = JSON.parse(raw); } catch { return json({ ok: false, error: 'bad-json' }, 400); }
  const report = parseReport(body);
  if (!report) return json({ ok: false, error: 'not-a-report' }, 400);

  const today = new Date().toISOString().slice(0, 10);
  // A random salt that changes daily and is never published: yesterday's hashes can't be turned back into addresses.
  const salt = await daily(env, 'salt', today, async () => crypto.randomUUID());
  const who = await hash(`${request.headers.get('cf-connecting-ip') || 'unknown'}|${salt}`);
  if (!(await allow(env, `ip:${who}`, today, REPORTS_PER_IP_PER_DAY))) return json({ ok: false, error: 'slow-down' }, 429);

  // No version from the future: a made-up "999.0.0" would reopen fixed issues and stop real regressions doing so.
  const latest = await daily(env, 'latest-version', today, () => latestRelease(env));
  if (latest && compareVersions(report.version, latest) > 0) return json({ ok: false, error: 'unknown-version' }, 400);

  let known = await env.DB.prepare('SELECT * FROM problems WHERE signature = ?').bind(report.signature).first();
  if (!known) {
    if (!(await allow(env, 'new-problems', today, NEW_PROBLEMS_PER_DAY))) return json({ ok: false, error: 'slow-down' }, 429);
    if (await fileNew(env, report)) return json({ ok: true, new: true });
    // Another copy of the same new problem got there first: count this one as a repeat.
    known = await env.DB.prepare('SELECT * FROM problems WHERE signature = ?').bind(report.signature).first();
    // ...unless that one failed and gave the problem back meanwhile: Shammy tries again later.
    if (!known) return json({ ok: false, error: 'retry' }, 503);
  }

  if (isNewerThanAll(report.version, known.versions)) await reopenIfFixed(env, known, report.version);
  const versions = addVersion(known.versions, report.version);
  await env.DB.batch([
    env.DB.prepare('INSERT INTO reports (signature, received_at, version, windows, note, log) VALUES (?, ?, ?, ?, ?, ?)')
      .bind(report.signature, new Date().toISOString(), report.version, report.windows, report.note, JSON.stringify(report.log)),
    env.DB.prepare('UPDATE problems SET total = total + 1, last_seen = ?, versions = ? WHERE signature = ?')
      .bind(new Date().toISOString(), versions, report.signature),
  ]);
  return json({ ok: true, new: false });
}

/** Files a problem not seen before; false when another report of it is already being filed. */
async function fileNew(env, report) {
  const now = new Date().toISOString();
  const problem = {
    signature: report.signature, kind: report.kind, error: report.error, area: report.area,
    total: 1, first_seen: now, last_seen: now, versions: JSON.stringify([report.version]),
  };
  // Claim the signature before talking to GitHub, so two reports at once can't open two issues.
  const claim = await env.DB.prepare(`INSERT OR IGNORE INTO problems (signature, kind, error, area, total, first_seen, last_seen, versions)
      VALUES (?, ?, ?, ?, 1, ?, ?, ?)`)
    .bind(problem.signature, problem.kind, problem.error, problem.area, now, now, problem.versions).run();
  if (!claim.meta.changes) return false;
  try {
    await openIssues(env, report, problem);
  } catch (e) {
    // Nothing reached the owner: let the next report of it try again.
    await env.DB.prepare('DELETE FROM problems WHERE signature = ?').bind(problem.signature).run();
    throw e;
  }
  return true;
}

async function openIssues(env, report, problem) {
  const labels = [report.kind, 'auto-reported'];
  // Public first, so the private issue can link to it; a failure there still files the private one.
  let publicIssue = null;
  try {
    publicIssue = await createIssue(env, env.PUBLIC_REPO, issueTitle(report), publicBody(problem), labels);
  } catch (e) {
    console.error('public issue failed', e && e.message);
  }
  const privateIssue = await createIssue(env, env.PRIVATE_REPO, issueTitle(report), privateBody(report, publicIssue, env.PUBLIC_REPO), labels);
  await env.DB.prepare('UPDATE problems SET private_issue = ?, public_issue = ? WHERE signature = ?')
    .bind(privateIssue, publicIssue, problem.signature).run();
}

/** Once a day: a comment per problem with new reports, and the public counters brought up to date. */
async function digest(env) {
  const pending = await env.DB.prepare(
    'SELECT DISTINCT p.* FROM problems p JOIN reports r ON r.signature = p.signature AND r.digested = 0 WHERE p.private_issue IS NOT NULL').all();
  for (const p of pending.results || []) {
    try {
      const { results: reports } = await env.DB.prepare(
        'SELECT * FROM reports WHERE signature = ? AND digested = 0 ORDER BY id').bind(p.signature).all();
      if (!reports.length) continue;
      await github(env, 'POST', `/repos/${env.PRIVATE_REPO}/issues/${p.private_issue}/comments`, { body: digestComment(p, reports) });
      // Sent on: never twice, whatever happens to the public counter below.
      const last = reports[reports.length - 1].id;
      await env.DB.prepare('UPDATE reports SET digested = 1 WHERE signature = ? AND id <= ?').bind(p.signature, last).run();
      try {
        if (p.public_issue) {
          await github(env, 'PATCH', `/repos/${env.PUBLIC_REPO}/issues/${p.public_issue}`, { body: publicBody(p) });
        } else {
          // The public issue failed when the problem was new: try again now.
          const number = await createIssue(env, env.PUBLIC_REPO, issueTitle(p), publicBody(p), [p.kind, 'auto-reported']);
          await env.DB.prepare('UPDATE problems SET public_issue = ? WHERE signature = ?').bind(number, p.signature).run();
        }
      } catch (e) {
        console.error('public counter failed for', p.signature, e && e.message);
      }
    } catch (e) {
      console.error('digest failed for', p.signature, e && e.message);
    }
  }
  // Old reports and the daily limits aren't needed once they've been sent on.
  const monthAgo = new Date(Date.now() - 30 * 86400000).toISOString();
  const today = new Date().toISOString().slice(0, 10);
  await env.DB.batch([
    env.DB.prepare('DELETE FROM reports WHERE digested = 1 AND received_at < ?').bind(monthAgo),
    env.DB.prepare('DELETE FROM limits WHERE day < ?').bind(today),
  ]);
}

async function createIssue(env, repo, title, body, labels) {
  let issue;
  try {
    issue = await github(env, 'POST', `/repos/${repo}/issues`, { title, body, labels });
  } catch (e) {
    // A label GitHub won't set must not lose the report.
    if (!String(e.message).includes('422')) throw e;
    issue = await github(env, 'POST', `/repos/${repo}/issues`, { title, body });
  }
  return issue.number;
}

async function github(env, method, path, body) {
  const response = await fetch(`https://api.github.com${path}`, {
    method,
    headers: {
      Authorization: `Bearer ${env.GITHUB_TOKEN}`,
      Accept: 'application/vnd.github+json',
      'X-GitHub-Api-Version': '2022-11-28',
      'User-Agent': 'shammy-relay',
      'Content-Type': 'application/json',
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok) throw new Error(`GitHub ${method} ${path}: ${response.status}`);
  return response.json();
}

/** A value worked out once a day and kept for the day (a failed make isn't kept, so it's tried again). */
async function daily(env, key, day, make) {
  const row = await env.DB.prepare('SELECT value FROM meta WHERE key = ? AND day = ?').bind(key, day).first();
  if (row) return row.value;
  const value = await make();
  if (value) await env.DB.prepare('INSERT OR REPLACE INTO meta (key, value, day) VALUES (?, ?, ?)').bind(key, value, day).run();
  return value;
}

/** The newest released version ("1.5.3"), or null when GitHub can't say (then no version is turned away). */
async function latestRelease(env) {
  try {
    const release = await github(env, 'GET', `/repos/${env.PUBLIC_REPO}/releases/latest`);
    const tag = String(release.tag_name || '').replace(/^v/i, '');
    return /^\d+\.\d+\.\d+$/.test(tag) ? tag : null;
  } catch (e) {
    console.error('latest release failed', e && e.message);
    return null;
  }
}

/** One more for this key today, or false when it's at the limit. */
async function allow(env, key, day, limit) {
  const row = await env.DB.prepare(
    `INSERT INTO limits (key, day, count) VALUES (?, ?, 1)
     ON CONFLICT(key) DO UPDATE SET count = CASE WHEN day = excluded.day THEN count + 1 ELSE 1 END, day = excluded.day
     RETURNING count`).bind(key, day).first();
  return row.count <= limit;
}

/**
 * A problem closed as fixed that comes back in a version newer than any that reported it: reopen both issues (GitHub
 * emails the owner). Reports from the older versions only count, since those copies still have the bug.
 */
async function reopenIfFixed(env, problem, version) {
  try {
    if (!problem.private_issue) return;
    const issue = await github(env, 'GET', `/repos/${env.PRIVATE_REPO}/issues/${problem.private_issue}`);
    // Only "closed as fixed": one closed as not planned or a duplicate stays closed.
    if (issue.state !== 'closed' || issue.state_reason !== 'completed') return;
    const note = { body: `Seen again in ${version}.` };
    for (const [repo, number] of [[env.PRIVATE_REPO, problem.private_issue], [env.PUBLIC_REPO, problem.public_issue]]) {
      if (!number) continue;
      await github(env, 'PATCH', `/repos/${repo}/issues/${number}`, { state: 'open' });
      await github(env, 'POST', `/repos/${repo}/issues/${number}/comments`, note);
    }
  } catch (e) {
    console.error('reopen failed for', problem.signature, e && e.message);
  }
}

function isNewerThanAll(version, versionsJson) {
  return JSON.parse(versionsJson || '[]').every(v => compareVersions(version, v) > 0);
}

export function compareVersions(a, b) {
  const x = a.split('.').map(Number), y = b.split('.').map(Number);
  for (let i = 0; i < 3; i++) if ((x[i] || 0) !== (y[i] || 0)) return (x[i] || 0) - (y[i] || 0);
  return 0;
}

function addVersion(versionsJson, version) {
  const list = JSON.parse(versionsJson || '[]');
  if (!list.includes(version)) list.push(version);
  return JSON.stringify(list.slice(-8));
}

async function hash(text) {
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(text));
  return [...new Uint8Array(digest)].slice(0, 12).map(b => b.toString(16).padStart(2, '0')).join('');
}

function json(value, status = 200) {
  return new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } });
}
