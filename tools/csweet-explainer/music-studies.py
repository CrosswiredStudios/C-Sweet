"""Three original synthesized music-direction sketches; no external samples or services.

These audition arrangement, tempo and tone, not a final soundtrack mix.
Run: python tools/csweet-explainer/music-studies.py
"""
from pathlib import Path
import json
import math
import subprocess
import wave
import numpy as np
from scipy.signal import butter, sosfilt, fftconvolve

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / 'assets/readme/explainer/music'
WORK = ROOT / 'artifacts/explainer-review/music'
OUT.mkdir(parents=True, exist_ok=True)
WORK.mkdir(parents=True, exist_ok=True)
SR = 48000
STYLES = [
    dict(id='a-warm-electronica', name='Warm cinematic electronica', bpm=100,
         notes='Soft keys, rounded bass, spacious pads and a restrained beat. Recommended for the C-Sweet brand.'),
    dict(id='b-playful-arcade', name='Playful arcade pop', bpm=116,
         notes='Bright plucks, syncopated bass, crisp drums and a touch of chiptune. More game-forward.'),
    dict(id='c-cinematic-lift', name='Cinematic lift', bpm=88,
         notes='Piano-like keys, swelling synth strings and broad percussion. More emotional and expansive.'),
]

def hz(midi):
    return 440 * 2 ** ((midi - 69) / 12)

def write_wav(path, signal):
    with wave.open(str(path), 'wb') as f:
        f.setnchannels(2)
        f.setsampwidth(2)
        f.setframerate(SR)
        f.writeframes((np.clip(signal, -.999, .999) * 32767).astype('<i2').tobytes())

def mix(track, signal, start, pan=0):
    offset = int(start * SR)
    count = min(len(signal), len(track) - offset)
    if count <= 0:
        return
    track[offset:offset+count, 0] += signal[:count] * math.sqrt((1-pan)/2)
    track[offset:offset+count, 1] += signal[:count] * math.sqrt((1+pan)/2)

def note(track, start, length, midi, level, kind='keys', pan=0):
    t = np.arange(int(length * SR)) / SR
    f = hz(midi)
    end = np.minimum((length-t)/.12, 1)
    if kind == 'pad' or kind == 'strings':
        signal = np.zeros_like(t)
        harmonics = 6 if kind == 'strings' else 3
        for h in range(1, harmonics+1):
            for detune in [-.0016, .0016]:
                signal += np.sin(2*np.pi*f*h*(1+detune)*t) / (h**1.7 * 2)
        env = np.minimum(t/(.8 if kind == 'pad' else .35), 1) * np.minimum((length-t)/.8, 1)
    elif kind == 'bass':
        signal = np.sin(2*np.pi*f*t) + .18*np.sin(2*np.pi*f*2*t) + .06*np.sin(2*np.pi*f*3*t)
        env = (1-np.exp(-t*100)) * np.exp(-t*3) * end
    elif kind == 'chip':
        signal = sum(np.sin(2*np.pi*f*h*t)/h**1.3 for h in [1,3,5,7]) * .65
        env = (1-np.exp(-t*180)) * np.exp(-t*7) * end
    elif kind == 'pluck':
        signal = sum(np.sin(2*np.pi*f*h*t)*np.exp(-t*h*2.8)/h**1.2 for h in range(1,7))
        env = (1-np.exp(-t*200))*np.exp(-t*2)*end
    else:
        signal = sum(np.sin(2*np.pi*f*h*(1+.00009*h*h)*t)*np.exp(-t*(.7+h*.45))/h**1.8 for h in range(1,8))
        env = (1-np.exp(-t*230))*end
    mix(track, signal * env * level, start, pan)

def drum(track, start, kind, level, rng):
    length = {'kick':.4, 'snare':.2, 'hat':.075, 'impact':1.4}[kind]
    t = np.arange(int(length*SR))/SR
    noise = rng.normal(0,1,len(t))
    if kind == 'kick':
        phase = 2*np.pi*(46*t + 90*(1-np.exp(-t*40))/40)
        signal = np.sin(phase)*np.exp(-t*15) + noise*np.exp(-t*220)*.045
    elif kind == 'snare':
        crisp = sosfilt(butter(2, 1900, 'highpass', fs=SR, output='sos'), noise)
        signal = crisp*np.exp(-t*28)*.35 + np.sin(2*np.pi*175*t)*np.exp(-t*36)*.4
    elif kind == 'hat':
        crisp = sosfilt(butter(2, 7000, 'highpass', fs=SR, output='sos'), noise)
        signal = crisp*np.exp(-t*95)
    else:
        low = sosfilt(butter(2, 700, 'lowpass', fs=SR, output='sos'), noise)
        signal = np.sin(2*np.pi*48*t)*np.exp(-t*5)*.6 + low*np.exp(-t*4)
    signal *= np.minimum(t/.002,1)*np.minimum((length-t)/.012,1)
    mix(track, signal*level, start, .12 if kind == 'hat' else 0)

def build(style):
    rng = np.random.default_rng(615 + STYLES.index(style))
    beat = 60/style['bpm']
    duration = beat*32+2.3  # Eight bars, then room for the closing chord.
    tracks = {key:np.zeros((int(duration*SR),2)) for key in ['harmony','melody','rhythm']}
    # D major/add9, B minor7, G major7, Asus4/A; a small recurring motif unifies the comparison.
    chords = [[50,57,61,66],[47,54,57,62],[43,50,54,59],[45,52,57,61]]
    for bar in range(8):
        chord = chords[bar%4]
        start = bar*beat*4
        growing = bar>=2
        for j,pitch in enumerate(chord):
            note(tracks['harmony'],start,beat*4+1,pitch+12,.023 if style['id'][0]!='c' else .034,
                 'strings' if style['id'][0]=='c' else 'pad',(j-1.5)*.25)
        if style['id'][0]=='a':
            for k in range(8):
                pitch=chord[[0,2,1,3,2,1,3,2][k]]+12
                note(tracks['melody'],start+k*beat/2,1.5,pitch,.07 if k%2==0 else .04,'keys',math.sin(k)*.25)
            bass_beats=[0,1.5,2.5] if growing else [0]
        elif style['id'][0]=='b':
            for k in [0,1,3,4,6,7]:
                note(tracks['melody'],start+k*beat/2,.65,chord[[0,2,3,1,2,3,1,2][k]]+24,.055,'pluck',(k%3-1)*.3)
            if bar>=4:
                for offset,pitch in zip([0,.75,1.5,2.5,3.25],[74,78,81,78,76]):
                    note(tracks['melody'],start+offset*beat,.3,pitch,.026,'chip',-.1)
            bass_beats=[0,.75,1.5,2,2.75,3.5]
        else:
            for k,pitch in enumerate(chord):
                note(tracks['melody'],start+k*beat*.5,3,pitch+12,.072,'keys',(k-1.5)*.18)
            if bar>=2:
                for offset,pitch in zip([0,1.5,2.5],[78,76,74] if bar%2==0 else [73,74,78]):
                    note(tracks['melody'],start+offset*beat,beat*1.4,pitch,.045,'strings',.1)
            bass_beats=[0,2] if growing else [0]
        for offset in bass_beats:
            note(tracks['rhythm'],start+offset*beat,.8,chord[0]-12,.13,'bass')
        if growing:
            for offset in ([0,2] if style['id'][0]!='b' else [0,1.5,2,3.5]):
                drum(tracks['rhythm'],start+offset*beat,'kick',.18,rng)
            if style['id'][0]!='c':
                for offset in [1,3]:drum(tracks['rhythm'],start+offset*beat,'snare',.065,rng)
                for k in range(8):drum(tracks['rhythm'],start+k*beat/2,'hat',.025 if k%2 else .015,rng)
            else:
                drum(tracks['rhythm'],start,'impact',.11,rng)
                for offset in [1.5,3.5]:drum(tracks['rhythm'],start+offset*beat,'hat',.018,rng)
    # Final D(add9) resolves each sketch instead of stopping at an arbitrary beat.
    for pitch in [50,57,62,66,69,76]:note(tracks['harmony'],beat*32,2.25,pitch,.04,'keys')
    dry=sum(tracks.values())
    # Short stereo ambience and cross-channel delay, deliberately quiet for narration space.
    melody=tracks['melody']
    for delay,gain in [(beat*.75,.17),(beat*1.5,.08)]:
        count=int(delay*SR)
        dry[count:]+=melody[:-count,::-1]*gain
    ir_t=np.arange(int(SR*1.3))/SR
    ir=rng.normal(0,1,len(ir_t))*np.exp(-ir_t*5)
    ir=sosfilt(butter(2,3500,'lowpass',fs=SR,output='sos'),ir)
    ir/=np.sqrt(np.sum(ir*ir))
    for channel in range(2):
        dry[:,channel]+=fftconvolve((tracks['harmony']+tracks['melody'])[:,channel],np.roll(ir,channel*421))[:len(dry)]*.1
    time=np.arange(len(dry))/SR
    dry*= (np.minimum(time/.08,1)*np.minimum((duration-time)/.8,1))[:,None]
    dry*=.8/max(1,np.max(np.abs(dry)))
    raw=WORK/(style['id']+'.wav')
    write_wav(raw,dry)
    # Equal integrated loudness makes the comparison about style rather than volume.
    measure=subprocess.run(['ffmpeg','-hide_banner','-i',str(raw),'-af','loudnorm=I=-18:TP=-1.5:LRA=9:print_format=json','-f','null','-'],capture_output=True,text=True,check=True).stderr
    stats=json.JSONDecoder().raw_decode(measure[measure.rfind('{'):])[0]
    normalization='loudnorm=I=-18:TP=-1.5:LRA=9:measured_I={input_i}:measured_TP={input_tp}:measured_LRA={input_lra}:measured_thresh={input_thresh}:offset={target_offset}:linear=true'.format(**stats)
    output=OUT/(style['id']+'.mp3')
    subprocess.run(['ffmpeg','-y','-hide_banner','-loglevel','error','-i',str(raw),'-af',normalization,'-ar','48000','-c:a','libmp3lame','-b:a','192k',str(output)],check=True)
    validation=subprocess.run(['ffmpeg','-hide_banner','-i',str(output),'-af','loudnorm=I=-18:TP=-1.5:LRA=9:print_format=json','-f','null','-'],capture_output=True,text=True,check=True).stderr
    final=json.JSONDecoder().raw_decode(validation[validation.rfind('{'):])[0]
    assert abs(float(final['input_i'])+18)<.7
    assert float(final['input_tp'])<=-1
    result={**style,'durationSeconds':round(duration,2),'file':output.name,'originalSynthesizedSketch':True,'vocals':False,'encodedLUFS':final['input_i'],'encodedTruePeakDb':final['input_tp']}
    print(json.dumps(result),flush=True)
    return result

if __name__=='__main__':
    studies=[build(style) for style in STYLES]
    (OUT/'studies.json').write_text(json.dumps(studies,indent=2),encoding='utf-8')
