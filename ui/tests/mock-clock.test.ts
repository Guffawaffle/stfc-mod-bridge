import { describe, expect, test } from 'vitest';
import { ManualClock } from '../src/mocks/clock';

describe('deterministic manual clock', () => {
  test('deadlines and ties retain insertion order including nested schedules', () => {
    const clock = new ManualClock();
    const delivered: string[] = [];
    clock.schedule(20, () => delivered.push('late'));
    clock.schedule(10, () => { delivered.push('first'); clock.schedule(0, () => delivered.push('nested')); });
    clock.schedule(10, () => delivered.push('second'));
    clock.advanceBy(9);
    expect(delivered).toEqual([]);
    clock.advanceBy(1);
    expect(delivered).toEqual(['first', 'second', 'nested']);
    clock.runAll();
    expect(delivered).toEqual(['first', 'second', 'nested', 'late']);
    expect(clock.now).toBe(20);
    expect(clock.pendingCount).toBe(0);
  });

  test('cancellation and reset remove old tasks and reset elapsed time', () => {
    const clock = new ManualClock();
    let count = 0;
    const cancel = clock.schedule(10, () => { count += 1; });
    cancel(); cancel();
    expect(clock.pendingCount).toBe(0);
    clock.schedule(10, () => { count += 1; });
    clock.advanceBy(5);
    clock.reset();
    expect(clock.now).toBe(0);
    clock.advanceBy(100);
    expect(count).toBe(0);
    clock.schedule(0, () => { count += 1; });
    expect(clock.runNext()).toBe(true);
    expect(clock.runNext()).toBe(false);
    expect(count).toBe(1);
  });

  test('a stale cancellation handle cannot remove a task scheduled after reset', () => {
    const clock = new ManualClock();
    const delivered: string[] = [];
    const staleCancel = clock.schedule(10, () => delivered.push('old'));
    clock.reset();
    clock.schedule(10, () => delivered.push('new-first'));
    clock.schedule(10, () => delivered.push('new-second'));
    staleCancel(); staleCancel();
    expect(clock.pendingCount).toBe(2);
    clock.runAll();
    expect(delivered).toEqual(['new-first', 'new-second']);
  });

  test('bounds reject runaway scheduling, invalid times, and reentrant advances', () => {
    const clock = new ManualClock({ maxTasks: 2 });
    const cancel = clock.schedule(1, () => {});
    clock.schedule(2, () => {});
    expect(() => clock.schedule(3, () => {})).toThrow('mock_clock_task_limit');
    cancel();
    expect(() => clock.schedule(-1, () => {})).toThrow('mock_clock_invalid_time');
    expect(() => clock.advanceBy(0.5)).toThrow('mock_clock_invalid_time');
    clock.advanceBy(1);
    expect(() => clock.advanceTo(0)).toThrow('mock_clock_backwards');
    clock.schedule(0, () => expect(() => clock.advanceBy(1)).toThrow('mock_clock_reentrant_advance'));
    clock.advanceBy(0);
    clock.reset();
    const repeat = () => { clock.schedule(0, repeat); };
    clock.schedule(0, repeat);
    expect(() => clock.advanceBy(0)).toThrow('mock_clock_turn_limit');
    clock.dispose();
    expect(clock.pendingCount).toBe(0);
    expect(() => clock.schedule(0, () => {})).toThrow('mock_clock_disposed');
  });
});
