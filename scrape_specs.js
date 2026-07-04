/**
 * Scrapes product specifications from the source nopCommerce sites
 * (foodstore/buildstore/services.digitaldarsi.in) for products that have
 * "specs": null, then updates the MySQL database.
 *
 * Usage: node scrape_specs.js [--store=foodstore|buildstore|services|all] [--dry-run]
 */

const https = require('https');
const mysql = require('mysql2/promise');

const DB_CONFIG = {
  host: '127.0.0.1', port: 3306,
  user: 'root', password: 'CK@8341754756',
  database: 'DOS', charset: 'utf8mb4',
};

const DELAY_MS  = 350;
const TIMEOUT_MS = 12000;

const args = process.argv.slice(2);
const DRY_RUN  = args.includes('--dry-run');
const storeArg = (args.find(a => a.startsWith('--store=')) || '--store=all').split('=')[1];

// ── HTML helpers ────────────────────────────────────────────────────────────

function decodeHtml(s) {
  return s
    .replace(/&#x([0-9A-Fa-f]+);/g, (_, h) => String.fromCodePoint(parseInt(h, 16)))
    .replace(/&#(\d+);/g,             (_, d) => String.fromCodePoint(parseInt(d, 10)))
    .replace(/&amp;/g, '&').replace(/&lt;/g, '<').replace(/&gt;/g, '>')
    .replace(/&nbsp;/g, ' ').replace(/&quot;/g, '"')
    .replace(/<[^>]+>/g, '').trim();
}

function extractSpecs(html) {
  const specs = {};
  // nopCommerce: no closing </td> — match until next <tr or </tbody
  const re = /<td class="spec-name">([\s\S]*?)<td class="spec-value">([\s\S]*?)(?=<tr|<\/tbody)/g;
  let m;
  while ((m = re.exec(html)) !== null) {
    const k = decodeHtml(m[1]);
    const v = decodeHtml(m[2]);
    if (k && v) specs[k] = v;
  }
  return specs;
}

// ── HTTP fetch ──────────────────────────────────────────────────────────────

function fetchPage(url) {
  return new Promise((resolve, reject) => {
    const req = https.get(url, { timeout: TIMEOUT_MS }, res => {
      const buf = [];
      res.on('data', d => buf.push(d));
      res.on('end', () => resolve(Buffer.concat(buf).toString('utf8')));
    });
    req.on('error', reject);
    req.on('timeout', () => { req.destroy(); reject(new Error('HTTP timeout')); });
  });
}

const sleep = ms => new Promise(r => setTimeout(r, ms));

// ── Main ────────────────────────────────────────────────────────────────────

async function main() {
  const db = await mysql.createConnection(DB_CONFIG);
  console.log('Connected to DB');

  const prefixes = storeArg === 'all'
    ? ['FOODSTORE', 'BUILDSTORE', 'SERVICES']
    : [storeArg.toUpperCase()];
  const prefixCond = prefixes.map(p => `sku LIKE '${p}%'`).join(' OR ');

  const [rows] = await db.execute(`
    SELECT id, sku, additional,
           JSON_UNQUOTE(JSON_EXTRACT(additional, '$.source_url')) AS src_url
    FROM   products
    WHERE  parent_id IS NULL
      AND  additional IS NOT NULL
      AND  JSON_TYPE(JSON_EXTRACT(additional, '$.specs')) = 'NULL'
      AND  JSON_EXTRACT(additional, '$.source_url') IS NOT NULL
      AND  (${prefixCond})
    ORDER  BY id
  `);

  console.log(`Found ${rows.length} products with null specs | DRY_RUN=${DRY_RUN}`);

  let updated = 0, noSpecs = 0, errors = 0;

  for (let i = 0; i < rows.length; i++) {
    const { id, sku, additional, src_url } = rows[i];
    const prefix = `[${String(i+1).padStart(4)}/${rows.length}] ${sku}`;

    let specs = null;
    try {
      const html = await fetchPage(src_url);
      specs = html.includes('product-specs-box') ? extractSpecs(html) : {};
    } catch (err) {
      process.stderr.write(`${prefix} ERROR: ${err.message}\n`);
      errors++;
      await sleep(DELAY_MS);
      continue;
    }

    const specCount = Object.keys(specs).length;

    if (!DRY_RUN) {
      // mysql2 may auto-parse JSON columns into objects; handle both cases
      let addJson;
      if (typeof additional === 'object' && additional !== null) {
        addJson = additional;
      } else {
        try { addJson = JSON.parse(additional || '{}'); } catch { addJson = {}; }
      }
      addJson.specs = specs;
      await db.execute(
        'UPDATE products SET additional = ? WHERE id = ?',
        [JSON.stringify(addJson), id]
      );
      updated++;
    }

    if (specCount > 0) {
      console.log(`${prefix}: ${specCount} specs — ${Object.keys(specs).slice(0,3).join(', ')}${specCount>3?'…':''}`);
    } else {
      noSpecs++;
    }

    await sleep(DELAY_MS);
  }

  console.log(`\nDone. Updated=${updated} | NoSpecs=${noSpecs} | Errors=${errors}`);
  await db.end();
}

main().catch(e => { console.error('Fatal:', e.message); process.exit(1); });
