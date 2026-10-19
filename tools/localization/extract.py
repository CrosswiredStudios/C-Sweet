#!/usr/bin/env python3
"""Conservative string extractor for CSweet.UI Razor components.

Moves plain markup text nodes, common text attributes and simple message assignments
into Localization/Resources/<Area>/en.json and replaces them with IAppLocalizer calls.
Anything ambiguous (interpolation, mixed markup, C# expressions) is left untouched and
listed by --report so it can be converted by hand.

Usage: python3 tools/localization/extract.py <Area> <file.razor> [...] [--dry-run] [--report]
"""
import html, json, os, re, sys
from collections import OrderedDict

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
RES = os.path.join(ROOT, 'src', 'CSweet.UI', 'Localization', 'Resources')
ATTRS = ('Label', 'Title', 'Text', 'HelperText', 'Placeholder', 'placeholder', 'aria-label', 'title', 'alt',
         'ButtonText', 'Tooltip', 'AdornmentAriaLabel', 'ConfirmText', 'CancelText', 'EmptyText', 'Description',
         'Subtitle', 'Caption', 'Heading', 'NoRecordsContent', 'LoadingText')
SKIP_PARENTS = ('code', 'pre', 'kbd', 'script', 'style', 'samp')
BAD_TEXT = re.compile(r'[@{}();=|\\]|&&|=>|::')
WORD = re.compile(r'[A-Za-z]{2,}')

def read(path):
    raw = open(path, 'rb').read()
    bom = raw.startswith(b'\xef\xbb\xbf')
    text = raw.decode('utf-8-sig')
    return text.replace('\r\n', '\n'), '\r\n' in text, bom

def write(path, text, crlf, bom):
    if crlf: text = text.replace('\n', '\r\n')
    data = text.encode('utf-8')
    open(path, 'wb').write((b'\xef\xbb\xbf' if bom else b'') + data)

def all_keys():
    keys = {}
    for area in os.listdir(RES):
        p = os.path.join(RES, area, 'en.json')
        if os.path.exists(p):
            for k, v in json.load(open(p, encoding='utf-8')).items():
                keys[k] = (v, area)
    return keys

def slug(text):
    words = re.findall(r"[A-Za-z0-9]+", text)
    s = ''.join(w[:1].upper() + w[1:] for w in words[:5])
    return s[:40] or 'Text'

class Extractor:
    def __init__(self, area, component, existing, table):
        self.area, self.component, self.existing, self.table = area, component, existing, table
        self.by_text = {v: k for k, v in table.items() if k.startswith(component + '.')}

    def key_for(self, value):
        common = {v: k for k, (v, a) in self.existing.items() if k.startswith('Common.')}
        if value in common: return common[value]
        if value in self.by_text: return self.by_text[value]
        base = f'{self.component}.{slug(value)}'; key = base; n = 2
        while key in self.table or (key in self.existing and self.existing[key][0] != value):
            if self.existing.get(key, (None,))[0] == value: break
            key = f'{base}{n}'; n += 1
        self.table[key] = value; self.by_text[value] = key
        return key

def good(text):
    t = text.strip()
    return t and WORD.search(t) and not BAD_TEXT.search(t) and not t.startswith('//')

def code_start(text):
    m = re.search(r'^@(code|functions)\s*\{', text, re.M)
    return m.start() if m else len(text)

def masked_regions(markup):
    spans = []
    # Razor directives (@inject ILogger<T> Logger) contain generics that look like tags.
    for m in re.finditer(r'^@(?:using|inject|implements|inherits|typeparam|attribute|layout|page|namespace|rendermode|preservewhitespace)\b[^\n]*$', markup, re.M):
        spans.append((m.start(), m.end()))
    for tag in SKIP_PARENTS:
        for m in re.finditer(rf'<{tag}\b[^>]*>.*?</{tag}>', markup, re.S | re.I):
            spans.append((m.start(), m.end()))
    return spans

def inside(spans, i): return any(a <= i < b for a, b in spans)

def process_razor(path, ex, report):
    text, crlf, bom = read(path)
    cut = code_start(text)
    markup, code = text[:cut], text[cut:]
    spans = masked_regions(markup)
    count = 0

    def page_title(m):
        nonlocal count; count += 1
        value = html.unescape(m.group(1).strip()); key = f'{ex.component}.PageTitle'
        ex.table[key] = value
        return f'<PageTitle>@L["{key}"]</PageTitle>'
    markup = re.sub(r'<PageTitle>([^<@]+)</PageTitle>', page_title, markup)
    def repl_text(m):
        nonlocal count
        inner = m.group(1)
        if inside(spans, m.start()) or not good(inner): return m.group(0)
        # Only real tag boundaries: '>' must close a tag (no space before it) and '<' must open one.
        before = m.string[m.start() - 1] if m.start() > 0 else ''
        after = m.string[m.end()] if m.end() < len(m.string) else ''
        if before.isspace() or (after and not (after.isalpha() or after in '/!')): return m.group(0)
        value = html.unescape(' '.join(inner.split()))
        key = ex.key_for(value)
        lead = inner[:len(inner) - len(inner.lstrip())]; trail = inner[len(inner.rstrip()):]
        count += 1
        return f'>{lead}@L["{key}"]{trail}<'
    markup = re.sub(r'>([^<>]+)<', repl_text, markup)

    attr_re = re.compile(r'(?<![\w-])(' + '|'.join(re.escape(a) for a in ATTRS) + r')="([^"]*)"')
    def repl_attr(m):
        nonlocal count
        name, value = m.group(1), m.group(2)
        if inside(spans, m.start()) or not good(value): return m.group(0)
        if name in ('Text', 'Title', 'Description') and not re.search(r'[a-z] |^[A-Z][a-z]+$|[a-z]$', value): return m.group(0)
        key = ex.key_for(html.unescape(value))
        count += 1
        return f'{name}="@L["{key}"]"'
    markup = attr_re.sub(repl_attr, markup)

    code, n = process_code(code, ex)
    count += n
    new = markup + code
    if count and '@inject IAppLocalizer L' not in new and 'IAppLocalizer L ' not in new:
        new = inject(new, '@inject IAppLocalizer L')
    if 'Time.Format(' in new and '@inject UserTime Time' not in new and 'UserTime Time ' not in new:
        new = inject(new, '@inject UserTime Time')
    if report:
        for m in re.finditer(r'>([^<>]*[A-Za-z]{3,}[^<>]*)<', new[:code_start(new)]):
            t = m.group(1).strip()
            if t and '@L[' not in t and not inside(masked_regions(new), m.start()) and WORD.search(t) and not t.startswith('@') and '@' not in t[:1]:
                if not re.fullmatch(r'[@\w.()\[\]"?!:, -]*', t) or ' ' in t:
                    report.append(f'{os.path.relpath(path, ROOT)}: markup "{t[:90]}"')
        for m in re.finditer(r'\$"([^"]*[A-Za-z]{3,}[^"]*)"', new):
            report.append(f'{os.path.relpath(path, ROOT)}: interpolated $"{m.group(1)[:90]}"')
    if new != text:
        write(path, new, crlf, bom)
    return count

MSG_TARGETS = r'(?:_\w*(?:[Ee]rror|[Mm]essage|[Ss]tatus|[Nn]otice|[Ww]arning|[Tt]itle|[Ss]ummary|[Ff]eedback|[Hh]int)\w*|ErrorMessage|StatusMessage)'
def process_code(code, ex):
    n = 0
    def lit(m):
        nonlocal n
        value = json.loads('"' + m.group('v') + '"')
        if not WORD.search(value): return m.group(0)
        n += 1
        return m.group('pre') + f'L["{ex.key_for(value)}"]'
    code = re.sub(r'(?P<pre>Snackbar\.Add\(\s*)"(?P<v>(?:[^"\\]|\\.)*)"', lit, code)
    code = re.sub(r'(?P<pre>\b' + MSG_TARGETS + r'\s*=\s*)"(?P<v>(?:[^"\\]|\\.)*)"(?=\s*;)', lit, code)
    def time(m):
        nonlocal n; n += 1
        return f'Time.Format({m.group(1)}, "{m.group(2)}")'
    code = re.sub(r'(\b[\w.]+(?:\.Value)?)\.ToLocalTime\(\)\.ToString\("([gdtfDT])"\)', time, code)
    return code, n

def process_cs(path, ex):
    text, crlf, bom = read(path)
    new, n = process_code(text, ex)
    if n: write(path, new, crlf, bom)
    return n

def inject(text, line):
    lines = text.split('\n'); idx = 0
    for i, l in enumerate(lines[:40]):
        if l.startswith(('@page', '@using', '@inject', '@layout', '@inherits', '@implements', '@attribute', '@typeparam', '@namespace')):
            idx = i + 1
    lines.insert(idx, line)
    return '\n'.join(lines)

def main():
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    report_on = '--report' in sys.argv
    area, files = args[0], args[1:]
    path = os.path.join(RES, area, 'en.json')
    table = OrderedDict(json.load(open(path, encoding='utf-8'), object_pairs_hook=OrderedDict)) if os.path.exists(path) else OrderedDict(_=f'{area} area.')
    existing = all_keys()
    report = []; total = 0
    for f in files:
        comp = os.path.basename(f).split('.')[0]
        ex = Extractor(area, comp, existing, table)
        n = process_razor(f, ex, report if report_on else None) if f.endswith('.razor') else process_cs(f, ex)
        total += n
        print(f'{n:4d}  {os.path.relpath(f, ROOT)}')
    os.makedirs(os.path.dirname(path), exist_ok=True)
    json.dump(table, open(path, 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
    open(path, 'a', encoding='utf-8').write('\n')
    print(f'{total} replacements, {len(table) - 1} keys in {area}/en.json')
    for r in report: print('  review:', r)

if __name__ == '__main__':
    main()
