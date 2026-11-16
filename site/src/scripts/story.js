// The story under the opening. One stage stays on screen while you scroll: the island glides up from the opening,
// pins near the top, and opens into each chapter as Shammy would, while the light behind it takes that chapter's
// colours. A scroll or swipe glides to the next chapter, and that chapter's moment then plays by itself on a GSAP
// timeline; the buttons inside the island work too.
import { gsap } from 'gsap';
import { ScrollTrigger } from 'gsap/ScrollTrigger';
import { skyAt, fmtHour, ROMAN, DEVA } from './shared.js';
import { chime, setSound, soundIsOn } from './sound.js';

gsap.registerPlugin(ScrollTrigger);

const hex = (h, a) => [parseInt(h.slice(1, 3), 16), parseInt(h.slice(3, 5), 16), parseInt(h.slice(5, 7), 16), a];
const sky = skyAt(new Date().getHours());
const HERO = [[107, 63, 216, .95], [25, 195, 178, .85], [224, 87, 122, .6], [60, 90, 220, .5]];

// Each scene: the light behind the island, the beam, the island's status, and how long its moment plays (seconds).
const SCENES = [
  { key: 'open', world: HERO, beam: 1, status: 'Charging 100%' },
  { key: 'claude', play: 2.4, world: [[107, 63, 216, .8], [242, 179, 90, .38], [224, 87, 122, .42], [60, 90, 220, .3]], beam: .55 },
  { key: 'talk', play: 2.8, world: [[25, 195, 178, .7], [60, 200, 120, .42], [107, 63, 216, .42], [30, 90, 160, .35]], beam: .45 },
  { key: 'music', play: 6, world: [[200, 40, 40, .8], [40, 150, 70, .6], [120, 20, 30, .55], [90, 30, 30, .4]], beam: .25 },
  { key: 'phone', play: 1.8, world: [[90, 120, 255, .62], [107, 63, 216, .62], [25, 195, 178, .38], [40, 60, 160, .4]], beam: .5 },
  { key: 'stage', play: 3.2, world: [[255, 190, 140, .5], [224, 87, 122, .38], [255, 220, 180, .32], [120, 70, 50, .4]], beam: .3 },
  { key: 'alarm', play: 2.6, world: [hex(sky.hor, .72), hex(sky.mid, .85), hex(sky.edge, .5), hex(sky.top, .9)], beam: .2 },
  { key: 'light', world: [[70, 70, 110, .4], [40, 40, 70, .35], [107, 63, 216, .28], [25, 195, 178, .16]], beam: .15, status: 'Using 90 MB' },
  { key: 'get', play: .01, world: HERO, beam: 1 },
];
const LAST = SCENES.length - 1;

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
  const hero = { title: stage.querySelector('.open-copy'), foot: stage.querySelector('.open-foot'), moments: stage.querySelector('.moments') };
  let gl = null, now = -1, user = {}, html = '';
  const speed = reduced ? 0 : 1;

  // Animated values: g = how far the island has left the opening; k = how open the island is; m = the moment.
  const pose = { g: 0 }, open = { k: 0, w: 380, h: 52 }, moment = { m: 0 };
  let sw = 0, sh = 0, scale = 1, mobile = false, stickTop = 0, restW = 380, restH = 52, pinTop = 0;
  function measure() {
    sw = stage.clientWidth; sh = stage.clientHeight; mobile = sw < 760;
    stickTop = parseFloat(getComputedStyle(stage).top) || 0;
    scale = mobile ? 1 : clamp(sh / 800, .78, 1.22);
    restW = mobile ? Math.min(300, sw * .72) : 380; restH = restW / 7.38;
    pinTop = sh * (mobile ? .06 : .09);
    journey.style.height = `${SCENES.length * sh}px`;
    journey.querySelectorAll('[data-at]').forEach((m) => { m.style.top = `${+m.dataset.at * sh}px`; });
  }

  // What the open island holds in each chapter, at moment m (0 to 1). w is its width before scaling.
  const CONTENT = {
    claude(m) {
      const answer = user.claude || (m > .55 ? 'allow' : '');
      const title = answer === 'allow' ? (m > .85 || user.claude ? 'Allowed. 42 tests passed' : 'Allowed. Running npm test') : answer === 'deny' ? 'Denied. Claude will try another way' : 'Wants to run a command';
      return { w: 640, html: `<div class="c-head"><span class="eyes"><i></i><i></i></span><div><div class="cap">Claude Code · login-fix</div><div class="c-title">${title}</div></div><span class="c-meta">${answer ? (answer === 'allow' ? '✓ allowed' : '✗ denied') : `waiting 0:0${Math.floor(m * 9)}`}</span></div>
        <code class="c-code">npm test</code>
        <div class="c-acts"><button type="button" class="pill${answer === 'deny' ? ' on' : ''}" data-act="deny">Deny</button><button type="button" class="pill pri${answer === 'allow' ? ' on' : ''}" data-act="allow">Allow</button></div>` };
    },
    talk(m) {
      const deva = user.script ? user.script === 'deva' : m > .8;
      const n = user.words ?? (deva ? 8 : Math.min(8, Math.floor(m * 11)));
      const words = (deva ? DEVA : ROMAN).slice(0, n).join(' ');
      return { w: 700, html: `<div class="c-head"><span class="rec"></span><span class="cap on">Dictating</span><span class="cap">into WhatsApp</span><span class="wave" aria-hidden="true"><i></i><i></i><i></i><i></i><i></i><i></i><i></i></span></div>
        <div class="c-said${deva ? ' deva' : ''}" aria-live="polite">${words}<span class="caret"></span></div>
        <div class="c-acts"><span class="switch" role="group" aria-label="Script"><button type="button" data-act="roman" aria-pressed="${!deva}">Roman</button><button type="button" class="deva" data-act="deva" aria-pressed="${deva}">देवनागरी</button></span><button type="button" class="pill" data-act="talk">Hold to talk</button></div>` };
    },
    music(m) {
      const at = Math.round(lerp(64, 70, m)), paused = !!user.paused;
      return { w: 660, html: `<div class="c-music"><img src="${cover}" alt="" width="132" height="132"><div class="c-track"><div class="cap">${paused ? 'Paused' : 'Now playing'}</div><div class="c-song">Night Song</div><div class="c-artist">Nusrat Fateh Ali Khan &amp; Michael Brook</div>
        <div class="c-bar"><i style="width:${(at / 367 * 100).toFixed(1)}%"></i></div><div class="c-times"><span>${Math.floor(at / 60)}:${String(at % 60).padStart(2, '0')}</span><button type="button" class="play" data-act="play" aria-label="${paused ? 'Play' : 'Pause'}"><svg viewBox="0 0 24 24" aria-hidden="true" fill="currentColor">${paused ? '<path d="M8 5.5v13l10-6.5z"/>' : '<rect x="7" y="5.5" width="3.5" height="13" rx="1"/><rect x="13.5" y="5.5" width="3.5" height="13" rx="1"/>'}</svg></button><span>6:07</span></div></div></div>` };
    },
    phone(m) {
      const p = Math.min(100, Math.floor(m * 112));
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
      const y = lerp(150, 34, m);
      const body = sky.moon ? `background:radial-gradient(circle at 40% 40%,${sky.core},${sky.edge});box-shadow:0 0 50px rgba(${sky.glow},.5)` : `background:radial-gradient(circle at 45% 40%,${sky.core},${sky.edge});box-shadow:0 0 80px 24px rgba(${sky.glow},.6)`;
      return { w: 640, html: `<div class="c-sky" style="--top:${sky.top};--mid:${sky.mid};--hor:${sky.hor}"><span class="c-body${sky.moon ? ' moon' : ''}" style="top:${y.toFixed(0)}px;${body}"></span><div class="cap">Wake up · ${sky.name}</div><div class="c-alarm">${t}<span>${ap}</span></div><button type="button" class="c-stop" style="background:${sky.stop}" data-act="stop">Stop</button></div>` };
    },
    get() {
      return { w: 600, html: `<div class="c-head"><span class="eyes"><i></i><i></i></span><div><div class="cap">Shammy ${version} · Windows 10 and 11</div><div class="c-title">Free, and about 105 MB</div></div></div>
        <div class="c-acts start"><a class="pill pri big" href="${release}/download/Shammy-Setup.exe">Download Setup.exe</a><a class="pill" href="${release}/download/Shammy-Portable.zip">Portable .zip</a></div>` };
    },
  };

  // Draw the island's content for the current moment, and size it to fit.
  function fill() {
    const content = CONTENT[SCENES[now]?.key];
    if (!content) return;
    const c = content(moment.m);
    if (c.html !== html) { html = c.html; inner.innerHTML = c.html; }
    open.w = Math.min(c.w, (sw - 32) / scale);
    inner.style.width = `${open.w}px`;
    open.h = inner.offsetHeight;
  }

  // Place everything from the animated values. Runs on every tween update.
  function draw() {
    const g = pose.g, k = open.k;
    const heroW = mobile ? sw * .84 : sw * .44;
    const w = lerp(heroW, restW * scale, g), cy = lerp(sh * .5, pinTop + (restH * scale) / 2, g);
    gl?.place(sw / 2, cy, w);
    gl?.show(k < .2);
    fallback.style.transform = `translate(-50%, -50%) translate(0, ${(cy - sh * .5).toFixed(1)}px) scale(${(w / 600).toFixed(3)})`;
    hero.title.hidden = hero.moments.hidden = hero.foot.hidden = g > .97;
    hero.title.style.opacity = String(1 - smooth(g, 0, .5));
    hero.title.style.transform = `translateY(${(-g * 80).toFixed(1)}px)`;
    hero.foot.style.opacity = String(1 - smooth(g, 0, .3));
    hero.moments.style.opacity = String(1 - smooth(g, 0, .45));
    hero.moments.style.setProperty('--spread', String(1 + g * .25));
    card.style.top = `${pinTop}px`;
    card.style.width = `${lerp(restW, open.w, k).toFixed(1)}px`;
    card.style.height = `${lerp(restH, open.h, k).toFixed(1)}px`;
    card.style.borderRadius = `${lerp(restH / 2, 34, k).toFixed(1)}px`;
    card.style.transform = `translateX(-50%) scale(${scale})`;
    card.style.opacity = String(smooth(k, 0, .08));
    card.style.visibility = k > 0 ? 'visible' : 'hidden';
    inner.style.opacity = String(smooth(k, .8, 1));
    inner.style.visibility = k > .8 ? 'visible' : 'hidden';
    card.toggleAttribute('inert', k < .9);
    phone.style.opacity = String(SCENES[now]?.key === 'phone' ? k : 0);
  }

  // Moving to a scene: the island closes, the light changes, the words swap, and the new moment plays by itself.
  let tl = null, playing = null;
  function go(i) {
    if (i === now) return;
    const was = now, s = SCENES[i];
    now = i; user = {}; stopTalk();
    tl?.kill(); playing?.kill();
    stage.dataset.scene = s.key;
    s.world.forEach((c, n) => stage.style.setProperty(`--w${n}`, `rgba(${c.join(',')})`));
    gl?.world(s.world, s.beam); gl?.status(s.status || ''); gl?.tab(i === 0);
    fallbackStatus.textContent = s.status || '';
    if (i === 0) rail.hidden = true;
    else if (rail.hidden) { rail.hidden = false; gsap.fromTo(rail, { opacity: 0, x: 12 }, { opacity: 1, x: 0, duration: .6 * speed, delay: (was <= 0 ? .9 : 0) * speed, ease: 'power2.out' }); }
    phone.hidden = s.key !== 'phone'; phone.classList.remove('got');
    railItems.forEach((b) => b.setAttribute('aria-current', String(+b.dataset.go === i)));

    tl = gsap.timeline({ defaults: { overwrite: 'auto' }, onUpdate: draw });
    tl.timeScale(speed ? 1 : 1000);
    // Close the island first if it is open.
    if (open.k > 0) tl.to(open, { k: 0, duration: was === -1 ? 0 : .26, ease: 'power2.in' });
    if (i === 0) tl.to(pose, { g: 0, duration: .7, ease: 'power3.out' }, 0);
    else tl.to(pose, { g: 1, duration: was <= 0 ? 1.05 : .2, ease: 'power3.inOut' }, 0);
    // The words for this chapter rise in.
    copies.forEach((c) => {
      if (+c.dataset.i !== i) { c.classList.add('away'); return; }
      c.classList.remove('away');
      gsap.fromTo(c.children, { opacity: 0, y: 28 }, { opacity: 1, y: 0, duration: .8 * speed, ease: 'power3.out', stagger: .08 * speed, delay: (was <= 0 ? .55 : .3) * speed });
      gsap.set(c, { opacity: 1, y: 0 });
    });
    if (!CONTENT[s.key]) { html = ''; inner.innerHTML = ''; return; }
    // Open into the new content, then play its moment.
    tl.call(() => { moment.m = 0; html = ''; fill(); });
    tl.to(open, { k: 1, duration: .7, ease: 'expo.out' }, was <= 0 ? '>-0.35' : '>');
    tl.call(() => {
      chime(i - 1);
      playing = gsap.to(moment, { m: 1, duration: (s.play || .01) * speed, ease: 'none', onUpdate: () => { fill(); draw(); } });
      if (!speed) playing.progress(1);
    }, null, '-=0.35');
  }

  // Taps inside the island.
  let talkTimer;
  card.addEventListener('click', (e) => {
    const act = e.target.closest('[data-act]')?.dataset.act;
    if (!act || act === 'talk') return;
    if (act === 'allow' || act === 'deny') { user.claude = act; playing?.progress(1); chime(act === 'allow' ? 4 : 1, .6); }
    else if (act === 'roman' || act === 'deva') { user.script = act; user.words = 8; }
    else if (act === 'play') { user.paused = !user.paused; playing?.paused(user.paused); }
    else if (act === 'send') { phone.classList.remove('got'); playing?.restart(); }
    else if (act === 'stop') { user.stopped = true; chime(0, .6); }
    html = ''; fill(); draw();
  });
  const startTalk = (e) => {
    if (!e.target.closest('[data-act="talk"]') || talkTimer) return;
    e.preventDefault();
    playing?.progress(1);
    user.script = user.script || 'roman'; user.words = 0;
    talkTimer = setInterval(() => { user.words = Math.min(8, user.words + 1); fill(); draw(); }, 260);
  };
  function stopTalk() { clearInterval(talkTimer); talkTimer = 0; if (user.words != null && user.words < 8) { user.words = 8; fill(); draw(); } }
  card.addEventListener('pointerdown', startTalk);
  card.addEventListener('pointerup', stopTalk);
  card.addEventListener('pointerleave', stopTalk);
  card.addEventListener('keydown', (e) => { if ((e.key === 'Enter' || e.key === ' ') && !e.repeat) startTalk(e); });
  card.addEventListener('keyup', (e) => { if (e.key === 'Enter' || e.key === ' ') stopTalk(); });

  // The speaker: island sounds are off until the visitor turns them on.
  const speaker = stage.querySelector('.sound');
  speaker.hidden = false;
  speaker.setAttribute('aria-pressed', String(soundIsOn()));
  speaker.addEventListener('click', () => { setSound(!soundIsOn()); speaker.setAttribute('aria-pressed', String(soundIsOn())); chime(2, .7); });

  // The scroll picks the scene; a short scroll or swipe glides on to the next one.
  measure();
  const trigger = ScrollTrigger.create({
    trigger: journey,
    start: () => `top ${stickTop}px`,
    end: 'bottom bottom',
    snap: reduced ? false : { snapTo: 1 / LAST, directional: true, delay: .04, duration: { min: .45, max: .9 }, ease: 'power2.inOut' },
    onUpdate: (self) => go(Math.round(self.progress * LAST)),
  });
  const goTo = (n) => scrollTo({ top: trigger.start + (n / LAST) * (trigger.end - trigger.start), behavior: reduced ? 'auto' : 'smooth' });
  railItems.forEach((b) => b.addEventListener('click', () => goTo(+b.dataset.go)));
  // Tabbing into a chapter's words (a link) brings that chapter on screen.
  copies.forEach((c) => c.addEventListener('focusin', () => { if (+c.dataset.i !== now) goTo(+c.dataset.i); }));
  addEventListener('resize', () => { measure(); fill(); draw(); });
  ScrollTrigger.addEventListener('refreshInit', measure);
  go(Math.round(trigger.progress * LAST));
  draw();

  return {
    attach(island) {
      gl = island;
      const s = SCENES[now];
      gl.world(s.world, s.beam); gl.status(s.status || ''); gl.tab(now === 0);
      stage.classList.add('gl-on');
      draw();
    },
  };
}
