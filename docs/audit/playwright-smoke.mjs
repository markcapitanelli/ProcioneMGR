// Smoke test di ProcioneMGR su http://localhost:5199
//
// Cosa fa:
//   1. verifica che ogni route protetta rimandi al login (302 / redirect a /Account/Login)
//   2. apre le pagine pubbliche e salva uno screenshot in docs/audit/screenshots/
//   3. raccoglie errori di console e risposte HTTP 4xx/5xx
//   4. controlla il layout a 375px (mobile) e 1280px (desktop)
//
// Uso:
//   npm install -D playwright && npx playwright install chromium
//   node docs/audit/playwright-smoke.mjs
//
// Per coprire anche l'area autenticata, esporta le credenziali PRIMA di lanciare:
//   $env:PROCIONE_SMOKE_USER = "..."; $env:PROCIONE_SMOKE_PASS = "..."
// Senza queste variabili lo script salta il login e verifica solo i redirect — che è
// comunque il controllo di sicurezza più importante.

import { chromium } from 'playwright';
import { mkdir } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const BASE = process.env.PROCIONE_SMOKE_BASE ?? 'http://localhost:5199';
const HERE = dirname(fileURLToPath(import.meta.url));
const SHOTS = resolve(HERE, 'screenshots');

const PUBLIC_ROUTES = ['/', '/not-found', '/Account/Login', '/Account/Register'];

const PROTECTED_ROUTES = [
  '/dashboard', '/trading', '/backtest', '/optimization', '/ml', '/discovery',
  '/pipeline', '/ensemble', '/portfolio', '/registry', '/experiments', '/metrics',
  '/market/watchlist', '/market-analysis', '/market/bars', '/alpha-mining', '/regimes',
  '/pairs-trading', '/volatility', '/sentiment', '/strategies', '/execution', '/bot',
  '/campaign', '/feature-selection', '/settings/exchanges', '/admin/ai-supervisor',
  '/admin/autonomy', '/admin/users', '/admin/backup', '/admin/protections',
];

// Pagine di cui vale la pena avere uno screenshot quando si è autenticati.
const SHOT_ROUTES = ['/', '/dashboard', '/trading', '/metrics', '/backtest', '/admin/autonomy'];

const slug = (r) => (r === '/' ? 'home' : r.replace(/^\//, '').replace(/[/?=&]/g, '_'));

const results = { redirects: [], consoleErrors: [], httpErrors: [], shots: [], failures: [] };

async function main() {
  await mkdir(SHOTS, { recursive: true });

  const browser = await chromium.launch();
  const context = await browser.newContext({ viewport: { width: 1280, height: 800 } });
  const page = await context.newPage();

  page.on('console', (m) => {
    if (m.type() === 'error') results.consoleErrors.push({ url: page.url(), text: m.text() });
  });
  page.on('response', (r) => {
    if (r.status() >= 400) results.httpErrors.push({ url: r.url(), status: r.status() });
  });

  // --- 1. Redirect di autenticazione (senza seguire i redirect) -----------------
  const api = await context.request;
  for (const route of PROTECTED_ROUTES) {
    try {
      const res = await api.get(`${BASE}${route}`, { maxRedirects: 0, failOnStatusCode: false });
      const location = res.headers()['location'] ?? '';
      const ok = res.status() === 302 && location.includes('/Account/Login');
      results.redirects.push({ route, status: res.status(), location, ok });
      if (!ok) results.failures.push(`ROUTE NON PROTETTA: ${route} -> ${res.status()} ${location}`);
    } catch (err) {
      results.failures.push(`${route}: ${err.message}`);
    }
  }

  // --- 2. /health deve essere anonimo e 200 ------------------------------------
  const health = await api.get(`${BASE}/health`, { failOnStatusCode: false });
  if (health.status() !== 200) results.failures.push(`/health -> ${health.status()}, atteso 200`);

  // --- 3. 404 su rotta inesistente ---------------------------------------------
  const missing = await api.get(`${BASE}/rotta-inesistente-xyz`, { failOnStatusCode: false });
  if (missing.status() !== 404) results.failures.push(`404 atteso, ottenuto ${missing.status()}`);

  // --- 4. Login opzionale -------------------------------------------------------
  const user = process.env.PROCIONE_SMOKE_USER;
  const pass = process.env.PROCIONE_SMOKE_PASS;
  let authenticated = false;
  if (user && pass) {
    await page.goto(`${BASE}/Account/Login`, { waitUntil: 'networkidle' });
    await page.fill('input[name="Input.Email"]', user);
    await page.fill('input[name="Input.Password"]', pass);
    await Promise.all([page.waitForNavigation({ waitUntil: 'networkidle' }), page.click('button[type="submit"]')]);
    authenticated = !page.url().includes('/Account/Login');
    if (!authenticated) results.failures.push('Login fallito: credenziali rifiutate o form cambiato.');
  }

  // --- 5. Screenshot ------------------------------------------------------------
  const routesToShoot = authenticated ? SHOT_ROUTES : PUBLIC_ROUTES;
  for (const route of routesToShoot) {
    try {
      await page.goto(`${BASE}${route}`, { waitUntil: 'networkidle', timeout: 30_000 });
      // Blazor Server: attendi che il circuito sia attivo prima di fotografare.
      await page.waitForTimeout(1200);
      const file = resolve(SHOTS, `${slug(route)}.png`);
      await page.screenshot({ path: file, fullPage: true });
      results.shots.push(file);
    } catch (err) {
      results.failures.push(`screenshot ${route}: ${err.message}`);
    }
  }

  // --- 6. Responsive ------------------------------------------------------------
  await page.setViewportSize({ width: 375, height: 812 });
  await page.goto(`${BASE}/`, { waitUntil: 'networkidle' });
  await page.waitForTimeout(800);
  const mobileShot = resolve(SHOTS, 'home-mobile.png');
  await page.screenshot({ path: mobileShot, fullPage: true });
  results.shots.push(mobileShot);

  const overflows = await page.evaluate(
    () => document.documentElement.scrollWidth > document.documentElement.clientWidth + 1,
  );
  if (overflows) results.failures.push('Scroll orizzontale a 375px: il layout mobile sborda.');

  await browser.close();
  report();
}

function report() {
  const okRedirects = results.redirects.filter((r) => r.ok).length;
  console.log('\n=== ProcioneMGR — smoke test ===');
  console.log(`Redirect di autenticazione : ${okRedirects}/${results.redirects.length} corretti`);
  console.log(`Screenshot salvati         : ${results.shots.length} in ${SHOTS}`);
  console.log(`Errori di console          : ${results.consoleErrors.length}`);
  console.log(`Risposte HTTP >= 400       : ${results.httpErrors.length}`);

  for (const e of results.httpErrors) console.log(`   ${e.status}  ${e.url}`);
  for (const e of results.consoleErrors) console.log(`   [console] ${e.text}`);

  if (results.failures.length) {
    console.log('\n--- FALLIMENTI ---');
    for (const f of results.failures) console.log(`  ✗ ${f}`);
    process.exitCode = 1;
  } else {
    console.log('\nTutti i controlli superati.');
  }
}

main().catch((err) => {
  console.error(err);
  process.exitCode = 1;
});
