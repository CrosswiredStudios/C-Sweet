from pathlib import Path
from PIL import Image, ImageOps, ImageDraw, ImageFont
ROOT=Path(__file__).resolve().parents[2]/'artifacts'/'promo'
QA=ROOT/'qa'
DEST=ROOT/'storyboards'
DEST.mkdir(parents=True,exist_ok=True)
font=ImageFont.truetype('arial.ttf',18)
for campaign in ['software','youtube','game']:
    for format in ['landscape','vertical']:
        order=['hook','chat','plan','team','calendar','approval','report','report-close','end'] if campaign=='youtube' else ['hook','chat','plan','team','board','task','document','evidence','end']
        frames=[(name,QA/f'{campaign}-{format}-{name}.png') for name in order if (QA/f'{campaign}-{format}-{name}.png').exists()]
        w,h=(480,270) if format=='landscape' else (240,427)
        cols=3 if format=='landscape' else 4
        rows=(len(frames)+cols-1)//cols
        sheet=Image.new('RGB',(cols*(w+24)+24,rows*(h+56)+24),'#0c1321')
        draw=ImageDraw.Draw(sheet)
        for i,(name,file) in enumerate(frames):
            x=24+(i%cols)*(w+24);y=24+(i//cols)*(h+56)
            sheet.paste(ImageOps.contain(Image.open(file),(w,h)),(x,y))
            draw.text((x,y+h+10),f'{i+1:02} / {name.replace("-"," ")}',fill='#c7cad4',font=font)
        sheet.save(DEST/f'{campaign}-{format}.png')
        print(f'Storyboard {campaign}/{format}')
    transitions=sorted(QA.glob(f'{campaign}-landscape-transition-*.png'))
    sheet=Image.new('RGB',(3*504+24,((len(transitions)+2)//3)*326+24),'#0c1321')
    draw=ImageDraw.Draw(sheet)
    for i,file in enumerate(transitions):
        x=24+(i%3)*504;y=24+(i//3)*326;sheet.paste(ImageOps.contain(Image.open(file),(480,270)),(x,y));draw.text((x,y+280),file.stem.split('transition-')[1],fill='#c7cad4',font=font)
    sheet.save(DEST/f'{campaign}-transitions.png')
