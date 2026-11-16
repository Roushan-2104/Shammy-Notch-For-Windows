// The practice desktop: circle the flight number, hold to talk, answer Claude, ring an alarm in any hour's sky, and
// open the dial with a middle click. The island at the top answers each one, as Shammy does.
const SKIES = {
  dawn: { name: 'Dawn', top: '#1E1626', mid: '#5A2E3A', hor: '#FFB070', core: '#FFF6E6', edge: '#FFA860', glow: '255,186,120', stop: '#FFF1E4', moon: false },
  day: { name: 'Day', top: '#16304F', mid: '#2F5C88', hor: '#E9C48C', core: '#FFFFFF', edge: '#FFE2A8', glow: '255,236,190', stop: '#FFFFFF', moon: false },
  sunset: { name: 'Sunset', top: '#170F2E', mid: '#5E1E4C', hor: '#F2504A', core: '#FFE0C8', edge: '#F04A32', glow: '255,110,90', stop: '#FFE9DE', moon: false },
  night: { name: 'Night', top: '#04050B', mid: '#0A1226', hor: '#1C2C50', core: '#F4F7FF', edge: '#B8C8F0', glow: '170,195,255', stop: '#E9EEFF', moon: true },
};
const skyAt = (h) => (h >= 5 && h < 9 ? SKIES.dawn : h >= 9 && h < 16 ? SKIES.day : h >= 16 && h < 19 ? SKIES.sunset : SKIES.night);
const fmtHour = (h) => `${h % 12 === 0 ? 12 : h % 12}:30 ${h < 12 ? 'AM' : 'PM'}`;
const ROMAN = ['haan,', 'kal', '6', 'baje', 'cafe', 'pe', 'milte', 'hain'];
const DEVA = ['हाँ,', 'कल', '6', 'बजे', 'कैफ़े', 'पे', 'मिलते', 'हैं'];

export function mountDesk(desk) {
  if (!desk) return;
  const island = desk.querySelector('.desk-island');
  const rest = island.innerHTML;
  const done = new Set();
  let back, talking = false, timer, words = 0;
  let hour = new Date().getHours();

  const show = (html, width, ms) => {
    clearTimeout(back);
    island.innerHTML = html;
    island.style.width = width ? `${width}px` : '';
    if (ms) back = setTimeout(restIsland, ms);
  };
  function restIsland() {
    island.innerHTML = rest; island.style.width = '';
    const t = island.querySelector('[data-clock]');
    if (t) t.textContent = new Date().toLocaleTimeString('en-US', { hour: 'numeric', minute: '2-digit' });
  }
  function tried(what) {
    done.add(what);
    if (done.size < 3) return;
    const lines = [];
    if (done.has('circle')) lines.push('You circled a flight and got it tracked.');
    if (done.has('talk')) lines.push('You said a Hinglish sentence and it typed itself.');
    if (done.has('claude')) lines.push('You answered Claude from the island.');
    if (done.has('alarm')) lines.push(`Your alarm rang in the ${skyAt(hour).name.toLowerCase()} sky.`);
    const card = desk.querySelector('.minute');
    card.querySelector('[data-minute]').textContent = lines.join(' ');
    card.hidden = false;
  }

  // Circle: draw around the flight number with the left button (or a finger).
  const lasso = desk.querySelector('.lasso polyline');
  const flight = desk.querySelector('.flight');
  let points = null;
  const local = (e) => { const r = desk.getBoundingClientRect(); return [e.clientX - r.left, e.clientY - r.top]; };
  desk.addEventListener('pointerdown', (e) => {
    if (e.button === 1) { e.preventDefault(); openDial(...local(e)); return; }
    if (e.button !== 0 || e.target.closest('button, input, a, .desk-bar, .dial, .minute')) return;
    closeDial();
    points = [local(e)];
    desk.setPointerCapture(e.pointerId);
  });
  desk.addEventListener('pointermove', (e) => {
    if (!points) return;
    points.push(local(e));
    lasso.setAttribute('points', points.map((p) => p.map(Math.round).join(',')).join(' '));
  });
  desk.addEventListener('pointerup', () => {
    if (!points) return;
    const pts = points; points = null;
    setTimeout(() => lasso.setAttribute('points', ''), 700);
    if (pts.length < 6) return;
    const xs = pts.map((p) => p[0]), ys = pts.map((p) => p[1]);
    const d = desk.getBoundingClientRect(), f = flight.getBoundingClientRect();
    const fx = f.left - d.left, fy = f.top - d.top;
    const hit = Math.min(...xs) < fx + f.width * .6 && Math.max(...xs) > fx + f.width * .4 && Math.min(...ys) < fy + f.height && Math.max(...ys) > fy && Math.max(...xs) - Math.min(...xs) < 420;
    if (hit) circled();
    else show('<div class="pad">Nothing to answer there. Circle the flight number in the email.</div>', 460, 2600);
  });
  function circled() {
    show(`<div class="pad"><div class="row"><span class="fly">✈</span><div><small>Flight · IndiGo</small><div class="big">6E 2134</div></div></div><div class="row gap"><span class="pill on">Track flight</span><span class="pill">Copy</span><span class="pill">Search</span></div></div>`, 520, 5200);
    tried('circle');
  }
  // Without a pointer: focus the flight number and press Enter, and the ring is drawn for you.
  function circleForMe() {
    const d = desk.getBoundingClientRect(), f = flight.getBoundingClientRect();
    const cx = f.left - d.left + f.width / 2, cy = f.top - d.top + f.height / 2, rx = f.width / 2 + 16, ry = f.height / 2 + 10;
    lasso.setAttribute('points', Array.from({ length: 33 }, (_, i) => { const a = i / 32 * Math.PI * 2; return `${Math.round(cx + Math.cos(a) * rx)},${Math.round(cy + Math.sin(a) * ry)}`; }).join(' '));
    setTimeout(() => lasso.setAttribute('points', ''), 900);
    circled();
  }
  flight.addEventListener('keydown', (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); circleForMe(); } });
  desk.addEventListener('auxclick', (e) => { if (e.button === 1) e.preventDefault(); });

  // Dictate: hold the button, or hold D while the desktop has focus.
  const startTalk = () => {
    if (talking) return;
    talking = true; words = 0; closeDial();
    clearInterval(timer);
    const draw = () => show(`<div class="pad"><div class="row"><span class="live"></span><b class="caps">Dictating</b><small>into WhatsApp · Roman</small></div><div class="said">${ROMAN.slice(0, words).join(' ')}<span class="caret">|</span></div></div>`, 600);
    draw();
    timer = setInterval(() => { words = Math.min(ROMAN.length, words + 1); draw(); }, 260);
  };
  const stopTalk = () => {
    if (!talking) return;
    talking = false; clearInterval(timer);
    const sent = desk.querySelector('[data-wa-sent]');
    sent.textContent = ROMAN.join(' '); sent.hidden = false;
    show(`<div class="pad"><small>Typed into WhatsApp. Say "in Hindi" next time for</small><div class="said deva">${DEVA.join(' ')}</div></div>`, 600, 3400);
    tried('talk');
  };
  const talk = desk.querySelector('[data-talk]');
  talk.addEventListener('pointerdown', (e) => { e.preventDefault(); startTalk(); });
  talk.addEventListener('pointerup', stopTalk);
  talk.addEventListener('pointerleave', stopTalk);
  talk.addEventListener('keydown', (e) => { if ((e.key === 'Enter' || e.key === ' ') && !e.repeat) { e.preventDefault(); startTalk(); } });
  talk.addEventListener('keyup', (e) => { if (e.key === 'Enter' || e.key === ' ') stopTalk(); });
  desk.addEventListener('keydown', (e) => { if ((e.key === 'd' || e.key === 'D') && !e.repeat && !e.target.closest('input')) startTalk(); });
  desk.addEventListener('keyup', (e) => { if (e.key === 'd' || e.key === 'D') stopTalk(); });

  // Claude asks; the island holds Allow and Deny.
  const ask = desk.querySelector('[data-term-ask]'), result = desk.querySelector('[data-term-result]');
  function askClaude() {
    closeDial();
    show(`<div class="pad"><div class="row"><span class="eyes"><i></i><i></i></span><b>Claude Code</b><small>login-fix · wants to run</small></div><div class="cmd">npm test</div><div class="row gap"><button type="button" class="pill" data-answer="deny">Deny</button><button type="button" class="pill on" data-answer="allow">Allow</button></div></div>`, 520);
    island.querySelectorAll('[data-answer]').forEach((b) => b.addEventListener('click', () => {
      const allow = b.dataset.answer === 'allow';
      ask.classList.add('done');
      result.textContent = allow ? '✓ 42 tests passed (allowed from Shammy)' : '✗ Denied from Shammy';
      show(`<div class="pad">${allow ? 'Allowed. 42 tests passed.' : 'Denied. Claude will try another way.'}</div>`, 420, 2600);
      tried('claude');
    }));
    island.querySelector('[data-answer="allow"]').focus();
  }
  desk.querySelector('[data-claude]').addEventListener('click', askClaude);

  // Alarm: any hour, in that hour's sky; one tap stops it.
  const range = desk.querySelector('[data-hour]'), label = desk.querySelector('[data-hour-label]');
  range.value = String(hour); label.textContent = fmtHour(hour);
  range.addEventListener('input', () => { hour = +range.value; label.textContent = fmtHour(hour); });
  function ring() {
    closeDial();
    const s = skyAt(hour), [t, ap] = fmtHour(hour).split(' ');
    const body = s.moon
      ? `<span class="body moon" style="background:radial-gradient(circle at 40% 40%,${s.core},${s.edge});box-shadow:0 0 50px rgba(${s.glow},.5)"></span>`
      : `<span class="body" style="background:radial-gradient(circle at 45% 40%,${s.core},${s.edge});box-shadow:0 0 70px 20px rgba(${s.glow},.55)"></span>`;
    show(`<div class="sky" style="background:linear-gradient(180deg,${s.top} 0%,${s.top} 28%,${s.mid} 64%,${s.hor} 100%)">${body}<small class="caps">Wake up · ${s.name}</small><div class="alarm-time">${t}<span>${ap}</span></div><button type="button" class="stop" style="background:${s.stop}" data-stop>Stop</button></div>`, 640);
    island.querySelector('[data-stop]').addEventListener('click', restIsland);
    island.querySelector('[data-stop]').focus();
    tried('alarm');
  }
  desk.querySelector('[data-ring]').addEventListener('click', ring);

  // The dial: a middle click (or the button) opens it where you are; each slice runs that thing.
  const dial = desk.querySelector('.dial');
  const slices = [['Circle', circleForMe],
    ['Dictate', () => { startTalk(); setTimeout(stopTalk, 2400); }], ['Claude', askClaude], ['Alarm', ring],
    ['Stage', () => show('<div class="pad">Stage puts your script under the camera and follows your voice.</div>', 520, 3200)],
    ['To phone', () => show('<div class="pad">Sent report.pdf to your phone over your Wi-Fi.</div>', 460, 3000)]];
  function openDial(x, y) {
    dial.innerHTML = '';
    slices.forEach(([name, run], i) => {
      const a = -Math.PI / 2 + i * Math.PI / 3, b = document.createElement('button');
      b.type = 'button'; b.textContent = name;
      b.style.left = `${Math.cos(a) * 110}px`; b.style.top = `${Math.sin(a) * 110}px`;
      b.addEventListener('click', () => { closeDial(); run(); });
      dial.append(b);
    });
    const r = desk.getBoundingClientRect();
    dial.style.left = `${Math.min(Math.max(x, 160), r.width - 160)}px`;
    dial.style.top = `${Math.min(Math.max(y, 170), r.height - 170)}px`;
    dial.hidden = false;
    dial.querySelector('button').focus();
  }
  function closeDial() { dial.hidden = true; }
  desk.querySelector('[data-dial-open]').addEventListener('click', () => { const r = desk.getBoundingClientRect(); openDial(r.width / 2, r.height / 2); });
  desk.addEventListener('keydown', (e) => { if (e.key === 'Escape') { closeDial(); restIsland(); } });
}
