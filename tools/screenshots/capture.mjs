// Capture CodeyBox Admin screenshots from a seeded, frozen instance.
// Deterministic by construction: the instance is seeded with a fixed seed and
// its queue is frozen, so the same seed produces the same images.
import { chromium } from 'playwright-core';
import { mkdirSync } from 'node:fs';

const BASE = process.env.ADMIN_URL ?? 'http://localhost:5070';
const OUT = process.env.OUT_DIR ?? './out';
const EXECUTABLE = process.env.CHROMIUM_PATH;

const PAGES = [
  ['01-queue',       '/',                 'Work Queue'],
  ['02-fleet',       '/fleet',            'Fleet'],
  ['03-supervision', '/supervision',      'Supervision'],
  ['04-statistics',  '/statistics',       'Statistics'],
  ['05-capacity',    '/capacity',         'Capacity'],
  ['06-releases',    '/releases',         'Releases'],
  ['07-suggestions', '/suggestions',      'Suggestions'],
  ['08-plugins',     '/plugins',          'Plugins'],
  ['09-new-item',    '/work-items/new',   'New work item'],
];

mkdirSync(OUT, { recursive: true });
const browser = await chromium.launch({ executablePath: EXECUTABLE });
const ctx = await browser.newContext({
  viewport: { width: 1440, height: 900 },
  deviceScaleFactor: 2,
  colorScheme: 'dark',
});
const page = await ctx.newPage();

for (const [name, path, label] of PAGES) {
  try {
    await page.goto(BASE + path, { waitUntil: 'networkidle', timeout: 30000 });
    await page.waitForTimeout(600);
    await page.screenshot({ path: `${OUT}/${name}.png`, fullPage: false });
    console.log(`ok   ${name}  (${label})`);
  } catch (e) {
    console.log(`FAIL ${name}  ${e.message.split('\n')[0]}`);
  }
}

// One detail page, picked deterministically as the first row's link.
try {
  await page.goto(BASE + '/', { waitUntil: 'networkidle' });
  const href = await page.locator('table tbody tr td a').first().getAttribute('href');
  if (href) {
    await page.goto(BASE + href, { waitUntil: 'networkidle' });
    await page.waitForTimeout(600);
    await page.screenshot({ path: `${OUT}/10-work-item-detail.png`, fullPage: false });
    console.log('ok   10-work-item-detail');
  }
} catch (e) { console.log('FAIL 10-work-item-detail ' + e.message.split('\n')[0]); }

await browser.close();
