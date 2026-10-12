import test from 'node:test';
import assert from 'node:assert/strict';
import { campaigns,formats,shotsFor } from './manifest.ts';
import { atTime,transitionDuration } from './timeline.ts';

for(const c of campaigns)for(const format of ['landscape','vertical'] as const){
    test(`${c.id}/${format}: exact duration and continuous frame coverage`,()=>{
        const shots=shotsFor(c,format);assert.equal(shots[0].start,0);assert.equal(shots.at(-1)!.end,formats[format].duration);
        for(let i=1;i<shots.length;i++)assert.equal(shots[i].start,shots[i-1].end);
        for(let frame=0;frame<formats[format].duration*30;frame++){const time=frame/30;const s=atTime(shots,time);assert.ok(s.shot.start<=time&&s.shot.end>time);assert.ok(s.transition>=0&&s.transition<=1);}
        assert.equal(atTime(shots,-1).index,0);assert.equal(atTime(shots,999).shot.id,'end');
    });
    test(`${c.id}/${format}: transitions settle before each readable hold`,()=>{
        const shots=shotsFor(c,format);for(const shot of shots.slice(1)){assert.equal(atTime(shots,shot.start).transition,0);assert.ok(Math.abs(atTime(shots,shot.start+transitionDuration).transition-1)<1e-8);assert.ok(shot.end-shot.start>transitionDuration+1);}
    });
}
test('YouTube contains no public upload outcome or generated video scene',()=>{
    const c=campaigns.find(c=>c.id==='youtube')!;for(const f of ['landscape','vertical'] as const){const shots=shotsFor(c,f);assert.ok(shots.some(s=>s.id==='approval'));assert.ok(shots.some(s=>s.id==='report'));assert.ok(!shots.some(s=>/upload|publish|generated video/i.test(s.heading+s.detail)));}
});
