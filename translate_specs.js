/**
 * Converts flat Telugu specs to locale-keyed specs: { "te": {...}, "en": {...} }
 * Applies a Telugu→English translation map to produce English spec names/values.
 *
 * Usage: node translate_specs.js [--store=store|foodstore|buildstore|services|all] [--dry-run]
 */

const mysql = require('mysql2/promise');

const DB_CONFIG = {
  host: '127.0.0.1', port: 3306,
  user: 'root', password: 'CK@8341754756',
  database: 'DOS', charset: 'utf8mb4',
};

const args = process.argv.slice(2);
const DRY_RUN  = args.includes('--dry-run');
const storeArg = (args.find(a => a.startsWith('--store=')) || '--store=all').split('=')[1];

// ── Translation maps ─────────────────────────────────────────────────────────

// Spec NAME translations (Telugu → English)
const NAME_MAP = {
  'ర్యామ్|రోమ్': 'RAM|ROM',
  'డిస్‌ప్లే': 'Display',
  'డిస్ప్లే': 'Display',
  'కెమెరా': 'Camera',
  'బ్యాటరీ': 'Battery',
  'స్క్రీన్ పరిమాణం': 'Screen Size',
  'బ్రాండ్': 'Brand',
  'ప్రదర్శన సాంకేతికత': 'Display Technology',
  'స్మార్ట్ టీవీ': 'Smart TV',
  'ప్రారంభించిన సంవత్సరం': 'Launch Year',
  'మోడల్ పేరు': 'Model Name',
  'మోడల్ సంఖ్య': 'Model Number',
  'సేల్స్ ప్యాకేజీ': 'Sales Package',
  'కోసం ఆదర్శ': 'Ideal For',
  'టైప్ చేయండి': 'Type',
  'టైప్': 'Type',
  'పరిమాణం': 'Size',
  'వినియోగ రకం': 'Usage Type',
  'ఉత్పత్తి కొలతలు': 'Product Dimensions',
  'కెపాసిటీ': 'Capacity',
  'రంగు': 'Color',
  'శిక్షకుడి పేరు': 'Teacher Name',
  'శిక్షకుడు పేరు': 'Teacher Name',
  'బోధనా అనుభవం': 'Teaching Experience',
  'అర్హత': 'Qualification',
  'మెటీరియల్ రకం': 'Material Type',
  'మెటీరియల్': 'Material',
  'మూసివేత రకం': 'Closure Type',
  'BEE స్టార్ రేటింగ్': 'BEE Star Rating',
  'టన్నులలో సామర్థ్యం': 'Capacity (Tons)',
  'శీతలీకరణ సామర్థ్యం': 'Cooling Capacity',
  'రిజల్యూషన్': 'Resolution',
  'హార్డ్వేర్ ఇంటర్ఫేస్': 'Hardware Interface',
  'అనుకూల పరికరాలు': 'Compatible Devices',
  'ప్రత్యేక ఫీచర్': 'Special Feature',
  'రిజర్వాయర్ సామర్థ్యం': 'Reservoir Capacity',
  'బ్లేడ్‌ల సంఖ్య': 'Number of Blades',
  'అంశం ఫారం': 'Item Form',
  'సువాసన': 'Fragrance',
  'చర్మం రకం': 'Skin Type',
  'ఉత్పత్తి ప్రయోజనాలు': 'Product Benefits',
  'దిండు రకం': 'Pillow Type',
  'మూల రకం': 'Root Type',
  'లిక్విడ్ వాల్యూమ్': 'Liquid Volume',
  'జ్యూస్ ఎక్స్‌ట్రాక్టర్ జార్ కెపాసిటీ': 'Juice Extractor Jar Capacity',
  'Care instructions': 'Care Instructions',
};

// Spec VALUE word replacements (longest-match first to avoid partial replacements)
// Each entry: [Telugu word/phrase, English replacement]
const VALUE_WORDS = [
  ['సెంటీమీటర్లు', 'cm'],
  ['అంగుళాలు', 'inches'],
  ['ఫ్రంట్ కెమెరా', 'Front Camera'],
  ['జీబీ ర్యామ్', 'GB RAM'],
  ['జీబీ రోమ్', 'GB ROM'],
  ['జీబీ', 'GB'],
  ['బ్యాటరీ', 'Battery'],
  ['డిస్‌ప్లే', 'Display'],
  ['డిస్ప్లే', 'Display'],
  ['ర్యామ్', 'RAM'],
  ['రోమ్', 'ROM'],
  ['కెమెరా', 'Camera'],
  ['స్మార్ట్ టీవీ', 'Smart TV'],
  ['మోడల్', 'Model'],
  ['టైప్', 'Type'],
  ['పరిమాణం', 'Size'],
  ['రంగు', 'Color'],
  ['బ్రాండ్', 'Brand'],
  ['అర్హత', 'Qualification'],
  ['సంవత్సరాలు', 'years'],
  ['సంవత్సరం', 'year'],
  ['టన్నులు', 'Tons'],
  ['వాట్లు', 'W'],
];

function translateName(name) {
  return NAME_MAP[name.trim()] ?? name;
}

function translateValue(value) {
  let v = value;
  for (const [te, en] of VALUE_WORDS) {
    v = v.split(te).join(en);
  }
  return v;
}

function buildEnglishSpecs(teSpecs) {
  const en = {};
  for (const [k, v] of Object.entries(teSpecs)) {
    const enKey = translateName(k);
    const enVal = translateValue(v);
    en[enKey] = enVal;
  }
  return en;
}

// ── Main ─────────────────────────────────────────────────────────────────────

async function main() {
  const db = await mysql.createConnection(DB_CONFIG);
  console.log('Connected to DB');

  const prefixCond = storeArg === 'all'
    ? "sku LIKE 'FOODSTORE%' OR sku LIKE 'BUILDSTORE%' OR sku LIKE 'SERVICES%' OR sku LIKE 'STORE%'"
    : `sku LIKE '${storeArg.toUpperCase()}%'`;

  // Select products whose specs is a flat object (not locale-keyed)
  // If already locale-keyed, it will have a "te" or "en" key
  const [rows] = await db.execute(`
    SELECT id, sku, additional
    FROM   products
    WHERE  parent_id IS NULL
      AND  JSON_TYPE(JSON_EXTRACT(additional, '$.specs')) = 'OBJECT'
      AND  JSON_EXTRACT(additional, '$.specs.te') IS NULL
      AND  (${prefixCond})
    ORDER  BY id
  `);

  console.log(`Found ${rows.length} products with flat (non-locale-keyed) specs | DRY_RUN=${DRY_RUN}`);

  let updated = 0, skipped = 0, errors = 0;

  for (let i = 0; i < rows.length; i++) {
    const row = rows[i];
    const sku = row.sku;
    // mysql2 may return additional as already-parsed object
    const addJson = typeof row.additional === 'object' ? row.additional : JSON.parse(row.additional || '{}');

    const teSpecs = addJson.specs;
    if (!teSpecs || typeof teSpecs !== 'object' || Object.keys(teSpecs).length === 0) {
      skipped++;
      continue;
    }

    const enSpecs = buildEnglishSpecs(teSpecs);
    addJson.specs = { te: teSpecs, en: enSpecs };

    if (!DRY_RUN) {
      await db.execute('UPDATE products SET additional = ? WHERE id = ?', [JSON.stringify(addJson), row.id]);
      updated++;
    }

    if ((i + 1) % 100 === 0 || i === 0) {
      const sampleTe = Object.keys(teSpecs)[0];
      const sampleEn = enSpecs[Object.keys(enSpecs)[0]];
      console.log(`[${String(i+1).padStart(4)}/${rows.length}] ${sku}: "${sampleTe}" → "${Object.keys(enSpecs)[0]}"`);
    }
  }

  console.log(`\nDone. Updated=${updated} | Skipped=${skipped} | Errors=${errors}`);
  await db.end();
}

main().catch(e => { console.error('Fatal:', e.message); process.exit(1); });
