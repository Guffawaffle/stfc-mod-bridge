/** Manually advanced elapsed time. Equal deadlines run in insertion order. */
export class ManualClock {
  readonly #maxTasks: number;
  #now = 0;
  #sequence = 0;
  #disposed = false;
  #advancing = false;
  #tasks = new Map<number, { due: number; callback: () => void }>();

  constructor(options: { maxTasks?: number } = {}) {
    this.#maxTasks = options.maxTasks ?? 1024;
    positiveInteger(this.#maxTasks);
    if (this.#maxTasks > 65536) throw new Error('mock_clock_invalid_limit');
  }

  get now(): number { return this.#now; }
  get pendingCount(): number { return this.#tasks.size; }
  get disposed(): boolean { return this.#disposed; }

  schedule(delayMs: number, callback: () => void): () => void {
    this.#assertActive();
    elapsed(delayMs);
    if (this.#tasks.size >= this.#maxTasks) throw new Error('mock_clock_task_limit');
    const due = this.#now + delayMs;
    elapsed(due);
    const id = ++this.#sequence;
    if (!Number.isSafeInteger(id)) throw new Error('mock_clock_sequence_limit');
    this.#tasks.set(id, { due, callback });
    return () => { this.#tasks.delete(id); };
  }

  advanceBy(milliseconds: number): void {
    elapsed(milliseconds);
    this.advanceTo(this.#now + milliseconds);
  }

  advanceTo(deadline: number): void {
    this.#assertActive();
    elapsed(deadline);
    if (deadline < this.#now) throw new Error('mock_clock_backwards');
    if (this.#advancing) throw new Error('mock_clock_reentrant_advance');
    this.#advancing = true;
    try {
      let executed = 0;
      for (;;) {
        const next = this.#next();
        if (!next || next.task.due > deadline) break;
        if (++executed > this.#maxTasks) throw new Error('mock_clock_turn_limit');
        this.#tasks.delete(next.id);
        this.#now = next.task.due;
        next.task.callback();
      }
      this.#now = deadline;
    } finally {
      this.#advancing = false;
    }
  }

  runNext(): boolean {
    this.#assertActive();
    const next = this.#next();
    if (!next) return false;
    this.advanceTo(next.task.due);
    return true;
  }

  runAll(maxTasks = this.#maxTasks): void {
    positiveInteger(maxTasks);
    if (maxTasks > 65536) throw new Error('mock_clock_invalid_limit');
    let turns = 0;
    while (this.#tasks.size) {
      if (++turns > maxTasks) throw new Error('mock_clock_run_limit');
      this.runNext();
    }
  }

  reset(): void {
    this.#assertActive();
    if (this.#advancing) throw new Error('mock_clock_reset_during_advance');
    this.#tasks.clear();
    this.#now = 0;
    // A cancellation handle from the previous context must never name a new task.
  }

  dispose(): void {
    this.#tasks.clear();
    this.#disposed = true;
  }

  #assertActive(): void {
    if (this.#disposed) throw new Error('mock_clock_disposed');
  }

  #next(): { id: number; task: { due: number; callback: () => void } } | undefined {
    let result: { id: number; task: { due: number; callback: () => void } } | undefined;
    for (const [id, task] of this.#tasks) {
      if (!result || task.due < result.task.due || (task.due === result.task.due && id < result.id)) {
        result = { id, task };
      }
    }
    return result;
  }
}

function elapsed(value: number): void {
  if (!Number.isSafeInteger(value) || value < 0) throw new Error('mock_clock_invalid_time');
}

function positiveInteger(value: number): void {
  if (!Number.isSafeInteger(value) || value < 1) throw new Error('mock_clock_invalid_limit');
}
