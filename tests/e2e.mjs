// End-to-end test: serve the built app, upload the sample SVG, select paths,
// assign heights, export the STL and validate the binary.
//
// Prereqs: `npm run build` (dist/ must exist), a Chromium binary
// (CHROMIUM_PATH env var, default /opt/pw-browsers/chromium).
import http from 'node:http';
import path from 'node:path';
import { createReadStream, existsSync, readFileSync, mkdtempSync } from 'node:fs';
import os from 'node:os';
import { chromium } from 'playwright-core';

const ROOT = path.resolve(path.dirname(new URL(import.meta.url).pathname), '..');
const DIST = path.join(ROOT, 'dist');
const SAMPLE = path.join(ROOT, 'examples', 'sample.svg');
const PORT = 4183;

const MIME = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml' };

let failures = 0;
const check = (cond, msg) => {
  console.log(`${cond ? 'PASS' : 'FAIL'}  ${msg}`);
  if (!cond) failures++;
};

const server = http.createServer((req, res) => {
  const url = req.url === '/' ? '/index.html' : req.url.split('?')[0];
  const file = path.join(DIST, decodeURIComponent(url));
  if (!file.startsWith(DIST) || !existsSync(file)) {
    res.writeHead(404).end('not found');
    return;
  }
  res.writeHead(200, { 'content-type': MIME[path.extname(file)] ?? 'application/octet-stream' });
  createReadStream(file).pipe(res);
});
await new Promise((r) => server.listen(PORT, r));

const browser = await chromium.launch({
  executablePath: process.env.CHROMIUM_PATH || '/opt/pw-browsers/chromium',
});
const context = await browser.newContext({ acceptDownloads: true });
const page = await context.newPage();
const pageErrors = [];
page.on('pageerror', (e) => pageErrors.push(String(e)));
page.on('console', (m) => {
  if (m.type() === 'error') pageErrors.push(m.text());
});

try {
  await page.goto(`http://127.0.0.1:${PORT}/`);
  await page.waitForSelector('#dropzone', { state: 'visible' });

  // --- upload ---
  await page.setInputFiles('#file-input', SAMPLE);
  await page.waitForSelector('#stage', { state: 'visible' });
  check(true, 'SVG uploaded and stage shown');
  check(await page.locator('.warning').count() >= 1, 'open-subpath warning surfaced');

  // --- build paths (groups) and assign shapes to them ---
  // Shape clicks are hit-tested at the click point, so click inside each
  // island (fractions of the element's bounding box).
  const clickPart = async (selector, fx, fy) => {
    // Interacting with the right-hand panel can scroll the SVG offscreen;
    // mouse.click needs viewport coordinates, so bring the shape back first.
    await page.locator(selector).scrollIntoViewIfNeeded();
    const box = await page.locator(selector).boundingBox();
    await page.mouse.click(box.x + box.width * fx, box.y + box.height * fy);
  };
  const setHeight = async (h) => {
    await page.fill('#height-num', String(h));
    await page.dispatchEvent('#height-num', 'input');
  };
  const newPath = async (h) => {
    await page.fill('#new-height', String(h));
    await page.click('#new-path-btn');
  };

  // Path 1 exists automatically — starting a new path is not mandatory.
  await clickPart('[data-svgx-id="donut"]', 0.14, 0.5); // on the ring, not the hole
  await setHeight(4); // editor edits the active path

  await newPath(8);
  await clickPart('[data-svgx-id="dot"]', 0.5, 0.5);

  await newPath(6);
  await clickPart('g ellipse', 0.5, 0.5); // transformed ellipse, generated id

  // One path holding BOTH islands of the single twin <path> element.
  await newPath(5);
  await clickPart('[data-svgx-id="twin"]', 0.5, 0.18); // upper square
  await clickPart('[data-svgx-id="twin"]', 0.5, 0.82); // lower square

  // Go BACK to Path 1 and add another shape to it at its 4mm height.
  await page.click('#legend li:first-child .l-name');
  await clickPart('[data-svgx-id="plate"]', 0.5, 0.5);

  await newPath(2);
  await clickPart('[data-svgx-id="wedge"]', 0.5, 0.6);

  check((await page.locator('#legend li').count()) === 5, 'legend lists 5 paths');
  check(
    (await page.locator('#legend li:first-child .l-count').textContent()).includes('2'),
    'plate joined Path 1 after re-activating it (2 parts)'
  );
  check(
    (await page.locator('#legend li:nth-child(4) .l-count').textContent()).includes('2'),
    'both islands of the single twin element are in one path'
  );
  check((await page.locator('.svgx-overlay').count()) === 7, '7 highlighted islands');

  // color change on the active path (the wedge's)
  await page.fill('#color-input', '#ff8800');
  await page.dispatchEvent('#color-input', 'input');

  // clicking a shape already in the active path removes it; click re-adds it
  await clickPart('[data-svgx-id="wedge"]', 0.5, 0.6);
  check(
    (await page.locator('#legend li:last-child .l-count').textContent()).includes('0'),
    'clicking a shape again removes it from the active path'
  );
  await clickPart('[data-svgx-id="wedge"]', 0.5, 0.6);

  // empty paths can be created and deleted freely
  await newPath(9);
  check((await page.locator('#legend li').count()) === 6, 'extra path created');
  await page.click('#legend li:last-child .l-x');
  check((await page.locator('#legend li').count()) === 5, 'path deleted via legend ×');

  const canvasBox = await page.locator('#viewer-box canvas').boundingBox();
  check(canvasBox && canvasBox.width > 100 && canvasBox.height > 100, '3D preview canvas is rendered');

  check(
    (await page.locator('#size-readout').textContent()).includes('mm'),
    'model size readout shows mm dimensions'
  );

  // --- export ---
  const [download] = await Promise.all([
    page.waitForEvent('download'),
    page.click('#export-btn'),
  ]);
  const outPath = path.join(mkdtempSync(path.join(os.tmpdir(), 'stl-')), 'model.stl');
  await download.saveAs(outPath);
  check(download.suggestedFilename() === 'sample.stl', `download named ${download.suggestedFilename()}`);

  // --- validate binary STL ---
  const buf = readFileSync(outPath);
  const triCount = buf.readUInt32LE(80);
  check(buf.length === 84 + 50 * triCount, `byte length matches triangle count (${triCount} tris)`);
  check(triCount > 150, 'non-trivial triangle count');

  let minZ = Infinity, maxZ = -Infinity, signedVolume = 0;
  const edges = new Map();
  const key = (x, y, z) => `${x.toFixed(4)},${y.toFixed(4)},${z.toFixed(4)}`;
  for (let t = 0; t < triCount; t++) {
    const o = 84 + 50 * t + 12; // skip normal
    const v = [];
    for (let k = 0; k < 3; k++) {
      const x = buf.readFloatLE(o + 12 * k);
      const y = buf.readFloatLE(o + 12 * k + 4);
      const z = buf.readFloatLE(o + 12 * k + 8);
      v.push([x, y, z]);
      if (z < minZ) minZ = z;
      if (z > maxZ) maxZ = z;
    }
    const [a, b, c] = v;
    signedVolume +=
      (a[0] * (b[1] * c[2] - c[1] * b[2]) -
        a[1] * (b[0] * c[2] - c[0] * b[2]) +
        a[2] * (b[0] * c[1] - c[0] * b[1])) / 6;
    for (let k = 0; k < 3; k++) {
      const e = key(...v[k]) + '|' + key(...v[(k + 1) % 3]);
      edges.set(e, (edges.get(e) ?? 0) + 1);
    }
  }
  check(Math.abs(minZ) < 1e-4, `model sits on z=0 (minZ=${minZ})`);
  check(Math.abs(maxZ - 8) < 1e-3, `tallest part is 8mm (maxZ=${maxZ})`);
  check(signedVolume > 0, `outward-facing normals (signed volume=${signedVolume.toFixed(1)} mm³)`);

  // Watertight: every directed edge used exactly once, and its reverse exists.
  let bad = 0;
  for (const [e, n] of edges) {
    const rev = e.split('|').reverse().join('|');
    if (n !== 1 || (edges.get(rev) ?? 0) !== 1) bad++;
  }
  check(bad === 0, `watertight mesh (${bad} bad edges of ${edges.size})`);

  // Volume sanity: at the default 0.01 tolerance the discretization is close
  // to exact — donut 816.8mm²x4 + plate 734.3mm²x4 + dot 452.4mm²x8
  // + ellipse 169.6mm²x6 + twins 200mm²x5 + wedge 200mm²x2 ≈ 12240 mm³.
  check(
    signedVolume > 12000 && signedVolume < 12350,
    `volume in expected range (${signedVolume.toFixed(0)} mm³ vs ~12240 expected)`
  );

  await page.screenshot({ path: path.join(ROOT, 'e2e-screenshot.png'), fullPage: true });
  check(pageErrors.length === 0, `no console/page errors${pageErrors.length ? ': ' + pageErrors.join(' | ') : ''}`);
} finally {
  await browser.close();
  server.close();
}

console.log(failures === 0 ? '\nAll checks passed.' : `\n${failures} check(s) FAILED`);
process.exit(failures === 0 ? 0 : 1);
