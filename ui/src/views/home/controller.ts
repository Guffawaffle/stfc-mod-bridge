import { canonicalData, type CallOptions, type DeepReadonly } from '../../client';
import { BridgeFacade } from '../../state';
import type { ActionId, ActionProjection, MutationIntent, Observation, SessionBinding, StoreMode, UnrecognizedRuntimeChoice } from '../../generated/protocol';
import { sessions, targetMatches } from './presentation';

export interface HomeState { readonly busy: boolean; readonly target?: DeepReadonly<Observation>; readonly launch?: DeepReadonly<ActionProjection>;
  readonly focus: Readonly<Record<string, DeepReadonly<ActionProjection>>>; readonly notice: string; }
/** Scoped read projections; WorkContext remains the only selected target. */
export class HomeController {
  private current: HomeState = Object.freeze({ busy: false, focus: Object.freeze({}), notice: '' });
  private generation = 0;
  private active = false;
  private disposed = false;
  private key = '';
  private abort = new AbortController();
  private stop: () => void;
  private sessionReads = new Map<string, number>();
  private listeners = new Set<(state: HomeState) => void>();
  constructor(readonly facade: BridgeFacade) {
    this.stop = facade.subscribe(state => {
      const key = canonicalData([state.work.selector ?? null, state.observations.cursor?.hostEpoch ?? null, state.observations.confidence,
        state.observations.snapshot?.installations ?? null, state.observations.snapshot?.profiles ?? null, state.observations.snapshot?.sessions ?? null]);
      if (key !== this.key) {
        this.key = key; this.generation++; this.abort.abort(); this.abort = new AbortController();
        this.sessionReads.clear();
        this.set({ busy: false, focus: Object.freeze({}), notice: '' });
        if (this.active && state.work.selector && state.observations.confidence !== 'stale') void this.inspectTarget();
      }
    });
  }
  get state(): HomeState { return this.current; }
  subscribe(listener: (state: HomeState) => void): () => void { this.listeners.add(listener); listener(this.state); return () => this.listeners.delete(listener); }
  private set(value: HomeState): void { this.current = Object.freeze(value); if (!this.disposed) for (const listener of this.listeners) listener(this.current); }
  private async observe<T>(execute: (options: CallOptions) => Promise<T>, options: CallOptions): Promise<T> {
    const lifecycle = this.abort.signal, controller = new AbortController(), abort = () => controller.abort();
    lifecycle.addEventListener('abort', abort, { once: true }); options.signal?.addEventListener('abort', abort, { once: true });
    if (lifecycle.aborted || options.signal?.aborted) controller.abort();
    try { return await execute({ ...options, signal: controller.signal }); }
    finally { lifecycle.removeEventListener('abort', abort); options.signal?.removeEventListener('abort', abort); }
  }
  start(): void { if (this.disposed || this.active) return; this.active = true; if (this.facade.work.state.selector) void this.inspectTarget(); }
  async inspectTarget(options: CallOptions = {}): Promise<void> {
    const selector = this.facade.work.state.selector;
    if (this.disposed || !selector || this.current.busy || this.facade.work.observations.state.confidence === 'stale') return;
    const generation = this.generation, epoch = this.facade.work.observations.state.cursor?.hostEpoch;
    this.set({ ...this.current, busy: true, launch: undefined, notice: '' });
    const outcome = await this.observe(value => this.facade.client.query('resolve_target', { target: selector }, value), options);
    if (this.disposed || generation !== this.generation || epoch !== this.facade.work.observations.state.cursor?.hostEpoch) return;
    if (outcome.kind !== 'result') { this.set({ ...this.current, busy: false, notice: 'Target observation is unavailable. Refresh before reviewing an action.' }); return; }
    const target = outcome.value.target;
    if (target.status !== 'observed' || !targetMatches(selector, target.value)) {
      this.set({ ...this.current, busy: false, target, notice: target.status === 'observed' ? 'The target observation conflicts with the requested selection.' : 'The selected target is not confirmed.' }); return;
    }
    this.facade.work.bindTarget(target.value);
    const action: ActionId = selector.profile.kind === 'ordinary' ? 'launch_ordinary' : 'launch_isolated';
    const result = await this.observe(value => this.facade.client.query('get_actions', { actions: [action], scope: { kind: 'target', target: target.value } }, value), options);
    if (this.disposed || generation !== this.generation || epoch !== this.facade.work.observations.state.cursor?.hostEpoch) return;
    const candidates = result.kind === 'result' ? result.value.filter(row => row.action === action) : [];
    this.set({ ...this.current, busy: false, target, launch: candidates.length === 1 ? candidates[0] : undefined,
      notice: candidates.length === 1 ? '' : 'Launch availability was not confirmed.' });
  }
  async inspectSession(binding: SessionBinding | DeepReadonly<SessionBinding>, options: CallOptions = {}): Promise<void> {
    if (this.disposed || this.facade.work.observations.state.confidence === 'stale') return;
    const captured = sessions(this.facade.work.observations.state).find(row => canonicalData(row.binding) === canonicalData(binding));
    if (!captured) return;
    const generation = this.generation;
    const read = (this.sessionReads.get(captured.binding.sessionId) ?? 0) + 1; this.sessionReads.set(captured.binding.sessionId, read);
    const focus = { ...this.current.focus }; delete focus[captured.binding.sessionId]; this.set({ ...this.current, focus: Object.freeze(focus) });
    const result = await this.observe(value => this.facade.client.query('get_actions', { actions: ['focus_session'], scope: { kind: 'session', session: captured.binding } }, value), options);
    if (this.disposed || generation !== this.generation || this.sessionReads.get(captured.binding.sessionId) !== read
      || !sessions(this.facade.work.observations.state).some(row => canonicalData(row.binding) === canonicalData(captured.binding))) return;
    const candidates = result.kind === 'result' ? result.value.filter(row => row.action === 'focus_session') : [];
    this.set({ ...this.current, focus: Object.freeze({ ...this.current.focus, ...(candidates.length === 1 ? { [captured.binding.sessionId]: candidates[0] } : {}) }),
      notice: candidates.length === 1 ? this.current.notice : 'Focus availability was not confirmed.' });
  }
  launchIntent(storeMode?: StoreMode, choice: UnrecognizedRuntimeChoice = 'reject'): DeepReadonly<MutationIntent> | undefined {
    const target = this.facade.work.state.selector;
    if (!target || this.current.launch?.availability.status !== 'available' || this.facade.work.observations.state.confidence === 'stale') return undefined;
    const consent = choice === 'reject' ? {} : { unrecognizedRuntimeChoice: choice };
    if (target.profile.kind === 'ordinary') return Object.freeze({ kind: 'launch_ordinary', input: Object.freeze({ target: Object.freeze({ installation: target.installation, profile: target.profile }), ...consent }) });
    if (!storeMode) return undefined;
    return Object.freeze({ kind: 'launch_isolated', input: Object.freeze({ target: Object.freeze({ installation: target.installation, profile: target.profile }), storeMode, ...consent }) });
  }
  focusIntent(binding: DeepReadonly<SessionBinding>): DeepReadonly<MutationIntent> | undefined {
    if (this.current.focus[binding.sessionId]?.availability.status !== 'available' || this.facade.work.observations.state.confidence === 'stale'
      || !sessions(this.facade.work.observations.state).some(row => canonicalData(row.binding) === canonicalData(binding))) return undefined;
    return Object.freeze({ kind: 'focus_session', input: Object.freeze({ session: binding }) });
  }
  dispose(): void { if (this.disposed) return; this.disposed = true; this.generation++; this.abort.abort(); this.stop(); this.listeners.clear(); }
}
