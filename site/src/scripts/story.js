// The story under the opening. One stage stays on screen while you scroll: the island glides up from the opening,
// pins near the top, and opens into each chapter as Shammy would, while the light behind it takes that chapter's
// colours. Scrolling plays each moment; the buttons inside work too.
import { skyAt, fmtHour, ROMAN, DEVA } from './shared.js';

const hex = (h, a) => [parseInt(h.slice(1, 3), 16), parseInt(h.slice(3, 5), 16), parseInt(h.slice(5, 7), 16), a];
const sky = skyAt(new Date().getHours());
const HERO = [[107, 63, 216, .95], [25, 195, 178, .85], [224, 87, 122, .6], [60, 90, 220, .5]];

// Each scene: how long it lasts (in stage heights), the light behind the island, the beam, and the island's status.
const SCENES = [
  { key: 'open', len: 1, world: HERO, beam: 1, status: 'Charging 100%' },
  { key: 'claude', len: 1.4, world: [[107, 63, 216, .8], [242, 179, 90, .38], [224, 87, 122, .42], [60, 90, 220, .3]], beam: .55 },
  { key: 'talk', len: 1.4, world: [[25, 195, 178, .7], [60, 200, 120, .42], [107, 63, 216, .42], [30, 90, 160, .35]], beam: .45 },
  { key: 'music', len: 1.4, world: [[200, 40, 40, .8], [40, 150, 70, .6], [120, 20, 30, .55], [90, 30, 30, .4]], beam: .25 },
  { key: 'phone', len: 1.4, world: [[90, 120, 255, .62], [107, 63, 216, .62], [25, 195, 178, .38], [40, 60, 160, .4]], beam: .5 },
  { key: 'stage', len: 1.4, world: [[255, 190, 140, .5], [224, 87, 122, .38], [255, 220, 180, .32], [120, 70, 50, .4]], beam: .3 },
  { key: 'alarm', len: 1.4, world: [hex(sky.hor, .72), hex(sky.mid, .85), hex(sky.edge, .5), hex(sky.top, .9)], beam: .2 },
  { key: 'light', len: 1.2, world: [[70, 70, 110, .4], [40, 40, 70, .35], [107, 63, 216, .28], [25, 195, 178, .16]], beam: .15, status: 'Using 90 MB' },
  { key: 'get', len: 1.4, world: HERO, beam: 1, last: true },
];

const clamp = (v, a = 0, b = 1) => Math.min(b, Math.max(a, v));
const smooth = (v, a, b) => { const x = clamp((v - a) / (b - a)); return x * x * (3 - 2 * x); };
const lerp = (a, b, k) => a + (b - a) * k;
const esc = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;');

export function mountStory(journey, { reduced, cover, release, version }) {
  const stage = journey.querySelector('.stage');
  const card = stage.querySelector('.isle');
  const inner = card.querySelector('.isle-in');
  const copies = [...stage.querySelectorAll('.copy')];
  const rail = stage.querySelector('.rail');
  const railItems = [...rail.querySelectorAll('button')];
  const phone = stage.querySelector('.phone');
  const fallback = stage.querySelector('.css-island');
  const fallbackStatus = fallback.querySelector('.ci-status');
  const heroParts = { title: stage.querySelector('.open-copy'), foot: stage.querySelector('.open-foot'), moments: stage.querySelector('.moments') };
  let gl = null, sceneNow = -1, html = '', user = {};

  // Scene starts, in stage heights.
  const starts = []; SCENES.reduce((a, s) => { starts.push(a); return a + s.len; }, 0);
  const total = starts[starts.length - 1] + SCENES[SCENES.length - 1].len;
  let sw = 0, sh = 0, scale = 1, mobile = false, stickTop = 0;
  function measure() {
    sw = stage.clientWidth; sh = stage.clientHeight; mobile = sw < 760;
    stickTop = parseFloat(getComputedStyle(stage).top) || 0;
    scale = mobile ? 1 : clamp(sh / 800, .78, 1.22);
    journey.style.height = `${(total + 1) * sh}px`;
    journey.querySelectorAll('[data-at]').forEach((m) => { m.style.top = `${(starts[+m.dataset.at] + .35) * sh}px`; });
  }

  // What the open island holds in each chapter, at moment m (0 to 1). w is its width before scaling.
  const CONTENT = {
    claude(m) {
      const answer = user.claude || (m > .55 ? 'allow' : '');
      const title = answer === 'allow' ? (m > .8 || user.claude ? 'Allowed. 42 tests passed' : 'Allowed. Running npm test') : answer === 'deny' ? 'Denied. Claude will try another way' : 'Wants to run a command';
      return { w: 640, html: `<div class="c-head"><span class="eyes"><i></i><i></i></span><div><div class="cap">Claude Code · login-fix</div><div class="c-title">${title}</div></div><span class="c-meta">${answer ? (answer === 'allow' ? '✓ allowed' : '✗ denied') : `waiting 0:0${Math.floor(m * 9)}`}</span></div>
        <code class="c-code">npm test</code>
        <div class="c-acts"><button type="button" class="pill${answer === 'deny' ? ' on' : ''}" data-act="deny">Deny</button><button type="button" class="pill pri${answer === 'allow' ? ' on' : ''}" data-act="allow">Allow</button></div>` };
    },
    talk(m) {
      const deva = user.script ? user.script === 'deva' : m > .78;
      const n = user.words ?? (deva ? 8 : Math.min(8, Math.floor(m * 11)));
      const words = (deva ? DEVA : ROMAN).slice(0, n).join(' ');
      return { w: 700, html: `<div class="c-head"><span class="rec"></span><span class="cap on">Dictating</span><span class="cap">into WhatsApp</span><span class="wave" aria-hidden="true"><i></i><i></i><i></i><i></i><i></i><i></i><i></i></span></div>
        <div class="c-said${deva ? ' deva' : ''}" aria-live="polite">${words}<span class="caret"></span></div>
        <div class="c-acts"><span class="switch" role="group" aria-label="Script"><button type="button" data-act="roman" aria-pressed="${!deva}">Roman</button><button type="button" class="deva" data-act="deva" aria-pressed="${deva}">देवनागरी</button></span><button type="button" class="pill" data-act="talk">Hold to talk</button></div>` };
    },
    music(m) {
      const at = Math.round(lerp(64, 250, m)), paused = !!user.paused;
      return { w: 660, html: `<div class="c-music"><img src="${cover}" alt="" width="132" height="132"><div class="c-track"><div class="cap">${paused ? 'Paused' : 'Now playing'}</div><div class="c-song">Night Song</div><div class="c-artist">Nusrat Fateh Ali Khan &amp; Michael Brook</div>
        <div class="c-bar"><i style="width:${(at / 367 * 100).toFixed(1)}%"></i></div><div class="c-times"><span>${Math.floor(at / 60)}:${String(at % 60).padStart(2, '0')}</span><button type="button" class="play" data-act="play" aria-label="${paused ? 'Play' : 'Pause'}">${paused ? '▶' : '❚❚'}</button><span>6:07</span></div></div></div>` };
    },
    phone(m) {
      const p = user.sent ? Math.min(100, Math.floor((performance.now() - user.sent) / 16)) : Math.min(100, Math.floor(m * 150));
      phone.classList.toggle('got', p >= 100);
      return { w: 580, html: `<div class="c-head"><span class="eyes"><i></i><i></i></span><div><div class="cap">To phone · Pixel 8</div><div class="c-title">${p < 100 ? 'Sending report.pdf' : 'On your phone'}</div></div><span class="c-meta big">${p}%</span></div>
        <div class="c-progress"><i style="width:${p}%"></i></div>
        <div class="c-acts"><button type="button" class="pill" data-act="send">Send it again</button></div>` };
    },
    stage(m) {
      const line = 'Thanks for joining, everyone. Here\'s what changes for you this week.'.split(' ');
      const k = Math.floor(m * (line.length + 1));
      return { w: 780, html: `<div class="c-prompt">${line.map((w, i) => `<span${i < k ? ' class="read"' : ''}>${esc(w)}</span>`).join(' ')}</div><div class="cap center">Stage · follows your voice</div>` };
    },
    alarm(m) {
      const [t, ap] = fmtHour(new Date().getHours()).split(' ');
      if (user.stopped) return { w: 640, html: `<div class="c-sky" style="--top:${sky.top};--mid:${sky.mid};--hor:${sky.hor}"><div class="cap">Wake up · ${sky.name}</div><div class="c-alarm">Stopped.</div><div class="c-note">Have a good one.</div></div>` };
      const y = lerp(150, 34, smooth(m, 0, .8));
      const body = sky.moon ? `background:radial-gradient(circle at 40% 40%,${sky.core},${sky.edge});box-shadow:0 0 50px rgba(${sky.glow},.5)` : `background:radial-gradient(circle at 45% 40%,${sky.core},${sky.edge});box-shadow:0 0 80px 24px rgba(${sky.glow},.6)`;
      return { w: 640, html: `<div class="c-sky" style="--top:${sky.top};--mid:${sky.mid};--hor:${sky.hor}"><span class="c-body${sky.moon ? ' moon' : ''}" style="top:${y.toFixed(0)}px;${body}"></span><div class="cap">Wake up · ${sky.name}</div><div class="c-alarm">${t}<span>${ap}</span></div><button type="button" class="c-stop" style="background:${sky.stop}" data-act="stop">Stop</button></div>` };
    },
    get() {
      return { w: 600, html: `<div class="c-head"><span class="eyes"><i></i><i></i></span><div><div class="cap">Shammy ${version} · Windows 10 and 11</div><div class="c-title">Free, and about 104 MB</div></div></div>
        <div class="c-acts start"><a class="pill pri big" href="${release}/download/Shammy-Setup.exe">Download Setup.exe</a><a class="pill" href="${release}/download/Shammy-Portable.zip">Portable .zip</a></div>` };
    },
  };

  // Taps inside the island.
  let talkTimer;
  card.addEventListener('click', (e) => {
    const act = e.target.closest('[data-act]')?.dataset.act;
    if (!act || act === 'talk') return;
    if (act === 'allow' || act === 'deny') user.claude = act;
    else if (act === 'roman' || act === 'deva') { user.script = act; user.words = 8; }
    else if (act === 'play') user.paused = !user.paused;
    else if (act === 'send') { user.sent = performance.now(); tick(1700); }
    else if (act === 'stop') user.stopped = true;
    render(true);
  });
  const startTalk = (e) => {
    if (!e.target.closest('[data-act="talk"]') || talkTimer) return;
    e.preventDefault();
    user.script = user.script || 'roman'; user.words = 0;
    talkTimer = setInterval(() => { user.words = Math.min(8, user.words + 1); render(true); }, 260);
  };
  const stopTalk = () => { clearInterval(talkTimer); talkTimer = 0; if (user.words != null) { user.words = 8; render(true); } };
  card.addEventListener('pointerdown', startTalk);
  card.addEventListener('pointerup', stopTalk);
  card.addEventListener('pointerleave', stopTalk);
  card.addEventListener('keydown', (e) => { if ((e.key === 'Enter' || e.key === ' ') && !e.repeat) startTalk(e); });
  card.addEventListener('keyup', (e) => { if (e.key === 'Enter' || e.key === ' ') stopTalk(); });

  railItems.forEach((b) => b.addEventListener('click', () => {
    const top = journey.getBoundingClientRect().top + scrollY - stickTop;
    scrollTo({ top: top + (starts[+b.dataset.go] + SCENES[+b.dataset.go].len * .45) * sh, behavior: reduced ? 'auto' : 'smooth' });
  }));

  // Time-driven moments (sending again) keep drawing for a moment after the tap.
  function tick(ms) {
    const until = performance.now() + ms;
    const loop = () => { render(true); if (performance.now() < until) requestAnimationFrame(loop); };
    requestAnimationFrame(loop);
  }

  function render(force) {
    const y = clamp((stickTop - journey.getBoundingClientRect().top) / sh, 0, total);
    let i = SCENES.length - 1;
    while (i > 0 && y < starts[i]) i--;
    const s = SCENES[i], t = clamp((y - starts[i]) / s.len);

    if (i !== sceneNow) {
      sceneNow = i; user = {}; clearInterval(talkTimer); talkTimer = 0;
      stage.dataset.scene = s.key;
      s.world.forEach((c, k) => stage.style.setProperty(`--w${k}`, `rgba(${c.join(',')})`));
      gl?.world(s.world, s.beam);
      gl?.status(s.status || '');
      gl?.tab(i === 0);
      fallbackStatus.textContent = s.status || '';
      copies.forEach((c) => { c.hidden = +c.dataset.i !== i; });
      heroParts.title.hidden = heroParts.moments.hidden = i !== 0;
      rail.hidden = i === 0;
      phone.hidden = s.key !== 'phone';
      railItems.forEach((b) => b.setAttribute('aria-current', String(+b.dataset.go === i)));
    }

    // The island: centred in the opening, then glides up and pins near the top.
    const restW = mobile ? Math.min(300, sw * .72) : 380, restH = restW / 7.38;
    const pinTop = sh * (mobile ? .06 : .09);
    const g = i === 0 ? smooth(t, .3, 1) : 1;
    const heroW = mobile ? sw * .84 : sw * .44;
    const w = lerp(heroW, restW * scale, reduced ? (g > .5 ? 1 : 0) : g);
    const cy = lerp(sh * .5, pinTop + (restH * scale) / 2, reduced ? (g > .5 ? 1 : 0) : g);
    gl?.place(sw / 2, cy, w);
    fallback.style.transform = `translate(-50%, -50%) translate(0, ${(cy - sh * .5).toFixed(1)}px) scale(${(w / 600).toFixed(3)})`;

    // The opening's words and moments step aside as the island leaves.
    heroParts.title.style.opacity = String(i === 0 ? 1 - smooth(t, 0, .45) : 0);
    heroParts.title.style.transform = i === 0 ? `translateY(${(-smooth(t, 0, .6) * 80).toFixed(1)}px)` : '';
    heroParts.foot.style.opacity = String(i === 0 ? 1 - smooth(t, 0, .25) : 0);
    heroParts.foot.hidden = i !== 0 || t > .3;
    heroParts.moments.style.opacity = String(i === 0 ? 1 - smooth(t, 0, .4) : 0);
    heroParts.moments.style.setProperty('--spread', String(1 + (i === 0 ? smooth(t, 0, .5) : 1) * .25));

    // The island opens out of its resting shape, holds while the moment plays, and closes before the next.
    const content = CONTENT[s.key];
    let k = 0, m = 0;
    if (content) {
      k = reduced ? 1 : smooth(t, 0, .14) * (s.last ? 1 : 1 - smooth(t, .88, 1));
      m = clamp((t - .14) / .72);
      const c = content(m);
      if (force || c.html !== html) { html = c.html; inner.innerHTML = c.html; }
      const tw = Math.min(c.w, (sw - 32) / scale);
      inner.style.width = `${tw}px`;
      const th = inner.offsetHeight;
      card.style.width = `${lerp(restW, tw, k).toFixed(1)}px`;
      card.style.height = `${lerp(restH, th, k).toFixed(1)}px`;
      card.style.borderRadius = `${lerp(restH / 2, 34, k).toFixed(1)}px`;
    } else if (html) { html = ''; inner.innerHTML = ''; }
    card.style.top = `${pinTop}px`;
    card.style.transform = `translateX(-50%) scale(${scale})`;
    card.style.opacity = String(content ? smooth(k, 0, .08) : 0);
    card.style.visibility = content && k > 0 ? 'visible' : 'hidden';
    inner.style.opacity = String(smooth(k, .55, 1));
    card.toggleAttribute('inert', k < .9);
    gl?.show(k < .2);
    phone.style.opacity = String(s.key === 'phone' ? k : 0);

    // Each chapter's words come in after the island opens and leave before it closes.
    copies.forEach((c) => {
      if (+c.dataset.i !== i) { c.style.opacity = '0'; return; }
      const o = reduced ? 1 : smooth(t, .04, .18) * (s.last ? 1 : 1 - smooth(t, .86, .98));
      c.style.opacity = String(o);
      c.style.transform = `translateY(${((1 - o) * 24).toFixed(1)}px)`;
    });
  }

  let queued = false;
  const onScroll = () => { if (!queued) { queued = true; requestAnimationFrame(() => { queued = false; render(); }); } };
  addEventListener('scroll', onScroll, { passive: true });
  addEventListener('resize', () => { measure(); render(true); });
  measure(); render(true);

  return {
    attach(island) {
      gl = island;
      const s = SCENES[sceneNow];
      gl.world(s.world, s.beam); gl.status(s.status || ''); gl.tab(sceneNow === 0);
      stage.classList.add('gl-on');
      render(true);
    },
  };
}
