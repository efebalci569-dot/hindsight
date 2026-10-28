// Hindsight site: TR/EN toggle, latest release info, and the rolling-buffer hero animation.
(() => {
  const REPO = 'efebalci569-dot/hindsight';

  // ── i18n (Turkish is in the HTML; English replaces it) ─────────────────────
  const en = {
    'nav.how': 'How it works', 'nav.features': 'Features', 'nav.performance': 'Performance', 'nav.faq': 'FAQ', 'nav.download': 'Download',
    'hero.eyebrow': 'Instant replay for Windows',
    'hero.title': 'Never miss your <em>best moment.</em>',
    'hero.lead': 'While you play, Hindsight quietly keeps the last moments in the background. When something great happens, press one key — the last 30 seconds become a clip. Sorted game by game, ready for Discord.',
    'hero.cta': 'Download for Windows', 'hero.github': 'Source code', 'hero.meta': 'Free · Open source · Windows 10 / 11',
    'ring.label': 'last moments', 'ring.sub': 'rolls on disk · never grows in RAM',
    'toast.title': 'Clip saved', 'launchers.label': 'Finds your library:',
    'how.title': 'Three steps. It handles the rest.',
    'how.s1.t': 'Open your game', 'how.s1.d': 'Hindsight recognises the game and starts the background buffer by itself — even full-screen games that are not in any launcher. It stops when the game closes.',
    'how.s2.t': 'Press the hotkey', 'how.s2.d': '<kbd>Alt</kbd> + <kbd>F8</kbd> — everything that just happened is saved as a clip in the game\'s folder. No need to leave the game; a small confirmation shows in the corner.',
    'how.s3.t': 'Share it', 'how.s3.d': 'Trim it, shrink it to 10 MB for Discord, turn it into a GIF, or copy the file and paste it into a chat.',
    'features.title': 'Clips sorted by game, library sorted by launcher.',
    'features.lead': 'Every game gets its own folder: Videos\\Hindsight\\Game Name. The app works the same way — pick a game on the left, see its clips.',
    'features.note': 'Illustrative preview; game names are made up.',
    'mock.home': 'Home', 'mock.games': 'Games', 'mock.clips': 'Clips', 'mock.settings': 'Settings', 'mock.buffer': 'Background buffer on',
    'mock.save': 'Save clip', 'mock.byGame': 'BY GAME', 'mock.all': 'All clips', 'mock.fav': 'Favourites',
    'f1.t': 'Instant replay', 'f1.d': 'Pick anything from the last 15 seconds to 5 minutes. A clip includes everything up to the moment you press the key.',
    'f2.t': 'Game library', 'f2.d': 'Steam, Epic, GOG, Ubisoft, EA, Battle.net, Riot, Rockstar and Xbox games, section by section. Covers, play time, clip counts, one-click launch.',
    'f3.t': 'Ready for Discord', 'f3.d': 'Trim in the player; shrink to fit 10 MB or 50 MB in one click; make a GIF; copy the file and paste it into a chat.',
    'f4.t': 'Quality is your call', 'f4.d': '480p to 1080p, 30 or 60 FPS, H.264 / HEVC / AV1, a quality preset or your own bitrate. Settings show the RAM and disk it will use before you record.',
    'f5.t': 'Recording & screenshots', 'f5.d': 'Unlimited manual recording (<kbd>Alt+F7</kbd>) and full-resolution screenshots (<kbd>Alt+F12</kbd>). Every hotkey can be changed.',
    'f6.t': 'Game audio + microphone', 'f6.d': 'Whatever you hear in your headset, plus your mic if you want, with its own volume. Switch output devices and the recording follows.',
    'perf.title': 'Built not to steal FPS from your game.',
    'perf.lead': 'Frames stay on the graphics card from start to finish. The last moments live in small chunks rolling on disk, not in RAM.',
    'p1.t': 'Screen', 'p1.d': 'Windows Graphics Capture', 'p2.t': 'Scaled on the GPU', 'p3.t': 'Hardware encoder', 'p4.t': '2-second chunks', 'p4.d': 'on disk, in a ring', 'p5.d': 'MP4 in 0.1 s',
    's1': 'RAM while waiting in the tray', 's2': 'recorder at 720p60', 's3': 'of one CPU core (720p60)', 's4': 're-encoding when saving a clip',
    'perf.note': 'Measured on an RTX 5070 with a 1920×1080 screen. At 1080p60 the recorder uses ~200 MB and ~10% of one core. PCs without a GPU encoder fall back to Windows Media Foundation or the CPU, which costs more.',
    'dl.title': 'Download Hindsight', 'dl.lead': 'Free, no ads, no account. Nothing is uploaded anywhere.',
    'dl.r1': 'Windows 10 (1903+) or Windows 11, 64-bit', 'dl.r2': 'No administrator rights needed', 'dl.r3': 'NVIDIA, AMD or Intel graphics recommended',
    'dl.installer': 'Download installer', 'dl.portable': 'Portable (.zip)', 'dl.notes': 'Release notes',
    'dl.smartscreen': 'The app is not code-signed, so Windows SmartScreen may warn on first launch: <b>More info → Run anyway</b>.',
    'faq.title': 'FAQ',
    'q1': 'Will it lower my FPS?', 'a1': 'With the GPU\'s dedicated encoder (NVENC / AMF / Quick Sync) the impact is usually a few FPS at most, because frames never leave the graphics card. No recorder can promise "zero" though; if your PC struggles, switch to 720p or 30 FPS, or set the recording priority to "Low".',
    'q2': 'Where are clips saved?', 'a2': 'By default in Videos\\Hindsight\\&lt;Game name&gt;, screenshots in a "Screenshots" folder inside it. You can change the folder in settings.',
    'q3': 'Why does it use so little RAM?', 'a3': 'Most instant-replay tools keep the compressed last moments in RAM, so memory grows with the replay length. Hindsight keeps them on disk in 2-second files that overwrite each other — a 5-minute setting uses as much RAM as 15 seconds. Closing the window also removes the whole UI from memory.',
    'q4': 'Which games does it recognise?', 'a4': 'It scans Steam, Epic Games, GOG, Ubisoft Connect, EA app, Battle.net, Riot Games, Rockstar and Xbox / Microsoft Store installs. Play a game in full screen that is not listed and it adds it too; you can also add a game by hand or tell it not to record one.',
    'q5': 'Do I need an account or internet?', 'a5': 'No. Hindsight sends no data and has no telemetry. Its only network request: downloading Steam\'s public store artwork once for Steam games that Steam has not cached locally.',
    'q6': 'Windows says "unknown publisher".', 'a6': 'Hindsight is distributed without a code-signing certificate. The source is open on GitHub, and you can compare your download with the SHA-256 checksums on the release page.',
    'footer.text': 'MIT-licensed open source. The recorder runs on FFmpeg (GPL v3). Game names and artwork belong to their owners.',
  };

  const nodes = [...document.querySelectorAll('[data-i18n]')];
  nodes.forEach((n) => { n.dataset.tr = n.innerHTML; });
  const button = document.getElementById('lang');

  function setLang(lang) {
    document.documentElement.lang = lang;
    nodes.forEach((n) => {
      const key = n.dataset.i18n;
      n.innerHTML = lang === 'en' && en[key] ? en[key] : n.dataset.tr;
    });
    button.textContent = lang === 'en' ? 'TR' : 'EN';
    document.title = lang === 'en' ? 'Hindsight — Never miss your best moment' : 'Hindsight — Oyundaki en iyi anı kaçırma';
    try { localStorage.setItem('lang', lang); } catch { /* private mode */ }
  }

  const browserLang = (navigator.language || '').toLowerCase().startsWith('tr') ? 'tr' : 'en';
  let lang = browserLang;
  try { lang = localStorage.getItem('lang') || browserLang; } catch { /* storage blocked: follow the browser */ }
  if (lang === 'en') setLang('en');
  button.addEventListener('click', () => setLang(document.documentElement.lang === 'en' ? 'tr' : 'en'));

  // ── Latest release: version + file sizes ─────────────────────────────────
  fetch(`https://api.github.com/repos/${REPO}/releases/latest`, { headers: { Accept: 'application/vnd.github+json' } })
    .then((r) => (r.ok ? r.json() : null))
    .then((release) => {
      if (!release) return;
      document.querySelectorAll('[data-version]').forEach((el) => { el.textContent = release.tag_name; });
      const size = (name) => {
        const asset = release.assets.find((a) => a.name === name);
        return asset ? `· ${Math.round(asset.size / 1048576)} MB` : '';
      };
      const setup = document.querySelector('[data-size="setup"]');
      const zip = document.querySelector('[data-size="zip"]');
      if (setup) setup.textContent = size('HindsightSetup.exe');
      if (zip) zip.textContent = size('Hindsight-win-x64.zip');
    })
    .catch(() => { /* offline or rate-limited: keep the static text */ });

  // ── Hero: a ring of 2-second segments; the newest lights up, the oldest is overwritten ──
  const svg = document.getElementById('ring');
  const SEGMENTS = 15; // 15 × 2 s = 30 s
  const cx = 160, cy = 160, r = 136, width = 22, gapDeg = 3.2;
  const ns = 'http://www.w3.org/2000/svg';
  const defs = document.createElementNS(ns, 'defs');
  defs.innerHTML = '<linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#ff9f43"/><stop offset="1" stop-color="#ff4d5e"/></linearGradient>';
  svg.appendChild(defs);
  const track = document.createElementNS(ns, 'circle');
  Object.entries({ cx, cy, r, fill: 'none', stroke: '#1b1b26', 'stroke-width': width + 8 }).forEach(([k, v]) => track.setAttribute(k, v));
  svg.appendChild(track);

  const polar = (deg, radius) => {
    const a = ((deg - 90) * Math.PI) / 180;
    return [cx + radius * Math.cos(a), cy + radius * Math.sin(a)];
  };
  const segs = [];
  for (let i = 0; i < SEGMENTS; i++) {
    const start = (360 / SEGMENTS) * i + gapDeg / 2;
    const end = (360 / SEGMENTS) * (i + 1) - gapDeg / 2;
    const [x1, y1] = polar(start, r + width / 2), [x2, y2] = polar(end, r + width / 2);
    const [x3, y3] = polar(end, r - width / 2), [x4, y4] = polar(start, r - width / 2);
    const path = document.createElementNS(ns, 'path');
    path.setAttribute('d', `M${x1} ${y1} A${r + width / 2} ${r + width / 2} 0 0 1 ${x2} ${y2} L${x3} ${y3} A${r - width / 2} ${r - width / 2} 0 0 0 ${x4} ${y4} Z`);
    path.setAttribute('class', 'seg');
    path.setAttribute('fill', 'url(#g)');
    path.setAttribute('opacity', '0.08');
    svg.appendChild(path);
    segs.push(path);
  }
  const head = document.createElementNS(ns, 'circle');
  head.setAttribute('r', '7');
  head.setAttribute('fill', '#fff');
  svg.appendChild(head);

  const games = ['Neon Drift', 'Skyfall Arena', 'Ironclad Tactics'];
  const keys = document.getElementById('keys');
  const toast = document.getElementById('toast');
  const toastGame = document.getElementById('toast-game');
  const reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  let tick = 0;

  function frame() {
    const current = tick % SEGMENTS;
    segs.forEach((seg, i) => {
      // age 0 = being written now; older segments fade, the oldest is about to be overwritten
      const age = (current - i + SEGMENTS) % SEGMENTS;
      seg.setAttribute('opacity', age === 0 ? '1' : String(Math.max(0.16, 0.92 - age * 0.05)));
    });
    const [hx, hy] = polar((360 / SEGMENTS) * (current + 0.5), r);
    head.setAttribute('cx', hx);
    head.setAttribute('cy', hy);

    if (tick % 12 === 8) {
      keys.classList.add('press');
      setTimeout(() => keys.classList.remove('press'), 220);
      toastGame.textContent = games[(tick / 12 | 0) % games.length];
      setTimeout(() => toast.classList.add('show'), 260);
      setTimeout(() => toast.classList.remove('show'), 2600);
    }
    tick++;
  }

  frame();
  if (!reduced) setInterval(frame, 700);
  else segs.forEach((s) => s.setAttribute('opacity', '0.85'));
})();
