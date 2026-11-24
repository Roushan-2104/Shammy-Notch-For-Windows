// The story under the opening. One stage stays on screen while you scroll: the island glides up from the opening,
// pins near the top, and opens into each chapter as Shammy would, while the light behind it takes that chapter's
// colours. Claude's two chapters leave the island closed and bring in the wing beside it, as the app does. A scroll or swipe glides to the next chapter, and that chapter's moment then plays by itself on a GSAP
// timeline; the buttons inside the island work too.
import { gsap } from 'gsap';
import { ScrollTrigger } from 'gsap/ScrollTrigger';
import { skyAt, fmtHour, ROMAN, DEVA } from './shared.js';
import { chime, setSound, soundIsOn } from './sound.js';

gsap.registerPlugin(ScrollTrigger);

const hex = (h, a) => [parseInt(h.slice(1, 3), 16), parseInt(h.slice(3, 5), 16), parseInt(h.slice(5, 7), 16), a];
const sky = skyAt(new Date().getHours());
const HERO = [[107, 63, 216, .95], [25, 195, 178, .85], [224, 87, 122, .6], [60, 90, 220, .5]];

// Each scene: its colour field (chapters only; the opening and the close keep the dark light), the light behind the island, the beam, the island's status, and how long its moment plays (seconds).
const SCENES = [
  { key: 'open', world: HERO, beam: 1, status: 'Charging 100%' },
  { key: 'steps', field: '#D97757', play: 5.5, wing: true, world: [[217, 119, 87, .55], [107, 63, 216, .72], [60, 90, 220, .35], [25, 195, 178, .25]], beam: .5 },
  { key: 'ask', field: '#F0C4A8', play: 5, wing: true, world: [[217, 119, 87, .7], [242, 179, 90, .35], [107, 63, 216, .55], [224, 87, 122, .3]], beam: .45 },
  { key: 'talk', field: '#F2B233', play: 2.8, world: [[25, 195, 178, .7], [60, 200, 120, .42], [107, 63, 216, .42], [30, 90, 160, .35]], beam: .45 },
  { key: 'music', field: '#C7372F', play: 6, world: [[200, 40, 40, .8], [40, 150, 70, .6], [120, 20, 30, .55], [90, 30, 30, .4]], beam: .25 },
  { key: 'phone', field: '#9FD8C2', play: 8, world: [[90, 120, 255, .62], [107, 63, 216, .62], [25, 195, 178, .38], [40, 60, 160, .4]], beam: .5 },
  { key: 'stage', field: '#FFD2AE', play: 3.2, world: [[255, 190, 140, .5], [224, 87, 122, .38], [255, 220, 180, .32], [120, 70, 50, .4]], beam: .3 },
  { key: 'alarm', field: sky.moon ? sky.hor : sky.mid, play: 2.6, world: [hex(sky.hor, .72), hex(sky.mid, .85), hex(sky.edge, .5), hex(sky.top, .9)], beam: .2 },
  { key: 'light', field: '#ECE9E2', world: [[70, 70, 110, .4], [40, 40, 70, .35], [107, 63, 216, .28], [25, 195, 178, .16]], beam: .15, status: 'Using 90 MB' },
  { key: 'get', play: .01, world: HERO, beam: 1 },
];
const LAST = SCENES.length - 1;

const clamp = (v, a = 0, b = 1) => Math.min(b, Math.max(a, v));
const smooth = (v, a, b) => { const x = clamp((v - a) / (b - a)); return x * x * (3 - 2 * x); };
const lerp = (a, b, k) => a + (b - a) * k;
// The ink that reads best on a field: near-black or near-white, whichever has more contrast.
const lum = (h) => [1, 3, 5].map((i) => parseInt(h.slice(i, i + 2), 16) / 255).map((c) => (c <= .04045 ? c / 12.92 : ((c + .055) / 1.055) ** 2.4)).reduce((a, c, i) => a + c * [.2126, .7152, .0722][i], 0);
const inkOn = (h) => ((lum(h) + .05) / .055 >= 1.05 / (lum(h) + .05) ? '#0B0A0E' : '#F4F3F0');
const esc = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;');

// Claude's wing: what a session is doing, step by step, and the questions it asks. Drawn here, not Claude's own mark.
const SPARK = '<svg class="spark" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" aria-hidden="true"><path d="M12 3v18M3 12h18M5.6 5.6l12.8 12.8M18.4 5.6 5.6 18.4"/></svg>';
const SWAP = '<button type="button" class="w-swap" data-wact="swap" aria-label="Move the wing to the other side of the island"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M7 7h12l-3-3M17 17H5l3 3"/></svg></button>';
const TASKS = ['Find why the login test fails', 'Fix the token refresh', 'Run the tests'];
// When each step starts (0 to 1 through the chapter), what it says, and where each task stands then (0 next, 1 underway, 2 done).
const STEPS = [
  [0, 'Reading src/auth/session.ts', [1, 0, 0]],
  [.24, 'Editing session.ts +6 −2', [2, 1, 0]],
  [.52, 'Running npm test', [2, 2, 1]],
  [.84, '42 tests passed', [2, 2, 2]],
];
const CHOICES = ['Refresh the token before each request', 'Retry once after a 401'];

export function mountStory(journey, { reduced, cover, release, version }) {
  const stage = journey.querySelector('.stage');
  const card = stage.querySelector('.isle');
  const inner = card.querySelector('.isle-in');
  const copies = [...stage.querySelectorAll('.copy')];
  const rail = stage.querySelector('.rail');
  const railItems = [...rail.querySelectorAll('button')];
  const phone = stage.querySelector('.phone');
  const phoneLive = phone.querySelector('[data-ph-live]');
  const wingEl = stage.querySelector('.wing');
  const wingIn = wingEl.querySelector('.w-in');
  const fallback = stage.querySelector('.css-island');
  const fallbackStatus = fallback.querySelector('.ci-status');
  const fieldEl = stage.querySelector('.field'), fieldTop = fieldEl.firstElementChild;
  const hero = { title: stage.querySelector('.open-copy'), foot: stage.querySelector('.open-foot'), moments: stage.querySelector('.moments') };
  let shownField = null, fieldTl = null;
  let gl = null, now = -1, user = {}, html = '';
  const speed = reduced ? 0 : 1;

  // Animated values: g = how far the island has left the opening; k = how open the island is; m = the moment.
  const pose = { g: 0 }, open = { k: 0, w: 380, h: 52 }, moment = { m: 0 }, wing = { k: 0 };
  // Where the wing sits: beside the island on the left or right, or wherever the visitor dropped it.
  let dock = 'left', free = null, wingHtml = '';
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
  // What the wing holds in each Claude chapter, at moment m (0 to 1).
  const WING = {
    steps(m) {
      let at = 0;
      STEPS.forEach((s, n) => { if (m >= s[0]) at = n; });
      const [, said, state] = STEPS[at], done = state.every((t) => t === 2);
      const prev = at > 0 ? `<li class="old"><span class="w-tag">login-fix</span>${esc(STEPS[at - 1][1])}</li>` : '';
      return `<div class="w-top" data-drag>${SPARK.replace('spark', done ? 'spark' : 'spark spin')}<b>Claude Code</b><span class="w-meta">${done ? '✓ done' : '2 sessions'}</span>${SWAP}</div>
        <ol class="thread">${TASKS.map((t, n) => `<li class="${['', 'now', 'done'][state[n]]}">${t}</li>`).join('')}</ol>
        <ul class="w-log" aria-live="polite">${prev}<li><span class="w-tag">login-fix</span>${esc(said)}</li><li><span class="w-tag t2">docs</span>Writing README.md</li></ul>`;
    },
    ask(m) {
      // With reduced motion the question waits for the visitor instead of answering itself.
      // Nor while a keyboard user is inside the wing, so they have time to pick.
      const answer = user.answer ?? (speed && m > .5 && !user.typing && !wingEl.contains(document.activeElement) ? CHOICES[0] : null);
      const top = `<div class="w-top" data-drag>${SPARK}<b>Claude asks</b><span class="w-tag">login-fix</span><span class="w-meta">1 of 1</span>${SWAP}</div>`;
      if (answer) return `${top}<p class="w-done"><span class="ok">✓ Sent:</span> “${esc(answer)}”. Claude carries on.</p><button type="button" class="w-again" data-wact="again">Ask me again</button>`;
      return `${top}<p class="w-q">The token runs out mid-request. How should I fix it?</p>
        <ol class="w-choices">${CHOICES.map((c, n) => `<li><button type="button" data-wact="pick" data-n="${n}"><i>${n + 1}</i>${c}</button></li>`).join('')}<li class="own"><i>3</i><input data-own maxlength="70" placeholder="Type your own answer" aria-label="Type your own answer, then press Enter" enterkeyhint="send"></li></ol>`;
    },
  };

  // The phone in the asking chapter: Claude's next command waits for Allow there too.
  function phoneFill() {
    if (SCENES[now]?.key !== 'ask') return;
    const answered = user.answer != null || (speed && moment.m > .5 && !user.typing && !wingEl.contains(document.activeElement));
    const asked = answered && moment.m > .64, allowed = moment.m > .9;
    phone.classList.toggle('got', asked);
    const h = allowed ? '<b>Claude Code · login-fix</b><span>Allowed from your phone. Running npm test.</span>'
      : '<b>Claude Code · login-fix</b><span>Run npm test?</span><span class="ph-acts"><i>Deny</i><i class="on">Allow</i></span>';
    if (phoneLive.innerHTML !== h) phoneLive.innerHTML = h;
  }

  function wingFill() {
    const key = SCENES[now]?.key;
    if (!WING[key]) return;
    phoneFill();
    // Leave the wing alone while the visitor is typing an answer in it.
    const focus = wingEl.contains(document.activeElement) ? document.activeElement : null;
    if (key === 'ask' && focus?.matches('[data-own]')) return;
    const h = WING[key](moment.m);
    if (h === wingHtml) return;
    wingHtml = h; wingIn.innerHTML = h;
    // A keyboard user keeps their place: focus returns to the same control after a redraw, or to the wing's first one.
    if (focus) (wingIn.querySelector(focus.dataset.wact ? `[data-wact="${focus.dataset.wact}"]${focus.dataset.n ? `[data-n="${focus.dataset.n}"]` : ''}` : 'x') || ['.w-again', '[data-wact="pick"]', '[data-own]', 'button'].map((q) => wingIn.querySelector(q)).find(Boolean))?.focus();
  }

  // Place the wing beside the pinned island, or where it was dropped; on narrow screens it sits under the island.
  function placeWing() {
    const ww = wingEl.offsetWidth * scale, wh = wingEl.offsetHeight * scale, iw = restW * scale, gap = 14 * scale;
    let x, y = pinTop, under = false;
    const below = () => { x = (sw - ww) / 2; y = pinTop + restH * scale + 12; under = true; };
    if (dock === 'free' && free) { x = clamp(free.x, 8, Math.max(8, sw - ww - 8)); y = clamp(free.y, 8, Math.max(8, sh - wh - 8)); }
    else if (mobile) below();
    else if (dock === 'right') { x = sw / 2 + iw / 2 + gap; if (x + ww > sw - 8) below(); }
    else { x = sw / 2 - iw / 2 - gap - ww; if (x < 8) below(); }
    // Under the island there is no side to swap to.
    wingEl.classList.toggle('under', under);
    wingEl.style.transform = `translate(${x.toFixed(1)}px, ${(y - (1 - wing.k) * 14).toFixed(1)}px) scale(${scale})`;
  }

  const CONTENT = {
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
    // Four beats of the laptop and phone keeping in step; the dot on the line shows which way each one travels.
    phone(m) {
      const n = user.beat ?? Math.min(3, Math.floor(m * 4)), t = user.beat != null ? 1 : clamp(m * 4 - n);
      const into = n === 0 || n === 2; // from the phone into the laptop
      const landed = into ? t > .08 : t > .4;
      const answered = n === 2 && t > .6, p = Math.floor(clamp((t - .1) / .5) * 100);
      const beat = [
        { cap: 'From your phone · WhatsApp', title: 'Riya: kal milte hain?', meta: 'just now',
          ph: '<b>WhatsApp · Riya</b><span>kal milte hain?</span>', there: 'Riya on WhatsApp' },
        { cap: 'Copied on your laptop', title: '6E 2134', meta: 'on your phone too',
          ph: '<b>Clipboard</b><span>6E 2134, copied on your laptop</span>', there: '6E 2134 is on the clipboard' },
        { cap: 'Your phone is ringing', title: answered ? 'Mom · answered' : 'Mom', meta: answered ? 'the call stays on your phone' : 'ringing',
          ph: answered ? '<b>Mom</b><span>On a call, answered from your laptop</span>' : '<b>Mom</b><span>Incoming call</span><span class="ph-acts"><i>Decline</i><i class="on">Answer</i></span>',
          there: answered ? 'on a call with Mom' : 'Mom is calling' },
        { cap: 'To your phone', title: p < 100 ? 'Sending report.pdf' : 'On your phone', meta: `${p}%`,
          ph: '<b>report.pdf</b><span>From your laptop · tap to open</span>', there: p < 100 ? 'report.pdf on its way' : 'report.pdf · tap to open' },
      ][n];
      phone.classList.toggle('got', landed);
      if (phoneLive.innerHTML !== beat.ph) phoneLive.innerHTML = beat.ph;
      // The dot runs along the line from where the beat starts, then rests where it landed.
      const pos = into ? lerp(100, 0, clamp(t / .3)) : lerp(0, 100, clamp(t / .4));
      const shown = into ? smooth(t, .05, .3) : 1;
      return { w: 620, html: `<div class="c-head"><span class="eyes"><i></i><i></i></span><div><div class="cap">${beat.cap}</div><div class="c-title" style="opacity:${shown.toFixed(2)}">${beat.title}</div></div><span class="c-meta${n === 3 ? ' big' : ''}">${beat.meta}</span></div>
        ${n === 2 ? `<div class="c-acts"><span class="pill static">Decline</span><span class="pill pri static${answered ? ' on' : ''}">Answer</span></div>` : ''}
        ${n === 3 ? `<div class="c-progress"><i style="width:${p}%"></i></div>` : ''}
        <div class="c-link"><span>Laptop</span><span class="c-wire"><i class="${landed ? 'lit' : ''}" style="left:${pos.toFixed(1)}%"></i></span><span>Phone</span></div>
        <div class="c-ph"><span class="c-ph-tag">On your phone</span><span>${beat.there}</span></div>
        <div class="c-acts start"><span class="switch" role="group" aria-label="What stays in sync">${['Messages', 'Clipboard', 'Calls', 'Files'].map((l, i) => `<button type="button" data-act="beat" data-n="${i}" aria-pressed="${i === n}">${l}</button>`).join('')}</span></div>` };
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
    const key = SCENES[now]?.key;
    phone.style.opacity = String(key === 'phone' ? k : key === 'ask' ? wing.k : 0);
    wingEl.style.opacity = String(smooth(wing.k, 0, .6));
    wingEl.style.visibility = wing.k > 0 ? 'visible' : 'hidden';
    wingEl.toggleAttribute('inert', wing.k < .9);
    if (wing.k > 0) placeWing();
  }

  // A chapter's colour wipes over the last one: up from the bottom going on, down from the top going back.
  // Leaving the chapters, the colour slides off and the dark light of the opening shows again.
  function wipe(col, up) {
    const from = shownField; shownField = col;
    fieldTl?.kill();
    fieldEl.style.background = from && col ? from : 'transparent';
    if (col) {
      stage.classList.add('fielded'); stage.style.setProperty('--field-ink', inkOn(col));
      fieldTop.style.background = col;
      fieldTl = gsap.fromTo(fieldTop, { clipPath: up ? 'inset(100% 0% 0% 0%)' : 'inset(0% 0% 100% 0%)' }, { clipPath: 'inset(0% 0% 0% 0%)', duration: .8 * speed, ease: 'power3.inOut', onComplete: () => { fieldEl.style.background = col; } });
    } else {
      stage.style.removeProperty('--field-ink');
      fieldTop.style.background = from;
      fieldTl = gsap.fromTo(fieldTop, { clipPath: 'inset(0% 0% 0% 0%)' }, { clipPath: up ? 'inset(0% 0% 100% 0%)' : 'inset(100% 0% 0% 0%)', duration: .8 * speed, ease: 'power3.inOut', onComplete: () => stage.classList.remove('fielded') });
    }
  }

  // Moving to a scene: the island closes, the colour and the light change, the words swap, and the new moment plays by itself.
  let tl = null, playing = null;
  function go(i) {
    if (i === now) return;
    const was = now, s = SCENES[i];
    now = i; user = {}; stopTalk();
    // Focus left inside the wing would freeze it on the last chapter's content.
    if (wingEl.contains(document.activeElement)) document.activeElement.blur();
    tl?.kill(); playing?.kill();
    stage.dataset.scene = s.key;
    if ((s.field || null) !== shownField) wipe(s.field || null, i > was);
    s.world.forEach((c, n) => stage.style.setProperty(`--w${n}`, `rgba(${c.join(',')})`));
    gl?.world(s.world, s.beam); gl?.status(s.status || ''); gl?.tab(i === 0);
    fallbackStatus.textContent = s.status || '';
    if (i === 0) rail.hidden = true;
    else if (rail.hidden) { rail.hidden = false; gsap.fromTo(rail, { opacity: 0, x: 12 }, { opacity: 1, x: 0, duration: .6 * speed, delay: (was <= 0 ? .9 : 0) * speed, ease: 'power2.out' }); }
    phone.hidden = s.key !== 'phone' && s.key !== 'ask'; phone.classList.remove('got'); phone.dataset.for = s.key;
    railItems.forEach((b) => b.setAttribute('aria-current', String(+b.dataset.go === i)));

    tl = gsap.timeline({ defaults: { overwrite: 'auto' }, onUpdate: draw });
    tl.timeScale(speed ? 1 : 1000);
    // Close the island first if it is open.
    if (open.k > 0) tl.to(open, { k: 0, duration: was === -1 ? 0 : .26, ease: 'power2.in' });
    // The wing folds away when leaving Claude's chapters; between them it stays and changes what it shows.
    if (!s.wing && wing.k > 0) tl.to(wing, { k: 0, duration: was === -1 ? 0 : .26, ease: 'power2.in' }, 0);
    if (i === 0) tl.to(pose, { g: 0, duration: .7, ease: 'power3.out' }, 0);
    else tl.to(pose, { g: 1, duration: was <= 0 ? 1.05 : .2, ease: 'power3.inOut' }, 0);
    // The words for this chapter rise in.
    copies.forEach((c) => {
      if (+c.dataset.i !== i) { c.classList.add('away'); return; }
      c.classList.remove('away');
      gsap.fromTo(c.children, { opacity: 0, y: 28 }, { opacity: 1, y: 0, duration: .8 * speed, ease: 'power3.out', stagger: .08 * speed, delay: (was <= 0 ? .55 : .3) * speed });
      gsap.set(c, { opacity: 1, y: 0 });
    });
    if (s.wing) {
      html = ''; inner.innerHTML = '';
      tl.call(() => { moment.m = 0; wingHtml = ''; wingFill(); });
      if (wing.k < 1) tl.to(wing, { k: 1, duration: .6, ease: 'expo.out' }, was <= 0 ? '>-0.3' : '>');
      tl.call(() => { if (s.key === 'ask') chime(1); playMoment(0, s.play); });
      return;
    }
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

  // Play a Claude chapter's moment from `from` to the end.
  function playMoment(from, seconds) {
    playing?.kill();
    playing = gsap.fromTo(moment, { m: from }, { m: 1, duration: seconds * speed, ease: 'none', onUpdate: () => { wingFill(); draw(); } });
    if (!speed) playing.progress(1);
  }

  // Taps and typing inside the wing.
  const answer = (text) => {
    const had = wingEl.contains(document.activeElement);
    if (had) document.activeElement.blur();
    user.answer = text; user.typing = false; wingHtml = '';
    playMoment(Math.min(moment.m, .52), 2.4); wingFill();
    if (had) wingIn.querySelector('.w-again')?.focus();
    chime(4, .6);
  };
  wingEl.addEventListener('click', (e) => {
    const b = e.target.closest('[data-wact]');
    if (!b) return;
    const act = b.dataset.wact;
    if (act === 'pick') answer(CHOICES[+b.dataset.n]);
    else if (act === 'again') { user = {}; wingHtml = ''; playMoment(0, 3); wingFill(); }
    else if (act === 'swap') { dock = dock === 'right' ? 'left' : 'right'; settle(); }
  });
  wingEl.addEventListener('focusin', (e) => { if (e.target.matches('[data-own]')) user.typing = true; });
  wingEl.addEventListener('keydown', (e) => {
    // Enter that confirms a Hindi or Hinglish input method's word is not a send.
    if (e.key !== 'Enter' || e.isComposing || e.keyCode === 229 || !e.target.matches('[data-own]')) return;
    const text = e.target.value.trim();
    if (text) answer(text);
  });
  wingEl.addEventListener('focusout', (e) => { if (!e.target.matches('[data-own]')) return; if (!e.target.value.trim()) user.typing = false; setTimeout(wingFill); });

  // Drag the wing by its top line: near either side of the island it snaps there, anywhere else it stays put.
  let drag = null;
  function settle() { wingEl.classList.add('settle'); draw(); setTimeout(() => wingEl.classList.remove('settle'), 400); }
  wingEl.addEventListener('pointerdown', (e) => {
    if (!e.target.closest('[data-drag]') || e.target.closest('button')) return;
    const r = wingEl.getBoundingClientRect();
    drag = { dx: e.clientX - r.left, dy: e.clientY - r.top };
    wingEl.setPointerCapture(e.pointerId);
    wingEl.classList.add('dragging');
    e.preventDefault();
  });
  wingEl.addEventListener('pointermove', (e) => {
    if (!drag) return;
    const sr = stage.getBoundingClientRect();
    free = { x: e.clientX - sr.left - drag.dx, y: e.clientY - sr.top - drag.dy };
    dock = 'free';
    draw();
  });
  const drop = () => {
    if (!drag) return;
    drag = null;
    wingEl.classList.remove('dragging');
    if (free && !mobile) {
      const ww = wingEl.offsetWidth * scale, iw = restW * scale, left = sw / 2 - iw / 2, right = sw / 2 + iw / 2;
      if (Math.abs(free.y - pinTop) < 110) {
        if (free.x + ww > left - 160 && free.x + ww < left + 80) dock = 'left';
        else if (free.x > right - 80 && free.x < right + 160) dock = 'right';
      }
    }
    settle();
  };
  wingEl.addEventListener('pointerup', drop);
  wingEl.addEventListener('pointercancel', drop);
  wingEl.addEventListener('dblclick', (e) => { if (e.target.closest('[data-drag]') && !e.target.closest('button')) { dock = 'left'; settle(); } });

  // Taps inside the island.
  let talkTimer;
  card.addEventListener('click', (e) => {
    const act = e.target.closest('[data-act]')?.dataset.act;
    if (!act || act === 'talk') return;
    if (act === 'roman' || act === 'deva') { user.script = act; user.words = 8; }
    else if (act === 'play') { user.paused = !user.paused; playing?.paused(user.paused); }
    else if (act === 'beat') {
      // Replay that beat and carry on from there; with reduced motion, just show it.
      const n = +e.target.closest('[data-act]').dataset.n;
      if (speed && playing) { delete user.beat; playing.progress(n / 4).play(); } else user.beat = n;
    }
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
