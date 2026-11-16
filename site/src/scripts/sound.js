// The island's sounds: a soft two-note chime drawn with Web Audio, so there is nothing to download.
// Off until the visitor turns it on; the choice is remembered in this browser only.
let ctx = null;
let on = false;
try { on = localStorage.getItem('shammy-sound') === 'on'; } catch { /* storage blocked: stay quiet */ }

export const soundIsOn = () => on;

export function setSound(next) {
  on = next;
  try { localStorage.setItem('shammy-sound', on ? 'on' : 'off'); } catch { /* not remembered, still works */ }
  if (on && !ctx) ctx = new AudioContext();
  if (on) ctx.resume();
}

// pitch: 0 to 5 steps up a pentatonic scale, so each chapter rings a little differently.
export function chime(pitch = 0, loud = 1) {
  if (!on || !ctx) return;
  const scale = [523.25, 587.33, 659.25, 783.99, 880, 1046.5];
  const f = scale[pitch % scale.length], t = ctx.currentTime;
  [[f, 0, .07], [f * 1.5, .09, .045]].forEach(([freq, at, gain]) => {
    const o = ctx.createOscillator(), g = ctx.createGain();
    o.type = 'sine'; o.frequency.value = freq;
    g.gain.setValueAtTime(0, t + at);
    g.gain.linearRampToValueAtTime(gain * loud, t + at + .012);
    g.gain.exponentialRampToValueAtTime(.0001, t + at + 1.1);
    o.connect(g).connect(ctx.destination);
    o.start(t + at); o.stop(t + at + 1.2);
  });
}
