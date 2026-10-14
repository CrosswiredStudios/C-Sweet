"""Render the README using CommonMark + tables, with GitHub-like light/dark previews."""
from pathlib import Path
import re
from markdown_it import MarkdownIt

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT/'artifacts'/'readme'
OUT.mkdir(parents=True,exist_ok=True)
markdown = (ROOT/'README.md').read_text(encoding='utf-8')
parser = MarkdownIt('commonmark', {'html':True}).enable('table')
tokens = parser.parse(markdown)
seen = {}
for i, token in enumerate(tokens):
    if token.type == 'heading_open':
        heading = tokens[i+1].content.lower()
        slug = re.sub(r'[^\w\- ]', '', heading).replace(' ', '-')
        count = seen.get(slug,0)
        seen[slug] = count+1
        token.attrSet('id',slug+(f'-{count}' if count else ''))
html = parser.renderer.render(tokens,parser.options,{})
html = re.sub(r'(src|srcset|href)="(?!https?:|#|/)([^"]+)"', lambda m:f'{m[1]}="/{m[2]}"',html)
css = '''
*{box-sizing:border-box}body{margin:0;background:#fff;color:#1f2328;font:16px/1.6 -apple-system,BlinkMacSystemFont,"Segoe UI",Arial,sans-serif}.markdown-body{max-width:1012px;margin:32px auto;padding:40px;border:1px solid #d1d9e0;border-radius:8px;overflow:hidden}h1,h2{line-height:1.25;border-bottom:1px solid #d1d9e0;padding-bottom:.3em;margin:24px 0 16px}h1{font-size:32px}h2{font-size:24px}h3{font-size:20px;margin:24px 0 16px}p,ul,ol,table,blockquote,details{margin:0 0 16px}a{color:#0969da;text-decoration:none}a:hover{text-decoration:underline}img{max-width:100%;height:auto}picture{display:block;margin:20px 0}picture img{display:block;width:100%}blockquote{padding:0 1em;border-left:.25em solid #d1d9e0;color:#59636e}table{display:block;width:max-content;max-width:100%;border-spacing:0;border-collapse:collapse;overflow:auto}th,td{padding:6px 13px;border:1px solid #d1d9e0;vertical-align:top}tr:nth-child(2n){background:#f6f8fa}pre{padding:16px;background:#f6f8fa;border-radius:6px;overflow:auto;line-height:1.45}pre code{font-size:85%;padding:0;background:none}code{font-family:Consolas,monospace;font-size:85%;padding:.2em .4em;background:#818b981f;border-radius:6px}details summary{cursor:pointer;margin:10px 0}details[open] summary{margin-bottom:16px}.dark{background:#0d1117;color:#f0f6fc}.dark .markdown-body{border-color:#3d444d}.dark h1,.dark h2{border-color:#3d444d}.dark a{color:#4493f8}.dark th,.dark td{border-color:#3d444d}.dark tr:nth-child(2n),.dark pre{background:#151b23}.dark blockquote{border-color:#3d444d;color:#9198a1}@media(max-width:600px){.markdown-body{padding:20px;margin:0;border:0;border-radius:0}body{font-size:15px}h1{font-size:28px}h2{font-size:22px}th,td{min-width:120px}pre{padding:12px}}
'''
for theme in ['light','dark']:
    (OUT/f'README-{theme}.html').write_text(f'<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>C-Sweet README · {theme}</title><style>{css}</style><body class="{theme}"><article class="markdown-body">{html}</article></body></html>',encoding='utf-8')
print('Built light/dark README previews.')
