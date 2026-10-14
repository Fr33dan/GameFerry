// Time left for a whole copy run, from how much of the source has been read: the engine reads each image once,
// front to back, so time follows source bytes far better than it follows bytes written.

const MINIMUM_BYTES = 16 * 1024 * 1024;
const MINIMUM_SECONDS = 15;

export class Estimate {
  /** @param {number[]} sourceSizes Bytes each game's image has to be read. */
  constructor(sourceSizes) {
    this.sizes = [...sourceSizes];
    this.done = this.sizes.map(() => 0);
    this.started = performance.now();
    /** @type {{at: number, bytes: number}[]} */
    this.recent = [];
  }

  /** @param {number} index @param {number} sourceBytes */
  report(index, sourceBytes) {
    this.done[index] = Math.min(sourceBytes, this.sizes[index]);
    const now = performance.now();
    this.recent.push({ at: now, bytes: this.total() });
    while (this.recent.length > 2 && now - this.recent[0].at > 20000) this.recent.shift();
  }

  /** @param {number} index Whether it finished, failed or was skipped, it no longer counts as work left. */
  finish(index) {
    this.sizes[index] = this.done[index];
  }

  total() {
    return this.done.reduce((sum, bytes) => sum + bytes, 0);
  }

  get fraction() {
    const all = this.sizes.reduce((sum, bytes) => sum + bytes, 0);
    return all === 0 ? 0 : Math.min(1, this.total() / all);
  }

  /** Bytes of source read per second over the last 20 seconds or so. */
  get speed() {
    if (this.recent.length < 2) return null;
    const first = this.recent[0];
    const last = this.recent[this.recent.length - 1];
    const seconds = (last.at - first.at) / 1000;
    return seconds >= 2 ? (last.bytes - first.bytes) / seconds : null;
  }

  /** Seconds left for everything, or null until there is enough to go on. */
  get remaining() {
    const elapsed = (performance.now() - this.started) / 1000;
    const done = this.total();
    const left = this.sizes.reduce((sum, bytes) => sum + bytes, 0) - done;
    if (left <= 0) return 0;
    if (done < MINIMUM_BYTES || elapsed < MINIMUM_SECONDS) return null;
    return elapsed * (left / done);
  }

  get elapsed() {
    return (performance.now() - this.started) / 1000;
  }
}

/** @param {number} seconds */
export function formatDuration(seconds) {
  const s = Math.round(seconds);
  if (s < 60) return `${s}s`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}m ${String(s % 60).padStart(2, '0')}s`;
  return `${Math.floor(m / 60)}h ${String(m % 60).padStart(2, '0')}m`;
}

/** @param {number} bytes */
export function formatSize(bytes) {
  const units = ['bytes', 'KB', 'MB', 'GB', 'TB'];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return unit === 0 ? `${value} bytes` : `${value.toFixed(value < 10 ? 2 : 1)} ${units[unit]}`;
}
