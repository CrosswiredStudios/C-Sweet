#!/usr/bin/env node
// Validates the UI resource files: JSON well-formedness, placeholder parity with English,
// missing/orphan keys per culture, and keys referenced from Razor/C# that English does not define.
// Usage: node tools/localization/check-resources.mjs [--strict]
import { readFileSync, readdirSync, statSync, existsSync } from 'node:fs';
import { join, dirname, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const uiRoot = join(root, 'src', 'CSweet.UI');
const resourcesRoot = join(uiRoot, 'Localization', 'Resources');
const strict = process.argv.includes('--strict');
const cultures = JSON.parse(readFileSync(join(root, 'tools', 'localization', 'cultures.json'), 'utf8'));

let errors = 0, warnings = 0;
const error = (m) => { errors++; console.error(`error: ${m}`); };
const warn = (m) => { warnings++; console.warn(`warn:  ${m}`); };

function walk(dir, out = []) {
  for (const name of readdirSync(dir)) {
    if (name === 'bin' || name === 'obj' || name === 'node_modules' || name.startsWith('.')) continue;
    const p = join(dir, name);
    if (statSync(p).isDirectory()) walk(p, out); else out.push(p);
  }
  return out;
}

function placeholders(pattern) {
  const names = new Set(); let depth = 0, start = -1;
  for (let i = 0; i < pattern.length; i++) {
    const c = pattern[i];
    if (c === '{') { if (depth === 0) start = i + 1; depth++; }
    else if (c === '}') { depth--; if (depth === 0 && start >= 0) { const body = pattern.slice(start, i); const comma = body.indexOf(','); names.add((comma < 0 ? body : body.slice(0, comma)).trim()); start = -1; } }
  }
  return names;
}

// Load tables.
const tables = {}; // culture -> { key -> { value, area } }
for (const area of readdirSync(resourcesRoot)) {
  const areaDir = join(resourcesRoot, area);
  if (!statSync(areaDir).isDirectory()) continue;
  for (const file of readdirSync(areaDir).filter(f => f.endsWith('.json'))) {
    const culture = file.slice(0, -5);
    let parsed;
    try { parsed = JSON.parse(readFileSync(join(areaDir, file), 'utf8')); }
    catch (e) { error(`${area}/${file}: invalid JSON (${e.message})`); continue; }
    tables[culture] ??= {};
    for (const [key, value] of Object.entries(parsed)) {
      if (key.startsWith('_')) continue;
      if (typeof value !== 'string') { error(`${area}/${file}: '${key}' must be a string`); continue; }
      if (tables[culture][key]) error(`${area}/${file}: '${key}' is also defined in ${tables[culture][key].area}`);
      tables[culture][key] = { value, area };
    }
  }
}

const en = tables.en ?? {};
if (Object.keys(en).length === 0) error('no English resources found');

// Keys referenced from source.
const keyPattern = /\bL(?:ocalizer)?\s*(?:\[\s*|\.Format\s*\(\s*|\.Has\s*\(\s*)"([A-Za-z0-9_.-]+)"/g;
const used = new Map();
for (const file of walk(uiRoot).filter(f => (f.endsWith('.razor') || f.endsWith('.cs')) && !f.includes(`${'Localization'}`))) {
  const text = readFileSync(file, 'utf8');
  for (const match of text.matchAll(keyPattern)) {
    const list = used.get(match[1]) ?? []; list.push(relative(root, file)); used.set(match[1], list);
  }
}
for (const [key, files] of used) {
  if (!en[key] && !key.startsWith('Mud.')) error(`'${key}' is used in ${files[0]} but not defined in English resources`);
}
// Keys built dynamically (e.g. prefix + enum) cannot be matched here; those areas keep an explicit "_dynamic" note.
for (const key of Object.keys(en)) {
  if (!used.has(key) && !key.startsWith('Mud.') && !key.startsWith('Common.') && !key.startsWith('Settings.Section.') && !key.startsWith('Settings.Group.')) {
    (strict ? error : warn)(`'${key}' (${en[key].area}) is defined in English but no Razor/C# file references it literally`);
  }
}

// Per-culture parity.
for (const culture of cultures.filter(c => c !== 'en')) {
  const table = tables[culture];
  if (!table) { error(`${culture}: no resource files`); continue; }
  const missing = Object.keys(en).filter(k => !table[k]);
  const orphan = Object.keys(table).filter(k => !en[k]);
  if (missing.length) (strict ? error : warn)(`${culture}: ${missing.length} keys missing (first: ${missing.slice(0, 5).join(', ')})`);
  if (orphan.length) warn(`${culture}: ${orphan.length} keys not in English (first: ${orphan.slice(0, 5).join(', ')})`);
  for (const [key, { value, area }] of Object.entries(table)) {
    if (!en[key]) continue;
    const a = placeholders(en[key].value), b = placeholders(value);
    if (a.size !== b.size || [...a].some(x => !b.has(x))) error(`${culture}/${area}: '${key}' placeholders differ (en {${[...a]}} vs {${[...b]}})`);
    if (table[key].area !== en[key].area) warn(`${culture}: '${key}' lives in ${area} but English has it in ${en[key].area}`);
  }
}

console.log(`${Object.keys(en).length} English keys, ${cultures.length} cultures, ${used.size} keys referenced from source. ${errors} errors, ${warnings} warnings.`);
process.exit(errors ? 1 : 0);
