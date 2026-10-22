import assert from 'node:assert/strict';
import {PrismGame,gameAt} from './game.mjs';
const g=new PrismGame();
for(let i=0;i<50;i++)g.update(1/120,1);
assert(g.paddle>900,'Keyboard input moves the paddle');
g.target=1192;for(let i=0;i<100;i++)g.update(1/120,1);assert.equal(g.paddle,1192,'Paddle stays inside arena');
g.reset();g.ball={x:100,y:160,vx:0,vy:-400};g.update(1/120);assert.equal(g.hits,1);assert.equal(g.score,100);assert.equal(g.bricks.filter(b=>b.alive).length,49);
g.reset();g.ball={x:640,y:760,vx:0,vy:400};g.update(1/120);assert.equal(g.lives,2);assert.equal(g.ball.y,570);
g.lives=1;g.ball.y=761;g.ball.vy=400;g.update(1/120);assert.equal(g.status,'over');const stopped=g.snapshot();g.update(1);assert.deepEqual(g.snapshot(),stopped,'Game over stops simulation');
g.reset();assert.equal(g.score,0);assert.equal(g.lives,3);assert.equal(g.particles.length,0);assert.equal(g.bricks.filter(b=>b.alive).length,50);
g.bricks.forEach(b=>b.alive=false);g.update(1/120);assert.equal(g.status,'won');
const a=gameAt(18);gameAt(2);assert.deepEqual(gameAt(18),a,'Film gameplay is deterministic');assert(a.hits>5&&a.lives===3);
const auto=new PrismGame(true);for(let i=0;i<120*180&&auto.status==='playing';i++){auto.update(1/120);assert(Number.isFinite(auto.ball.x)&&Number.isFinite(auto.ball.y));assert(auto.paddle>=88&&auto.paddle<=1192);}
console.log('Game checks passed: input, boundaries, collisions, scoring, lives, game over, restart, win, deterministic reveal and long simulation.');
