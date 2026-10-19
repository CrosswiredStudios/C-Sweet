#!/usr/bin/env python3
"""Stage translations by hand (or by an assistant) without touching JSON structure.

  python3 tools/localization/stage.py areas
  python3 tools/localization/stage.py export <Area> [<culture>]     # numbered English values; with a culture, only missing/stale ones
  python3 tools/localization/stage.py apply  <Area> <culture> <file> # file: JSON object {"<number>": "<translation>", ...}

apply validates ICU placeholder parity and plural/select structure, merges into
Localization/Resources/<Area>/<culture>.json (ordered like en.json) and updates
<Area>/translations.<culture>.lock with the same sha1 scheme as translate.mjs.
"""
import hashlib, json, os, re, sys
from collections import OrderedDict

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
RES = os.path.join(ROOT, 'src', 'CSweet.UI', 'Localization', 'Resources')
PLURALS = {'zh-Hans': {'other'}, 'ja': {'other'}, 'ko': {'other'}, 'es': {'one', 'other'}, 'de': {'one', 'other'},
           'fr': {'one', 'other'}, 'pt-BR': {'one', 'other'}, 'ru': {'one', 'few', 'many', 'other'},
           'ar': {'zero', 'one', 'two', 'few', 'many', 'other'}}

def h(s): return hashlib.sha1(s.encode('utf-8')).hexdigest()[:12]
def load(p): return json.load(open(p, encoding='utf-8'), object_pairs_hook=OrderedDict) if os.path.exists(p) else OrderedDict()
def save(p, d):
    with open(p, 'w', encoding='utf-8') as f: json.dump(d, f, ensure_ascii=False, indent=2); f.write('\n')

def english(area):
    en = load(os.path.join(RES, area, 'en.json'))
    return [(k, v) for k, v in en.items() if not k.startswith('_') and isinstance(v, str)], en

def args_of(p):
    names, depth, start = set(), 0, -1
    for i, c in enumerate(p):
        if c == '{':
            if depth == 0: start = i + 1
            depth += 1
        elif c == '}':
            depth -= 1
            if depth == 0 and start >= 0:
                body = p[start:i]; comma = body.find(',')
                names.add((body if comma < 0 else body[:comma]).strip()); start = -1
    return names

def plural_blocks(p):
    return [m.start() for m in re.finditer(r'\{\s*\w+\s*,\s*(plural|selectordinal)\s*,', p)]

def check(src, dst, culture):
    if args_of(src) != args_of(dst): return f'placeholders differ: {sorted(args_of(src))} vs {sorted(args_of(dst))}'
    if dst.count('{') != dst.count('}'): return 'unbalanced braces'
    if bool(plural_blocks(src)) != bool(plural_blocks(dst)): return 'plural structure differs'
    for m in re.finditer(r'\{\s*\w+\s*,\s*plural\s*,(.*)\}', dst, re.S):
        cats = set(re.findall(r'(?:^|[\s}])(zero|one|two|few|many|other)\s*\{', m.group(1)))
        if 'other' not in cats: return 'plural without other'
    return None

def main():
    cmd = sys.argv[1]
    if cmd == 'areas':
        for a in sorted(os.listdir(RES)):
            if os.path.exists(os.path.join(RES, a, 'en.json')): print(a, len(english(a)[0]))
        return
    area = sys.argv[2]
    items, en = english(area)
    notes = {k[1:]: v for k, v in en.items() if k.startswith('_') and len(k) > 1}
    if cmd == 'export':
        culture = sys.argv[3] if len(sys.argv) > 3 else None
        target = load(os.path.join(RES, area, f'{culture}.json')) if culture else {}
        lock = load(os.path.join(RES, area, f'translations.{culture}.lock')) if culture else {}
        for i, (k, v) in enumerate(items, 1):
            if culture and k in target and lock.get(k) == h(v): continue
            note = f'   # {notes[k]}' if k in notes else ''
            print(f'{i}\t{k}\t{json.dumps(v, ensure_ascii=False)}{note}')
        return
    if cmd == 'apply':
        culture, path = sys.argv[3], sys.argv[4]
        incoming = json.load(open(path, encoding='utf-8'))
        target_path = os.path.join(RES, area, f'{culture}.json')
        target = load(target_path); lock_path = os.path.join(RES, area, f'translations.{culture}.lock')
        lock = load(lock_path)
        errors, applied = [], 0
        for num, value in incoming.items():
            idx = int(num) - 1
            if idx < 0 or idx >= len(items): errors.append(f'{num}: no such entry'); continue
            k, src = items[idx]
            if not isinstance(value, str) or not value.strip(): errors.append(f'{num} {k}: empty'); continue
            problem = check(src, value, culture)
            if problem: errors.append(f'{num} {k}: {problem}'); continue
            target[k] = value; lock[k] = h(src); applied += 1
        ordered = OrderedDict(_=f'{area} area, {culture}. Edit freely; tools/localization re-translate only keys whose English changed.')
        for k, _ in items:
            if k in target: ordered[k] = target[k]
        save(target_path, ordered); save(lock_path, OrderedDict((k, lock[k]) for k, _ in items if k in lock))
        missing = [i for i, (k, _) in enumerate(items, 1) if k not in ordered]
        print(f'{area}/{culture}: applied {applied}, total {len(ordered) - 1}/{len(items)}, missing {len(missing)}' + (f' (first: {missing[:15]})' if missing else ''))
        for e in errors: print('  REJECTED', e)
        sys.exit(1 if errors else 0)

if __name__ == '__main__':
    main()
