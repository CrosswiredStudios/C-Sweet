"""Package README previews and validate both finished marketing exports."""
from pathlib import Path
import json
import subprocess
from PIL import Image, ImageDraw, ImageFont
ROOT=Path(__file__).resolve().parents[2];OUT=ROOT/'assets/readme/explainer';QA=ROOT/'artifacts/explainer-final'
def run(*args):return subprocess.run(args,check=True,capture_output=True,text=True)
report=json.loads((QA/'verification.json').read_text(encoding='utf-8'))
sheet=Image.new('RGB',(1500,1320),'#f3eff7');d=ImageDraw.Draw(sheet)
font=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',17)
d.text((30,20),'C-Sweet / From an idea to play / Final film / 112 seconds',font=font,fill='#30243f')
for i,s in enumerate(report['scenes']):
    x=30+i%3*490;y=65+i//3*310
    im=Image.open(QA/f"{i+1:02}-{s['id']}.png");im.thumbnail((460,259));sheet.paste(im,(x,y))
    label=f"{s['start']:02}–{s['end']:02}s · {s['title']}"
    if d.textlength(label,font=font)>460:label=f"{s['start']:02}–{s['end']:02}s · {s['id'].title()}"
    d.text((x,y+267),label,font=font,fill='#30243f')
sheet.save(OUT/'final-storyboard.jpg',quality=92)
filters='[0:v]split=4[a][b][c][d];[a]trim=start=1:end=4,setpts=PTS-STARTPTS[a1];[b]trim=start=57:end=60,setpts=PTS-STARTPTS[b1];[c]trim=start=98:end=101,setpts=PTS-STARTPTS[c1];[d]trim=start=108:end=111,setpts=PTS-STARTPTS[d1];[a1][b1][c1][d1]concat=n=4:v=1:a=0,fps=10,scale=800:-1:flags=lanczos,split[p][q];[p]palettegen=stats_mode=diff[pal];[q][pal]paletteuse=dither=bayer:bayer_scale=3'
run('ffmpeg','-y','-hide_banner','-loglevel','error','-i',str(OUT/'from-idea-to-play.mp4'),'-filter_complex',filters,'-loop','0',str(OUT/'final-teaser.gif'))
results=[]
for name,w,h,duration in [('from-idea-to-play.mp4',1920,1080,112),('from-idea-to-play.vertical.mp4',1080,1920,30)]:
    file=OUT/name;probe=json.loads(run('ffprobe','-v','error','-show_streams','-show_format','-of','json',str(file)).stdout)
    v=next(s for s in probe['streams'] if s['codec_type']=='video');a=next(s for s in probe['streams'] if s['codec_type']=='audio')
    assert (v['width'],v['height'],v['codec_name'],v['pix_fmt'],v['color_range'],v['r_frame_rate'])==(w,h,'h264','yuv420p','tv','30/1')
    assert int(v['nb_frames'])==duration*30
    assert a['codec_name']=='aac' and a['sample_rate']=='48000' and a['channels']==2
    assert abs(float(probe['format']['duration'])-duration)<.05
    assert not run('ffmpeg','-v','error','-i',str(file),'-f','null','-').stderr
    binary=file.read_bytes();assert binary.index(b'moov')<binary.index(b'mdat')
    loudness=run('ffmpeg','-hide_banner','-i',str(file),'-af','loudnorm=I=-16:TP=-1.5:LRA=10:print_format=json','-f','null','-').stderr
    stats=json.JSONDecoder().raw_decode(loudness[loudness.rfind('{'):])[0]
    assert float(stats['input_tp'])<=-1
    results.append(dict(file=name,width=w,height=h,duration=duration,frames=int(v['nb_frames']),audio='AAC stereo 48 kHz',lufs=stats['input_i'],truePeakDb=stats['input_tp'],bytes=file.stat().st_size,fullDecodeErrors=0,faststart=True))
report['encoded']=results;report['teaserBytes']=(OUT/'final-teaser.gif').stat().st_size
(QA/'verification.json').write_text(json.dumps(report,indent=2))
(OUT/'delivery.json').write_text(json.dumps({'exports':results,'music':'Original B · Playful arcade pop · 116 BPM','sampleWorkflow':True,'realAppComponents':True,'teaserBytes':report['teaserBytes']},indent=2))
print(json.dumps(results,indent=2));print('README teaser bytes:',report['teaserBytes'])
