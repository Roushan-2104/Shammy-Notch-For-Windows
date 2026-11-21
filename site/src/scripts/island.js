// The island at the top of the home page, drawn as Shammy draws it: a slim pill of frosted smoked glass, a soft
// light inside it, a rim of light along the bottom, the pet's eyes, a status and the visitor's own time.
// It tilts with the pointer, floats a little and blinks. It stops drawing when it is off screen or the tab is hidden.
import * as THREE from 'three';
import { RoomEnvironment } from 'three/examples/jsm/environments/RoomEnvironment.js';

const W = 6.2, H = 0.84, R = H / 2;
const FW = 1240, FH = 168;
const FACE = '"Schibsted Grotesk", "Segoe UI", sans-serif';

function pillPath(g, x, y, w, h) {
  const r = h / 2;
  g.beginPath(); g.moveTo(x + r, y); g.lineTo(x + w - r, y);
  g.arc(x + w - r, y + r, r, -Math.PI / 2, Math.PI / 2); g.lineTo(x + r, y + h);
  g.arc(x + r, y + r, r, Math.PI / 2, Math.PI * 1.5); g.closePath();
}

export function mountIsland(host, canvas, { ground = '#05050A', status = 'Claude is done', reduced = false } = {}) {
  const renderer = new THREE.WebGLRenderer({ canvas, antialias: true });
  renderer.setPixelRatio(Math.min(devicePixelRatio, 1.75));
  renderer.toneMapping = THREE.ACESFilmicToneMapping;
  renderer.outputColorSpace = THREE.SRGBColorSpace;

  const scene = new THREE.Scene();
  scene.background = new THREE.Color(ground);
  const camera = new THREE.PerspectiveCamera(30, 1, 0.1, 100);
  camera.position.set(0, 0, 12);
  const pmrem = new THREE.PMREMGenerator(renderer), room = new RoomEnvironment();
  scene.environment = pmrem.fromScene(room, 0.04).texture;
  pmrem.dispose(); room.dispose();

  // One soft pool of violet and teal behind the glass, so it has light to bend.
  const back = document.createElement('canvas'); back.width = 1024; back.height = 512;
  const bg = back.getContext('2d');
  bg.fillStyle = ground; bg.fillRect(0, 0, 1024, 512);
  for (const [x, y, r, col] of [[420, 250, 260, 'rgba(98,64,200,0.55)'], [640, 230, 220, 'rgba(25,170,155,0.42)']]) {
    const gr = bg.createRadialGradient(x, y, 0, x, y, r); gr.addColorStop(0, col); gr.addColorStop(1, 'rgba(0,0,0,0)');
    bg.fillStyle = gr; bg.fillRect(0, 0, 1024, 512);
  }
  const backTex = new THREE.CanvasTexture(back); backTex.colorSpace = THREE.SRGBColorSpace;
  const glow = new THREE.Mesh(new THREE.PlaneGeometry(40, 20), new THREE.MeshBasicMaterial({ map: backTex, toneMapped: false }));
  glow.position.z = -8;
  scene.add(glow);

  const island = new THREE.Group();
  scene.add(island);
  const shape = new THREE.Shape();
  shape.moveTo(-W / 2 + R, -H / 2);
  shape.lineTo(W / 2 - R, -H / 2);
  shape.absarc(W / 2 - R, 0, R, -Math.PI / 2, Math.PI / 2, false);
  shape.lineTo(-W / 2 + R, H / 2);
  shape.absarc(-W / 2 + R, 0, R, Math.PI / 2, Math.PI * 1.5, false);
  const body = new THREE.ExtrudeGeometry(shape, { depth: 0.22, bevelEnabled: true, bevelThickness: 0.07, bevelSize: 0.05, bevelSegments: 8, curveSegments: 48 });
  body.translate(0, 0, -0.11);
  island.add(new THREE.Mesh(body, new THREE.MeshPhysicalMaterial({
    color: '#ffffff', roughness: 0.34, transmission: 1, thickness: 0.6, ior: 1.4,
    attenuationColor: new THREE.Color('#0d0c14'), attenuationDistance: 0.35,
    clearcoat: 0.35, clearcoatRoughness: 0.3, envMapIntensity: 0.55, specularIntensity: 0.6,
  })));

  const faceCanvas = document.createElement('canvas'); faceCanvas.width = FW; faceCanvas.height = FH;
  const faceTex = new THREE.CanvasTexture(faceCanvas); faceTex.colorSpace = THREE.SRGBColorSpace;
  let blinking = false;
  function drawFace(t) {
    const g = faceCanvas.getContext('2d');
    g.clearRect(0, 0, FW, FH);
    g.save(); pillPath(g, 0, 0, FW, FH); g.clip();
    g.fillStyle = 'rgba(8,8,12,0.6)'; g.fillRect(0, 0, FW, FH);
    const k = t / 1000;
    for (const [cx, cy, rx, col] of [[380 + Math.sin(k * 0.3) * 140, 110, 240, 'rgba(110,70,220,0.28)'], [780 + Math.cos(k * 0.25) * 160, 64, 220, 'rgba(25,195,178,0.2)']]) {
      const gr = g.createRadialGradient(cx, cy, 0, cx, cy, rx); gr.addColorStop(0, col); gr.addColorStop(1, 'rgba(0,0,0,0)');
      g.fillStyle = gr; g.fillRect(0, 0, FW, FH);
    }
    const rim = g.createLinearGradient(0, 0, FW, 0);
    rim.addColorStop(0, 'rgba(120,220,200,0)'); rim.addColorStop(0.3, 'rgba(120,220,200,0.8)'); rim.addColorStop(0.7, 'rgba(160,140,255,0.8)'); rim.addColorStop(1, 'rgba(160,140,255,0)');
    g.fillStyle = rim; g.fillRect(90, FH - 5, FW - 180, 3);
    g.restore();

    g.fillStyle = '#F4F3F0';
    const eh = blinking ? 10 : 68;
    for (const x of [96, 162]) { g.beginPath(); g.roundRect(x - 23, 84 - eh / 2, 46, eh, 23); g.fill(); }

    const [time, ampm = ''] = new Date().toLocaleTimeString('en-US', { hour: 'numeric', minute: '2-digit' }).split(' ');
    g.textBaseline = 'middle'; g.textAlign = 'left';
    g.font = `700 58px ${FACE}`; const tw = g.measureText(time).width;
    g.font = `600 30px ${FACE}`; const aw = g.measureText(ampm).width;
    const right = FW - 96, timeX = right - aw - 10 - tw;
    g.font = `700 58px ${FACE}`; g.fillStyle = '#F4F3F0'; g.fillText(time, timeX, 88);
    g.font = `600 30px ${FACE}`; g.fillStyle = '#A9A7A2'; g.fillText(ampm, right - aw, 97);
    g.fillStyle = 'rgba(255,255,255,0.18)'; g.fillRect(timeX - 30, 64, 2, 42);
    g.font = `600 44px ${FACE}`;
    const sx = timeX - 60 - g.measureText(status).width;
    g.fillStyle = '#F4F3F0'; g.fillText(status, sx, 88);
    g.fillStyle = '#3BD16F'; g.beginPath(); g.arc(sx - 26, 86, 10, 0, Math.PI * 2); g.fill();
    faceTex.needsUpdate = true;
  }
  const face = new THREE.Mesh(new THREE.PlaneGeometry(W, H), new THREE.MeshBasicMaterial({ map: faceTex, transparent: true, depthWrite: false }));
  face.position.z = 0.19; face.renderOrder = 2;
  island.add(face);
  drawFace(0);
  document.fonts?.ready.then(() => drawFace(performance.now()));

  let tx = 0, ty = 0, mx = 0, my = 0;
  const onMove = (e) => { const r = host.getBoundingClientRect(); tx = (e.clientX - r.left) / r.width - 0.5; ty = (e.clientY - r.top) / r.height - 0.5; };
  const onLeave = () => { tx = 0; ty = 0; };
  if (!reduced) {
    host.addEventListener('pointermove', onMove);
    host.addEventListener('pointerleave', onLeave);
  }

  function resize() {
    const w = host.clientWidth, h = host.clientHeight;
    if (!w || !h) return;
    renderer.setSize(w, h, false);
    camera.aspect = w / h;
    const visibleW = 2 * Math.tan(THREE.MathUtils.degToRad(camera.fov / 2)) * camera.position.z * camera.aspect;
    island.scale.setScalar((visibleW * (w < 700 ? 0.88 : 0.62)) / W);
    camera.updateProjectionMatrix();
    draw(performance.now());
  }

  let visible = true, raf = 0, last = 0, nextBlink = 3500, minute = -1;
  function draw(t) {
    mx += (tx - mx) * 0.05; my += (ty - my) * 0.05;
    island.rotation.y = mx * 0.5;
    island.rotation.x = my * 0.3;
    if (!reduced) island.position.y = Math.sin(t / 1600) * 0.06;
    glow.position.x = -mx * 0.6;
    const m = new Date().getMinutes();
    if ((!reduced && t - last > 66) || m !== minute) { minute = m; last = t; drawFace(t); }
    renderer.render(scene, camera);
  }
  function frame(t) {
    raf = 0;
    if (!visible || document.hidden) return;
    if (!reduced && t > nextBlink) { blinking = true; setTimeout(() => { blinking = false; }, 140); nextBlink = t + 3200 + Math.random() * 3000; }
    draw(t);
    raf = requestAnimationFrame(frame);
  }
  // With reduced motion the island holds still: no tilt, no float, no blink, and it redraws only for a new size
  // or a new minute.
  const wake = () => { if (!reduced && visible && !document.hidden && !raf) raf = requestAnimationFrame(frame); };
  new ResizeObserver(resize).observe(host);
  new IntersectionObserver(([e]) => { visible = e.isIntersecting; wake(); }).observe(host);
  document.addEventListener('visibilitychange', wake);
  if (reduced) setInterval(() => { if (visible && !document.hidden) draw(performance.now()); }, 15000);
  resize();
  wake();
}
