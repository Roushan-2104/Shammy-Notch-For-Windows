// What a report may hold, and how it reads on GitHub. No Cloudflare or network code here, so it runs under node:test.

export const Limits = {
  bodyBytes: 32 * 1024,
  logLines: 30,
  lineChars: 300,
  noteChars: 500,
  fieldChars: 160,
};

const KINDS = new Set(['crash', 'error']);
const SIGNATURE = /^[a-z0-9][a-z0-9/._-]{2,79}$/;
const VERSION = /^\d{1,3}\.\d{1,3}\.\d{1,3}$/;

/** A second pass of what Shammy already removed, in case an older or modified copy sends something it shouldn't. */
export function scrub(text) {
  // Dates and times stay: set them aside so the long-number rule can't take them.
  const kept = [];
  const keep = m => `\u0001${kept.push(m) - 1}\u0002`;
  return String(text)
    .replace(/\b\d{4}-\d{2}-\d{2}(?:[ T]\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?)?\b|\b\d{1,2}:\d{2}(?::\d{2}(?:\.\d+)?)?\b/g, keep)
    .replace(/[\w.+-]+@[\w-]+(\.[\w-]+)+/g, '‹email›')
    .replace(/\b(?:https?|ftp):\/\/\S+/gi, '‹link›')
    .replace(/\bwww\.\S+/gi, '‹link›')
    // Folders may have spaces ("C:\Users\Jane Doe\..."): anything up to each backslash is a folder.
    .replace(/[a-z]:\\(?:[^\\\r\n"'<>|:*?]*\\)*[^\s\\"'<>|:*?]*/gi, '‹path›')
    .replace(/\\\\[^\s"'<>|]+/g, '‹path›')
    // Long numbers: phone, OTP, card, PIN code.
    .replace(/\d[\d -]{2,}\d/g, m => (m.replace(/\D/g, '').length >= 4 ? '‹number›' : m))
    .replace(/\u0001(\d+)\u0002/g, (_, i) => kept[Number(i)]);
}

function text(value, max) {
  if (typeof value !== 'string') return '';
  // Control characters (other than tab) and Markdown that could ping people or hide text.
  const clean = scrub(value).replace(/[\u0000-\u0008\u000b-\u001f\u007f]/g, '').replace(/@/g, '＠').trim();
  return clean.length > max ? clean.slice(0, max - 1) + '…' : clean;
}

/** The report as Shammy sent it, checked and trimmed; null when it isn't one. */
export function parseReport(body) {
  if (!body || typeof body !== 'object' || Array.isArray(body)) return null;
  if (!KINDS.has(body.kind)) return null;
  if (typeof body.signature !== 'string' || !SIGNATURE.test(body.signature)) return null;
  if (typeof body.version !== 'string' || !VERSION.test(body.version)) return null;
  const error = text(body.error, Limits.fieldChars);
  if (!error) return null;
  const log = Array.isArray(body.log) ? body.log.slice(-Limits.logLines).map(l => text(l, Limits.lineChars)) : [];
  const stack = Array.isArray(body.stack) ? body.stack.slice(0, 6).map(l => text(l, Limits.lineChars)) : [];
  const when = typeof body.when === 'string' && !Number.isNaN(Date.parse(body.when)) ? new Date(body.when).toISOString() : new Date().toISOString();
  return {
    kind: body.kind,
    signature: body.signature,
    version: body.version,
    error,
    area: text(body.area, 60) || 'Shammy',
    where: text(body.where, Limits.fieldChars),
    windows: text(body.windows, 60),
    when,
    times: Number.isInteger(body.times) && body.times > 0 ? Math.min(body.times, 9999) : 1,
    healing: text(body.healing, Limits.fieldChars),
    note: text(body.note, Limits.noteChars),
    stack,
    log,
  };
}

const KIND_WORD = { crash: 'Crash', error: 'Error' };

export function issueTitle(r) {
  // Titles show as plain text, but GitHub still links "#12" in them.
  return `${KIND_WORD[r.kind]}: ${r.error} · ${r.area}`.replace(/#/g, '#‍');
}

/**
 * Text from a report, safe in Markdown: no link, image, HTML, emphasis or table cell can come of it, and "#12" or
 * "owner/repo#12" can't become a cross-reference on someone else's issue.
 */
export function safe(value) {
  return String(value ?? '').replace(/[\\`*_{}[\]()<>!|~]/g, '\\$&').replace(/#/g, '#\u200d');
}

/** Inside a code span only a backtick could end it. */
function code(value) {
  return '`' + String(value ?? '').replace(/`/g, "'") + '`';
}

function day(iso) {
  return new Date(iso).toLocaleDateString('en-US', { month: 'short', day: 'numeric', timeZone: 'UTC' });
}

function fence(lines) {
  // A line can't close the fence early.
  return '```text\n' + lines.join('\n').replace(/```/g, "'''") + '\n```';
}

function row(name, value) {
  // Values come through safe() or code(): a | can't open another cell.
  return value ? `| ${name} | ${value} |\n` : '';
}

/** The private issue: everything in the report. GitHub emails it to the owner. */
export function privateBody(r, publicIssue, publicRepo) {
  let md = '| | |\n|---|---|\n';
  md += row('What', `${KIND_WORD[r.kind]} · ${safe(r.error)}`);
  md += row('Where', r.where ? `${safe(r.area)} · ${code(r.where)}` : safe(r.area));
  md += row('Versions', `Shammy ${r.version}${r.windows ? ' · ' + safe(r.windows) : ''}`);
  md += row('When', `${day(r.when)} (UTC)`);
  if (r.kind === 'error') md += row('Times', `${r.times} that day`);
  md += row('Self-healing', r.healing && safe(r.healing));
  if (publicIssue) md += row('Public issue', `${publicRepo}#${publicIssue}`);
  md += row('Signature', code(r.signature));
  md += '\n### User’s note\n\n' + (r.note ? '> ' + safe(r.note).replace(/\n/g, '\n> ') : '_None._') + '\n';
  if (r.stack.length) md += '\n### Top of the stack\n\n' + fence(r.stack) + '\n';
  if (r.log.length) md += `\n<details open><summary>Last ${r.log.length} log lines (cleaned)</summary>\n\n` + fence(r.log) + '\n\n</details>\n';
  return md;
}

/** The public issue: a summary and a counter, never log lines or notes. */
export function publicBody(p) {
  const versions = JSON.parse(p.versions || '[]').join(', ');
  let md = 'Shammy reported this by itself, each time with the user’s OK. The log lines and notes are kept private; this page only counts it.\n\n';
  md += '| | |\n|---|---|\n';
  md += row('Error', safe(p.error));
  md += row('Part of Shammy', safe(p.area));
  md += row('Versions', versions);
  md += row('Reports', `**${p.total}** · first ${day(p.first_seen)}, last ${day(p.last_seen)} · updated daily`);
  if (p.kind === 'crash') md += row('Meanwhile', 'Shammy pauses the part that crashed on a PC where it crashes twice in a day, until the next update');
  return md;
}

/** The daily comment on the private issue: what came in since the last one. */
export function digestComment(p, reports) {
  const counts = {};
  for (const r of reports) counts[r.version] = (counts[r.version] || 0) + 1;
  const byVersion = Object.entries(counts).map(([v, n]) => `${v} ×${n}`).join(', ');
  let md = `**+${reports.length} report${reports.length === 1 ? '' : 's'}** since the last digest (total ${p.total}) · ${byVersion}\n`;
  const notes = reports.filter(r => r.note).map(r => `> ${safe(r.note).replace(/\n/g, ' ')}`);
  if (notes.length) md += '\n**Notes**\n\n' + notes.join('\n\n') + '\n';
  for (const [i, r] of reports.entries()) {
    const log = JSON.parse(r.log || '[]');
    const piece = `\n<details><summary>Report ${i + 1} · ${r.version}${r.windows ? ' · ' + safe(r.windows) : ''} · ${day(r.received_at)}</summary>\n\n${fence(log)}\n\n</details>\n`;
    // GitHub refuses a comment over 65,536 characters.
    if (md.length + piece.length > 60000) {
      md += `\n_${reports.length - i} more not shown (comment size limit)._\n`;
      break;
    }
    md += piece;
  }
  return md;
}
