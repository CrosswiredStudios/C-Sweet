class StarGame {
  static SPEED = 5;
  constructor() { this.reset(); }
  reset() { this.x = 40; this.y = 180; this.score = 0; this.won = false; this.placeStar(); }
  placeStar() { this.star = {x: 80 + (this.score * 97 + 180) % 480, y: 60 + (this.score * 73 + 100) % 240}; }
  step(dx, dy) {
    if (this.won) return;
    const length = Math.max(1, Math.hypot(dx, dy));
    this.x = Math.max(14, Math.min(626, this.x + dx / length * StarGame.SPEED));
    this.y = Math.max(14, Math.min(346, this.y + dy / length * StarGame.SPEED));
    if (Math.hypot(this.x-this.star.x, this.y-this.star.y) < 23) {
      this.score++; this.won = this.score === 10; if (!this.won) this.placeStar();
    }
  }
}
if (typeof module !== 'undefined') module.exports = StarGame;
else globalThis.StarGame = StarGame;
