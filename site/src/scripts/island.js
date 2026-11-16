// The island, drawn as Shammy draws it today: a slim pill of frosted smoked glass with aurora light drifting
// inside it, a rim of light along the bottom, the pet's eyes, a status and the time. It turns with the pointer.
// The story places it (centre and width in stage pixels), sets its status and recolours the light behind it.
import * as THREE from 'three';
import { RoomEnvironment } from 'three/examples/jsm/environments/RoomEnvironment.js';

const W = 6.2, H = 0.84, R = H / 2;
const FW = 1240, FH = 168;
const AW = 512, AH = 256;
const BLOBS = [[115, 85, 150], [400, 60, 140], [310, 235, 150], [235, 145, 100]];

function pillPath(g, x, y, w, h) {
  const r = h / 2;
  g.beginPath(); g.moveTo(x + r, y); g.lineTo(x + w - r, y);
  g.arc(x + w - r, y + r, r, -Math.PI / 2, Math.PI / 2); g.lineTo(x + r, y + h);
  g.arc(x + r, y + r, r, Math.PI / 2, Math.PI * 1.5); g.closePath();
}

function prismTexture() {
  const c = document.createElement('canvas'); c.width = 1024; c.height = 64;
  const g = c.getContext('2d'); const gr = g.createLinearGradient(0, 0, 1024, 0);
  [['rgba(0,0,0,0)', 0], ['rgba(255,90,90,.55)', .22], ['rgba(255,180,90,.7)', .34], ['rgba(250,240,140,.65)', .44], ['rgba(120,255,180,.65)', .54], ['rgba(90,170,255,.7)', .66], ['rgba(170,110,255,.6)', .78], ['rgba(0,0,0,0)', 1]]
    .forEach(([col, s]) => gr.addColorStop(s, col));
  g.fillStyle = gr; g.fillRect(0, 0, 1024, 64);
  const v = g.createLinearGradient(0, 0, 0, 64); v.addColorStop(0, '#000'); v.addColorStop(.5, 'rgba(0,0,0,0)'); v.addColorStop(1, '#000');
  g.globalCompositeOperation = 'destination-out'; g.fillStyle = v; g.fillRect(0, 0, 1024, 64);
  const t = new THREE.CanvasTexture(c); t.colorSpace = THREE.SRGBColorSpace; return t;
}

// world: four [r, g, b, a] lights, the same ones the page's CSS uses when there is no WebGL.
export function mountIsland(stage, canvas, world) {
  const renderer = new THREE.WebGLRenderer({ canvas, antialias: true, powerPreference: 'high-performance' });
  renderer.setPixelRatio(Math.min(devicePixelRatio, 1.75));
  renderer.toneMapping = THREE.ACESFilmicToneMapping;
  renderer.outputColorSpace = THREE.SRGBColorSpace;

  const scene = new THREE.Scene();
  scene.background = new THREE.Color('#05050A');
  const camera = new THREE.PerspectiveCamera(32, 1, 0.1, 100);
  camera.position.set(0, 0, 12);
  scene.environment = new THREE.PMREMGenerator(renderer).fromScene(new RoomEnvironment(), 0.04).texture;

  // The light behind the glass, redrawn while it moves between one chapter's colours and the next.
  const auroraCanvas = document.createElement('canvas'); auroraCanvas.width = AW; auroraCanvas.height = AH;
  const auroraTex = new THREE.CanvasTexture(auroraCanvas); auroraTex.colorSpace = THREE.SRGBColorSpace;
  let lights = world.map((c) => c.slice()), want = lights;
  function drawAurora() {
    const g = auroraCanvas.getContext('2d');
    g.fillStyle = '#05050A'; g.fillRect(0, 0, AW, AH);
    BLOBS.forEach(([x, y, r], i) => {
      const [cr, cg, cb, ca] = lights[i], gr = g.createRadialGradient(x, y, 0, x, y, r);
      gr.addColorStop(0, `rgba(${cr | 0},${cg | 0},${cb | 0},${ca})`); gr.addColorStop(1, 'rgba(0,0,0,0)');
      g.fillStyle = gr; g.fillRect(0, 0, AW, AH);
    });
    auroraTex.needsUpdate = true;
  }
  drawAurora();
  const aurora = new THREE.Mesh(new THREE.PlaneGeometry(40, 20), new THREE.MeshBasicMaterial({ map: auroraTex }));
  aurora.position.z = -8;
  scene.add(aurora);
  const prismMat = new THREE.MeshBasicMaterial({ map: prismTexture(), transparent: true, blending: THREE.AdditiveBlending, depthWrite: false });
  const prism = new THREE.Mesh(new THREE.PlaneGeometry(22, 1.8), prismMat);
  prism.position.set(0, -0.2, -3); prism.rotation.z = -0.23;
  scene.add(prism);

  const island = new THREE.Group();
  scene.add(island);
  const outline = new THREE.Shape();
  outline.moveTo(-W / 2 + R, -H / 2); outline.lineTo(W / 2 - R, -H / 2);
  outline.absarc(W / 2 - R, 0, R, -Math.PI / 2, Math.PI / 2, false);
  outline.lineTo(-W / 2 + R, H / 2);
  outline.absarc(-W / 2 + R, 0, R, Math.PI / 2, Math.PI * 1.5, false);
  const body = new THREE.ExtrudeGeometry(outline, { depth: 0.22, bevelEnabled: true, bevelThickness: 0.07, bevelSize: 0.05, bevelSegments: 8, curveSegments: 48 });
  body.translate(0, 0, -0.11);
  island.add(new THREE.Mesh(body, new THREE.MeshPhysicalMaterial({
    color: '#ffffff', roughness: 0.34, transmission: 1, thickness: 0.6, ior: 1.4,
    attenuationColor: new THREE.Color('#0d0c14'), attenuationDistance: 0.35,
    clearcoat: 0.35, clearcoatRoughness: 0.3, envMapIntensity: 0.55, specularIntensity: 0.6,
  })));

  const faceCanvas = document.createElement('canvas'); faceCanvas.width = FW; faceCanvas.height = FH;
  const faceTex = new THREE.CanvasTexture(faceCanvas); faceTex.colorSpace = THREE.SRGBColorSpace;
  let blinking = false, status = '';
  function drawFace(t) {
    const g = faceCanvas.getContext('2d');
    g.clearRect(0, 0, FW, FH);
    g.save(); pillPath(g, 0, 0, FW, FH); g.clip();
    g.fillStyle = 'rgba(8,8,12,0.55)'; g.fillRect(0, 0, FW, FH);
    const k = t / 1000;
    const wave = (cx, cy, r, col) => { const gr = g.createRadialGradient(cx, cy, 0, cx, cy, r); gr.addColorStop(0, col); gr.addColorStop(1, 'rgba(0,0,0,0)'); g.fillStyle = gr; g.fillRect(0, 0, FW, FH); };
    wave(330 + Math.sin(k * 0.35) * 160, 110 + Math.sin(k * 0.5) * 22, 240, 'rgba(110,70,220,0.34)');
    wave(760 + Math.cos(k * 0.28) * 180, 64 + Math.cos(k * 0.42) * 22, 220, 'rgba(25,195,178,0.26)');
    wave(980 + Math.sin(k * 0.22 + 1) * 120, 140, 180, 'rgba(70,110,240,0.22)');
    const rim = g.createLinearGradient(0, 0, FW, 0);
    rim.addColorStop(0, 'rgba(120,220,200,0)'); rim.addColorStop(.3, 'rgba(120,220,200,.85)'); rim.addColorStop(.7, 'rgba(160,140,255,.85)'); rim.addColorStop(1, 'rgba(160,140,255,0)');
    g.fillStyle = rim; g.fillRect(90, FH - 5, FW - 180, 3);
    g.restore();
    g.fillStyle = '#F4F3F0';
    const eh = blinking ? 10 : 68;
    [[96, 84], [162, 84]].forEach(([x, y]) => { g.beginPath(); g.roundRect(x - 23, y - eh / 2, 46, eh, 23); g.fill(); });
    const [time, ap = ''] = new Date().toLocaleTimeString('en-US', { hour: 'numeric', minute: '2-digit' }).split(' ');
    g.textBaseline = 'middle'; g.textAlign = 'left';
    g.font = '700 58px "Schibsted Grotesk", "Segoe UI", sans-serif'; const tw = g.measureText(time).width;
    g.font = '600 30px "Schibsted Grotesk", "Segoe UI", sans-serif'; const aw = g.measureText(ap).width;
    const right = FW - 96, timeX = right - aw - 10 - tw;
    g.fillStyle = '#F4F3F0'; g.font = '700 58px "Schibsted Grotesk", "Segoe UI", sans-serif'; g.fillText(time, timeX, 88);
    g.fillStyle = '#A9A7A2'; g.font = '600 30px "Schibsted Grotesk", "Segoe UI", sans-serif'; g.fillText(ap, right - aw, 97);
    if (status) {
      g.fillStyle = 'rgba(255,255,255,0.18)'; g.fillRect(timeX - 30, 64, 2, 42);
      g.font = '600 44px "Schibsted Grotesk", "Segoe UI", sans-serif';
      const sw = g.measureText(status).width, sx = timeX - 60 - sw;
      g.fillStyle = '#F4F3F0'; g.fillText(status, sx, 88);
      g.fillStyle = '#3BD16F'; g.beginPath(); g.arc(sx - 26, 86, 10, 0, Math.PI * 2); g.fill();
    }
    faceTex.needsUpdate = true;
  }
  const face = new THREE.Mesh(new THREE.PlaneGeometry(W, H), new THREE.MeshBasicMaterial({ map: faceTex, transparent: true, depthWrite: false }));
  face.position.z = 0.19; face.renderOrder = 2;
  island.add(face);

  const tabCanvas = document.createElement('canvas'); tabCanvas.width = 192; tabCanvas.height = 72;
  { const g = tabCanvas.getContext('2d'); g.fillStyle = '#0A0A0E'; pillPath(g, 0, 0, 192, 72); g.fill();
    g.strokeStyle = '#E8E6E1'; g.lineWidth = 7; g.lineCap = 'round'; g.lineJoin = 'round'; g.beginPath(); g.moveTo(76, 44); g.lineTo(96, 26); g.lineTo(116, 44); g.stroke(); }
  const tabTex = new THREE.CanvasTexture(tabCanvas); tabTex.colorSpace = THREE.SRGBColorSpace;
  const tabMat = new THREE.MeshBasicMaterial({ map: tabTex, transparent: true });
  const tab = new THREE.Mesh(new THREE.PlaneGeometry(0.62, 0.23), tabMat);
  tab.position.set(0, -H / 2 - 0.24, 0.05);
  island.add(tab);
  document.fonts.ready.then(() => drawFace(performance.now()));

  let mx = 0, my = 0, tx = 0, ty = 0;
  stage.addEventListener('pointermove', (e) => {
    const r = stage.getBoundingClientRect();
    tx = (e.clientX - r.left) / r.width - 0.5;
    ty = (e.clientY - r.top) / r.height - 0.5;
  });

  // Where the story wants the island: centre (cx, cy) and width, in stage pixels; beam 0 to 1.
  let place = { cx: 0, cy: 0, w: 0 }, beam = 1, tabShown = 1, sw = 1, sh = 1, visW = 1, visH = 1;
  function apply() {
    if (!place.w) return;
    island.scale.setScalar((place.w / sw) * visW / W);
    island.position.x = (place.cx / sw - 0.5) * visW;
    island.position.y = -(place.cy / sh - 0.5) * visH;
  }
  function resize() {
    sw = stage.clientWidth; sh = stage.clientHeight;
    renderer.setSize(sw, sh, false);
    camera.aspect = sw / sh;
    camera.updateProjectionMatrix();
    visH = 2 * Math.tan(THREE.MathUtils.degToRad(camera.fov / 2)) * camera.position.z;
    visW = visH * camera.aspect;
    apply();
  }
  addEventListener('resize', resize);
  resize();

  // Draw only while the stage is on screen and the tab is visible.
  let visible = true, last = 0, nextBlink = 4200, minute = -1, running = false;
  const kick = () => { if (!running && visible && !document.hidden) { running = true; requestAnimationFrame(frame); } };
  new IntersectionObserver(([e]) => { visible = e.isIntersecting; kick(); }).observe(stage);
  document.addEventListener('visibilitychange', kick);
  function frame(t) {
    if (!visible || document.hidden) { running = false; return; }
    mx += (tx - mx) * 0.06; my += (ty - my) * 0.06;
    island.rotation.y = mx * 0.55; island.rotation.x = my * 0.35;
    island.position.z = Math.sin(t / 1400) * 0.12;
    prism.position.x = -mx * 1.6;
    prismMat.opacity += (beam - prismMat.opacity) * 0.08;
    tabMat.opacity += (tabShown - tabMat.opacity) * 0.15;
    aurora.position.x = -mx * 0.8; aurora.position.y = my * 0.5;
    if (want !== lights) {
      let moving = false;
      lights = lights.map((c, i) => c.map((v, j) => { const d = want[i][j] - v; if (Math.abs(d) > (j === 3 ? .004 : .6)) moving = true; return v + d * 0.07; }));
      if (!moving) lights = want;
      drawAurora();
    }
    if (t > nextBlink) { blinking = true; setTimeout(() => { blinking = false; }, 140); nextBlink = t + 2800 + Math.random() * 3000; }
    const m = new Date().getMinutes();
    if (t - last > 50 || m !== minute) { minute = m; last = t; drawFace(t); }
    renderer.render(scene, camera);
    requestAnimationFrame(frame);
  }
  kick();

  return {
    place(cx, cy, w) { place = { cx, cy, w }; apply(); },
    status(s) { status = s; },
    world(next, beamLevel) { want = next.map((c) => c.slice()); beam = beamLevel; },
    tab(on) { tabShown = on ? 1 : 0; },
    show(on) { island.visible = on; },
  };
}
