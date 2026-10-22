"""Original 112-second arrangement of approved B: Playful arcade pop, 116 BPM.
No downloaded recordings, samples or external music services.
"""
from pathlib import Path
import importlib.util
import json
import math
import subprocess
import numpy as np
from scipy.signal import butter, sosfilt, fftconvolve

spec=importlib.util.spec_from_file_location('studies',Path(__file__).with_name('music-studies.py'))
m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)
SR=m.SR;beat=60/116;duration=112
tracks={k:np.zeros((duration*SR,2)) for k in ['harmony','melody','rhythm','effects']}
rng=np.random.default_rng(20261022)
chords=[[50,57,61,66],[47,54,57,62],[43,50,54,59],[45,52,57,61]]
for bar in range(52):
    start=bar*beat*4
    if start>=105:break
    chord=chords[bar%4]
    quiet=88<=start<95.5
    intensity=.55 if start<5 else .7 if start<20 else .85 if start<35 else 1
    if quiet:intensity=.38
    for j,p in enumerate(chord):m.note(tracks['harmony'],start,beat*4+1,p+12,.023*intensity,'pad',(j-1.5)*.3)
    for k in ([0,3,6] if quiet else [0,1,3,4,6,7]):
        m.note(tracks['melody'],start+k*beat/2,.65,chord[[0,2,3,1,2,3,1,2][k]]+24,.055*intensity,'pluck',(k%3-1)*.35)
    if 39<=start<88 or start>=96:
        # Answering chip phrase, with gaps and variations rather than a continuous loop.
        if bar%4 in [0,2,3]:
            motif=[74,78,81,78,76] if bar%2==0 else [74,76,78,81,85]
            for o,p in zip([0,.75,1.5,2.5,3.25],motif):m.note(tracks['melody'],start+o*beat,.32,p,.024,'chip',-.12)
    for o in ([0] if start<5 or quiet else [0,.75,1.5,2,2.75,3.5]):m.note(tracks['rhythm'],start+o*beat,.65,chord[0]-12,.13*intensity,'bass')
    if start>=12 and not quiet:
        for o in [0,1.5,2,3.5]:m.drum(tracks['rhythm'],start+o*beat,'kick',.19*intensity,rng)
        for o in [1,3]:m.drum(tracks['rhythm'],start+o*beat,'snare',.075*intensity,rng)
        for k in range(8):m.drum(tracks['rhythm'],start+k*beat/2,'hat',(.027 if k%2 else .016)*intensity,rng)
        if bar%4==3:
            for k in range(4):m.drum(tracks['rhythm'],start+(3+k/4)*beat,'hat',.018,rng)
# Film-timed transition swells and subtle clicks; deliberately leave room to read.
for at in [10,17,24.3,31,39,49,61,72,88,96,105]:
    length=.65 if at!=96 else 1.6
    t=np.arange(int(length*SR))/SR
    noise=sosfilt(butter(2,[1200,6000],btype='bandpass',fs=SR,output='sos'),rng.normal(0,1,len(t)))
    env=np.sin(np.pi*t/length)**2*(t/length)
    m.mix(tracks['effects'],noise*env*(.028 if at!=96 else .065),at-length,-.12)
    m.drum(tracks['effects'],at,'impact',.045 if at!=96 else .15,rng)
    for j,p in enumerate([74,78,81]):m.note(tracks['effects'],at+j*.055,.45,p,.015 if at!=96 else .035,'pluck',(j-1)*.3)
# Reveal lands exactly at 96 seconds, independent of the underlying bar grid.
for j,p in enumerate([50,57,62,66,69,74,78,81]):m.note(tracks['harmony'],96,4,p,.035,'strings',(j-3.5)*.15)
for j in range(16):m.drum(tracks['effects'],94+j*.125,'hat',.012+j*.0015,rng)
# Let the closing question breathe, then resolve the motif under the brand.
for j,p in enumerate([62,66,69,74,78]):m.note(tracks['melody'],105+j*.22,2.2,p,.058,'keys',(j-2)*.2)
for j,p in enumerate([50,57,62,66,69,76]):m.note(tracks['harmony'],107.2,4.7,p,.034,'pad',(j-2.5)*.2)
for o,p in zip([0,.5,1,2],[81,78,76,74]):m.note(tracks['melody'],107.2+o*.52,1.7,p,.039,'pluck')
dry=sum(tracks.values())
for delay,gain in [(beat*.75,.17),(beat*1.5,.08)]:
    n=int(delay*SR);dry[n:]+=tracks['melody'][:-n,::-1]*gain
t=np.arange(int(SR*1.2))/SR
ir=sosfilt(butter(2,3500,'lowpass',fs=SR,output='sos'),rng.normal(0,1,len(t))*np.exp(-t*5));ir/=np.sqrt(np.sum(ir*ir))
for channel in range(2):dry[:,channel]+=fftconvolve((tracks['melody']+tracks['harmony'])[:,channel],np.roll(ir,channel*421))[:len(dry)]*.1
t=np.arange(len(dry))/SR;dry*=(np.minimum(t/.08,1)*np.minimum((duration-t)/2,1))[:,None]
raw=m.WORK/'final-raw.wav';m.write_wav(raw,dry)
def measure(file):
    result=subprocess.run(['ffmpeg','-hide_banner','-i',str(file),'-af','loudnorm=I=-16:TP=-1.8:LRA=10:print_format=json','-f','null','-'],capture_output=True,text=True,check=True).stderr
    return json.JSONDecoder().raw_decode(result[result.rfind('{'):])[0]
stats=measure(raw)
normal='loudnorm=I=-16:TP=-1.8:LRA=10:measured_I={input_i}:measured_TP={input_tp}:measured_LRA={input_lra}:measured_thresh={input_thresh}:offset={target_offset}:linear=true'.format(**stats)
out=m.WORK/'final-score.wav'
subprocess.run(['ffmpeg','-y','-hide_banner','-loglevel','error','-i',str(raw),'-af',normal,'-ar','48000',str(out)],check=True)
report={'direction':'B · Playful arcade pop','bpm':116,'duration':112,'originalSynthesis':True,'externalSamples':False,'master':measure(out)}
(m.WORK/'final-score.json').write_text(json.dumps(report,indent=2))
print(json.dumps(report,indent=2))
