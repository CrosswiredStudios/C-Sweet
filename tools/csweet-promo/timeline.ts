import type { Shot } from './manifest.ts';
export const transitionDuration = .72;
export const clamp = (v:number,lo=0,hi=1) => Math.max(lo,Math.min(hi,v));
export const ease = (v:number) => { const x=clamp(v); return x*x*(3-2*x); };
export function atTime(shots:Shot[],time:number) {
    const t=clamp(time,0,shots.at(-1)!.end-1e-7);
    const index=shots.findIndex(s=>t>=s.start&&t<s.end);
    const shot=shots[index];
    return { time:t,index,shot,local:t-shot.start,progress:clamp((t-shot.start)/(shot.end-shot.start)),transition:index?ease((t-shot.start)/transitionDuration):1,previous:index?shots[index-1]:undefined };
}
