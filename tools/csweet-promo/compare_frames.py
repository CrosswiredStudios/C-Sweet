"""Compare full-resolution decoded holds/transitions with the frame-aligned compositor captures."""
from pathlib import Path
from PIL import Image, ImageOps, ImageDraw, ImageFont
import numpy as np
import json

ROOT = Path(__file__).resolve().parents[2] / 'artifacts' / 'promo'
QA = ROOT / 'qa'
DEST = QA / 'encoded-contact-sheets'
DEST.mkdir(exist_ok=True)
font = ImageFont.truetype('arial.ttf', 18)
checks = []
for campaign in ['software', 'youtube', 'game']:
    for format in ['landscape', 'vertical']:
        order = ['hook', 'chat', 'plan', 'team', 'calendar', 'approval', 'report', 'report-close', 'end'] if campaign == 'youtube' else ['hook', 'chat', 'plan', 'team', 'board', 'task', 'document', 'evidence', 'end']
        for kind in ['hold', 'transition']:
            files = [(name, QA / f'{campaign}-{format}-encoded-{kind}-{name}.png') for name in order if (QA / f'{campaign}-{format}-encoded-{kind}-{name}.png').exists()]
            width, height = (480, 270) if format == 'landscape' else (240, 427)
            cols = 3 if format == 'landscape' else 4
            sheet = Image.new('RGB', (cols * (width + 24) + 24, ((len(files) + cols - 1) // cols) * (height + 56) + 24), '#0c1321')
            draw = ImageDraw.Draw(sheet)
            for i, (name, file) in enumerate(files):
                image = Image.open(file).convert('RGB')
                reference = QA / f'{campaign}-{format}-{name if kind == "hold" else "transition-" + name}.png'
                original = Image.open(reference).convert('RGB')
                if original.size != image.size:
                    raise RuntimeError(f'{file.name}: incorrect decoded dimensions')
                a, b = np.asarray(image, dtype=np.float32), np.asarray(original, dtype=np.float32)
                rmse = float(np.sqrt(np.mean((a - b) ** 2)))
                # JPEG input + H.264 are lossy; require close correspondence across every full-size pixel.
                if rmse > 5:
                    raise RuntimeError(f'{file.name}: compositor/export RMSE {rmse:.3f} exceeds 5/255')
                checks.append({'frame': file.name, 'fullResolutionRMSE': round(rmse, 3), 'comparedPixels': image.width * image.height})
                x, y = 24 + (i % cols) * (width + 24), 24 + (i // cols) * (height + 56)
                sheet.paste(ImageOps.contain(image, (width, height)), (x, y))
                draw.text((x, y + height + 10), name, fill='#c7cad4', font=font)
            sheet.save(DEST / f'{campaign}-{format}-{kind}.png')
(QA / 'encoded-visual-comparison.json').write_text(json.dumps(checks, indent=2))
print(f'Compared {len(checks)} decoded full-resolution frames; max RMSE {max(c["fullResolutionRMSE"] for c in checks):.3f}/255')
