"""Original procedural score; no recordings, samples, or third-party music.

Editable instrumentation: warm sustained chords, a bell-like arpeggio, sine bass,
quiet percussion and seeded noise transition sweeps. Keep raw stems for remixing.
"""
from pathlib import Path
import json
import math
import subprocess
import wave
import numpy as np

HERE=Path(__file__).resolve().parent
OUT=HERE.parents[1]/'artifacts'/'promo'/'audio'
OUT.mkdir(parents=True,exist_ok=True)
SR=48000

def save(path,signal):
    pcm=(np.clip(signal,-.999,.999)*32767).astype('<i2')
    with wave.open(str(path),'wb') as f:
        f.setnchannels(2);f.setsampwidth(2);f.setframerate(SR);f.writeframes(pcm.tobytes())

def freq(note):return 440*2**((note-69)/12)

def add_note(track,start,length,note,volume,kind='bell',pan=0):
    offset=int(start*SR);count=min(int(length*SR),len(track)-offset)
    if count<=0:return
    t=np.arange(count)/SR;f=freq(note)
    if kind=='pad':
        tone=np.sin(2*np.pi*f*t)+.22*np.sin(2*np.pi*f*2.003*t)+.08*np.sin(2*np.pi*f*3*t)
        env=np.minimum(t/.65,1)*np.minimum((length-t)/1.2,1)
    elif kind=='bass':
        tone=np.sin(2*np.pi*f*t)+.16*np.sin(2*np.pi*f*2*t)
        env=(1-np.exp(-t*40))*np.exp(-t*2.8)
    else:
        tone=np.sin(2*np.pi*f*t)+.18*np.sin(2*np.pi*f*2*t)+.07*np.sin(2*np.pi*f*3.997*t)
        env=(1-np.exp(-t*95))*np.exp(-t*2.6)
    s=tone*env*volume
    track[offset:offset+count,0]+=s*math.sqrt((1-pan)/2)
    track[offset:offset+count,1]+=s*math.sqrt((1+pan)/2)

def build(campaign,format):
    duration=50 if format=='landscape' else 25
    rng=np.random.default_rng({'software':12,'youtube':29,'game':43}[campaign])
    n=duration*SR;t=np.arange(n)/SR
    pads=np.zeros((n,2));pulse=np.zeros((n,2));sfx=np.zeros((n,2))
    beat=60/(96 if campaign!='game' else 90)
    roots={'software':[47,43,50,45],'youtube':[48,45,53,43],'game':[38,34,41,36]}[campaign]
    for bar,start in enumerate(np.arange(0,duration,beat*8)):
        root=roots[bar%4]
        for note in [root+12,root+19,root+22,root+26]:add_note(pads,start,beat*8+.8,note,.037 if campaign!='game' else .043,'pad',pan=(note-root-20)/35)
    for k,start in enumerate(np.arange(0,duration,beat/2)):
        root=roots[int(start/(beat*8))%4]
        if k%8 in [0,2,3,6]:add_note(pulse,start,1.5,root+24+[0,7,10,14,7,0,14,10][k%8],.032,'bell',math.sin(k*.7)*.45)
        if k%4==0:add_note(pulse,start,.6,root,.07 if campaign!='game' else .095,'bass')
        if k%4==0:
            count=min(int(.13*SR),n-int(start*SR));u=np.arange(count)/SR
            kick=np.sin(2*np.pi*(48*u+17*(1-np.exp(-u*35))/35))*np.exp(-u*32)*.095
            pulse[int(start*SR):int(start*SR)+count]+=kick[:,None]
        if k%2:
            count=min(int(.04*SR),n-int(start*SR));noise=rng.normal(0,1,count);noise=np.diff(noise,prepend=0)*np.exp(-np.arange(count)/SR*150)*.005
            pulse[int(start*SR):int(start*SR)+count]+=noise[:,None]
    boundaries=[2,4,6,10,13,16,21] if format=='vertical' else [3,6.5,10,18,24,31,38,44]
    for boundary in boundaries:
        start=boundary-.3;length=.7;offset=int(start*SR);count=int(length*SR);u=np.arange(count)/SR
        env=np.sin(np.pi*u/length)**2
        noise=rng.normal(0,1,count);smooth=np.convolve(noise,np.ones(17)/17,'same')
        sweep=smooth*env*.036+np.sin(2*np.pi*(400*u+160*u*u))*env*.008
        sfx[offset:offset+count,0]+=sweep;sfx[offset:offset+count,1]+=np.roll(sweep,250)
        add_note(sfx,boundary,.9,roots[-1]+36,.025,'bell',.2)
    # Soft delay on the arpeggio creates width; fade the final chord to silence.
    delay=int(.22*SR);pulse[delay:,1]+=pulse[:-delay,0]*.18
    fade=np.minimum(t/1.2,1)*np.minimum((duration-t)/1.5,1)
    stems={'atmosphere':pads*fade[:,None],'pulse':pulse*fade[:,None],'transitions':sfx*fade[:,None]}
    for name,stem in stems.items():save(OUT/f'{campaign}-{format}-{name}.wav',stem)
    raw=OUT/f'{campaign}-{format}-raw.wav';save(raw,np.tanh(sum(stems.values())*1.1))
    command=['ffmpeg','-hide_banner','-i',str(raw),'-af','loudnorm=I=-16:TP=-1:LRA=9:print_format=json','-f','null','-']
    measured=subprocess.run(command,capture_output=True,text=True,check=True).stderr
    stats=json.JSONDecoder().raw_decode(measured[measured.rfind('{'):])[0]
    normalize='loudnorm=I=-16:TP=-1:LRA=9:measured_I={input_i}:measured_TP={input_tp}:measured_LRA={input_lra}:measured_thresh={input_thresh}:offset={target_offset}:linear=true'.format(**stats)
    target=OUT/f'{campaign}-{format}.wav'
    subprocess.run(['ffmpeg','-y','-hide_banner','-loglevel','error','-i',str(raw),'-af',normalize,'-ar',str(SR),'-c:a','pcm_s16le',str(target)],check=True)
    print(f'Composed {campaign}/{format}',flush=True)
    return {'campaign':campaign,'format':format,'duration':duration,'original':True,'seed':{'software':12,'youtube':29,'game':43}[campaign],'sampleRate':SR,'targetLUFS':-16,'targetTruePeak':-1,'normalization':stats}

results=[build(c,f) for c in ['software','youtube','game'] for f in ['landscape','vertical']]
(OUT/'score.json').write_text(json.dumps(results,indent=2))
