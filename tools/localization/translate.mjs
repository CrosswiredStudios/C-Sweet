#!/usr/bin/env node
// Fills missing or stale translations by asking an OpenAI-compatible chat endpoint, one area and
// culture at a time, with the glossary and ICU rules in the prompt. English is the source of truth;
// a key is "stale" when its English text changed since it was last translated (tracked in
// Localization/Resources/<Area>/translations.lock, which is not embedded).
//
// Usage:
//   CSWEET_TRANSLATE_ENDPOINT=http://localhost:1234/v1 CSWEET_TRANSLATE_MODEL=... [CSWEET_TRANSLATE_API_KEY=...] \
//   node tools/localization/translate.mjs [--culture de] [--area Settings] [--dry-run] [--force]
import { readFileSync, writeFileSync, readdirSync, statSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const resourcesRoot = join(root, 'src', 'CSweet.UI', 'Localization', 'Resources');
const toolsRoot = join(root, 'tools', 'localization');
const args = process.argv.slice(2);
const opt = (name) => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : undefined; };
const onlyCulture = opt('--culture'), onlyArea = opt('--area');
const dryRun = args.includes('--dry-run'), force = args.includes('--force');
const endpoint = (process.env.CSWEET_TRANSLATE_ENDPOINT ?? '').replace(/\/$/, '');
const model = process.env.CSWEET_TRANSLATE_MODEL;
const apiKey = process.env.CSWEET_TRANSLATE_API_KEY;
if (!dryRun && (!endpoint || !model)) { console.error('Set CSWEET_TRANSLATE_ENDPOINT and CSWEET_TRANSLATE_MODEL.'); process.exit(2); }

const cultures = JSON.parse(readFileSync(join(toolsRoot, 'cultures.json'), 'utf8')).filter(c => c !== 'en');
const glossary = JSON.parse(readFileSync(join(toolsRoot, 'glossary.json'), 'utf8'));
const languageNames = { 'zh-Hans': 'Simplified Chinese', es: 'Spanish (neutral Latin American)', ja: 'Japanese', de: 'German', fr: 'French', 'pt-BR': 'Brazilian Portuguese', ar: 'Modern Standard Arabic', ru: 'Russian', ko: 'Korean' };
const pluralCategories = { 'zh-Hans': 'other', es: 'one, other', ja: 'other', de: 'one, other', fr: 'one, other', 'pt-BR': 'one, other', ar: 'zero, one, two, few, many, other', ru: 'one, few, many, other', ko: 'other' };
const hash = (s) => createHash('sha1').update(s).digest('hex').slice(0, 12);
const readJson = (p) => existsSync(p) ? JSON.parse(readFileSync(p, 'utf8')) : {};
const writeJson = (p, o) => writeFileSync(p, JSON.stringify(o, null, 2) + '\n', 'utf8');

async function translateBatch(culture, area, entries) {
  const terms = Object.entries(glossary).filter(([t]) => !t.startsWith('_')).map(([t, v]) => `- "${t}" → "${v[culture]}"${v._ ? ` (${v._})` : ''}`).join('\n');
  const system = `You translate user-interface strings for C-Sweet, a workspace where a person runs a company staffed by AI agents. Target language: ${languageNames[culture]} (${culture}).
Rules:
- Return ONLY a JSON object mapping each key to its translation. No commentary.
- Strings use ICU MessageFormat. Keep every {placeholder} name exactly as in English. Keep # inside plural branches. Keep ICU syntax ({x, plural, …}, {x, select, …}, offset:n, =0).
- Plural categories for this language: ${pluralCategories[culture]}. Provide exactly those branches (always include "other"). Do not add categories the language lacks.
- Lines starting with "_" in the context are translator notes, not strings.
- Match the register of a polished product UI: concise, neutral, no slang. Keep casing conventions of the target language (German nouns capitalized; sentence case elsewhere).
- Never translate: C-Sweet, GitHub, Discord, LM Studio, OpenAI, Hyper-V, Docker, URLs, code, file paths, keyboard keys.
- Glossary (mandatory):
${terms}`;
  const user = JSON.stringify(Object.fromEntries(entries.map(([k, v, note]) => [k, note ? { text: v, note } : v])), null, 2);
  if (dryRun) { console.log(`[dry-run] ${culture}/${area}: ${entries.length} strings`); return {}; }
  const response = await fetch(`${endpoint}/chat/completions`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', ...(apiKey ? { authorization: `Bearer ${apiKey}` } : {}) },
    body: JSON.stringify({ model, temperature: 0.2, response_format: { type: 'json_object' }, messages: [{ role: 'system', content: system }, { role: 'user', content: user }] })
  });
  if (!response.ok) throw new Error(`${culture}/${area}: HTTP ${response.status} ${await response.text()}`);
  const body = await response.json();
  let content = body.choices?.[0]?.message?.content ?? '{}';
  content = content.replace(/^```(?:json)?\s*/i, '').replace(/```\s*$/, '');
  const parsed = JSON.parse(content);
  const out = {};
  for (const [k, v] of Object.entries(parsed)) out[k] = typeof v === 'string' ? v : v?.text ?? '';
  return out;
}

const placeholders = (p) => { const n = new Set(); let d = 0, s = -1; for (let i = 0; i < p.length; i++) { if (p[i] === '{') { if (d === 0) s = i + 1; d++; } else if (p[i] === '}') { d--; if (d === 0 && s >= 0) { const b = p.slice(s, i); const c = b.indexOf(','); n.add((c < 0 ? b : b.slice(0, c)).trim()); s = -1; } } } return [...n].sort().join('|'); };

for (const area of readdirSync(resourcesRoot).filter(a => statSync(join(resourcesRoot, a)).isDirectory())) {
  if (onlyArea && area !== onlyArea) continue;
  const areaDir = join(resourcesRoot, area);
  const en = readJson(join(areaDir, 'en.json'));
  const notes = Object.fromEntries(Object.entries(en).filter(([k]) => k.startsWith('_') && k.length > 1).map(([k, v]) => [k.slice(1), v]));
  const source = Object.entries(en).filter(([k, v]) => !k.startsWith('_') && typeof v === 'string');
  const lockPath = join(areaDir, 'translations.lock');
  const lock = readJson(lockPath);
  for (const culture of cultures) {
    if (onlyCulture && culture !== onlyCulture) continue;
    const targetPath = join(areaDir, `${culture}.json`);
    const target = readJson(targetPath);
    lock[culture] ??= {};
    const pending = source.filter(([k, v]) => force || !target[k] || lock[culture][k] !== hash(v)).map(([k, v]) => [k, v, notes[k]]);
    for (const k of Object.keys(target)) if (!k.startsWith('_') && !en[k]) delete target[k]; // drop orphans
    if (pending.length === 0) { if (dryRun) console.log(`${culture}/${area}: up to date`); continue; }
    console.log(`${culture}/${area}: translating ${pending.length} of ${source.length}`);
    for (let i = 0; i < pending.length; i += 40) {
      const batch = pending.slice(i, i + 40);
      const result = await translateBatch(culture, area, batch);
      for (const [k, v] of batch) {
        const t = result[k];
        if (!t) { console.warn(`  ${culture}/${area}: no translation returned for ${k}`); continue; }
        if (placeholders(t) !== placeholders(v)) { console.warn(`  ${culture}/${area}: placeholder mismatch for ${k}, keeping English`); continue; }
        target[k] = t; lock[culture][k] = hash(v);
      }
    }
    if (!dryRun) {
      const ordered = { _: `Generated from en.json by tools/localization/translate.mjs; edit freely, re-runs only touch keys whose English changed.` };
      for (const [k] of source) if (target[k]) ordered[k] = target[k];
      writeJson(targetPath, ordered);
      writeJson(lockPath, lock);
    }
  }
}
console.log('done');
