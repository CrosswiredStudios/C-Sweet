"""Package review artwork and verify the encoded animatic; no external services."""
from pathlib import Path
import json
import subprocess
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / 'assets/readme/explainer'
QA = ROOT / 'artifacts/explainer-review'
MOVIE = OUT / 'from-idea-to-play.animatic.mp4'

def run(*args):
    return subprocess.run(args, check=True, capture_output=True, text=True)

report = json.loads((QA / 'verification.json').read_text(encoding='utf-8'))
font = ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf', 20)
small = ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf', 17)
sheet = Image.new('RGB', (1500, 1650), '#eeeaf3')
d = ImageDraw.Draw(sheet)
d.text((30, 20), 'C-Sweet / From an idea to play / Animatic 01 / 96 seconds', font=font, fill='#30263f')
for i, scene in enumerate(report['scenes']):
    x = 30 + (i % 3) * 490
    y = 70 + (i // 3) * 310
    image = Image.open(QA / f"{i+1:02}-{scene['id']}.png").convert('RGB')
    image.thumbnail((460, 259))
    sheet.paste(image, (x, y))
    label = f"{scene['start']:02}–{scene['end']:02}s · {scene['title']}"
    d.text((x, y + 266), label, font=small, fill='#30263f')
sheet.save(OUT / 'storyboard.jpg', quality=92)

# A 12-second invitation, rather than a large looping copy of the full narrative.
# Reuses settled moments of the actual film; the adjacent README identifies it as a teaser.
filters = (
    '[0:v]split=4[a][b][c][d];'
    '[a]trim=start=1:end=4,setpts=PTS-STARTPTS[a1];'
    '[b]trim=start=38:end=41,setpts=PTS-STARTPTS[b1];'
    '[c]trim=start=82:end=85,setpts=PTS-STARTPTS[c1];'
    '[d]trim=start=91:end=94,setpts=PTS-STARTPTS[d1];'
    '[a1][b1][c1][d1]concat=n=4:v=1:a=0,fps=8,scale=800:-1:flags=lanczos,'
    'split[p][q];[p]palettegen=stats_mode=diff[pal];[q][pal]paletteuse=dither=bayer:bayer_scale=3'
)
run('ffmpeg', '-y', '-hide_banner', '-loglevel', 'error', '-i', str(MOVIE),
    '-filter_complex', filters, '-loop', '0', str(OUT / 'teaser.gif'))
probe = json.loads(run('ffprobe', '-v', 'error', '-show_streams', '-show_format', '-of', 'json', str(MOVIE)).stdout)
video = next(s for s in probe['streams'] if s['codec_type'] == 'video')
assert video['width'] == 1280 and video['height'] == 720
assert video['codec_name'] == 'h264' and video['pix_fmt'] == 'yuv420p'
assert video['color_range'] == 'tv'
assert video['r_frame_rate'] == '24/1' and int(video['nb_frames']) == 2304
assert abs(float(probe['format']['duration']) - 96) < .05
decode = run('ffmpeg', '-v', 'error', '-i', str(MOVIE), '-f', 'null', '-')
assert not decode.stderr, decode.stderr
binary = MOVIE.read_bytes()
assert binary.index(b'moov') < binary.index(b'mdat'), 'MP4 lacks faststart'
report['encoded'] = {'codec': video['codec_name'], 'pixelFormat': video['pix_fmt'],
                     'frames': int(video['nb_frames']), 'duration': probe['format']['duration'],
                     'faststart': True, 'fullDecodeErrors': 0,
                     'mp4Bytes': MOVIE.stat().st_size, 'teaserBytes': (OUT/'teaser.gif').stat().st_size}
(QA / 'verification.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
print(json.dumps(report['encoded'], indent=2))
