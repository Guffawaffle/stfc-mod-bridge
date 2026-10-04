// Synthetic fixture authoring only. These helpers consume generated Rust types;
// they perform no engine dispatch, native calls or production account work.
import type {
  Command, CommandResult, Event, EventBody, Cursor, Query, QueryResult,
  Reply, Request, BridgeError, OrdinaryTargetSelector, IsolatedTargetSelector, ResolvedTarget
} from '../../ui/src/generated/protocol.js';

export function syntheticId(sequence: number): string {
  if (!Number.isSafeInteger(sequence) || sequence < 0 || sequence > 0xffffffff) throw new Error('Invalid synthetic ID sequence');
  return `${sequence.toString(16).padStart(8, '0')}-1111-4111-8111-111111111111`;
}

export function queryRequest(requestId: string, query: Query): Request {
  return { protocolVersion: 1, requestId, body: { type: 'query', query } };
}
export function commandRequest(requestId: string, command: Command): Request {
  return { protocolVersion: 1, requestId, body: { type: 'command', command } };
}
export function queryReply(requestId: string, query: QueryResult): Reply {
  return { protocolVersion: 1, requestId, body: { type: 'result', result: { type: 'query', query } } };
}
export function commandReply(requestId: string, command: CommandResult): Reply {
  return { protocolVersion: 1, requestId, body: { type: 'result', result: { type: 'command', command } } };
}
export function rejectedReply(requestId: string, error: BridgeError): Reply {
  return { protocolVersion: 1, requestId, body: { type: 'rejected', error } };
}
export function protocolEvent(cursor: Cursor, body: EventBody): Event {
  return { protocolVersion: 1, cursor, body };
}

export const ordinarySelector: OrdinaryTargetSelector = {
  installation: { kind: 'registered', id: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' },
  profile: { kind: 'ordinary' }
};
export const isolatedSelector: IsolatedTargetSelector = {
  installation: { kind: 'registered', id: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' },
  profile: { kind: 'isolated', id: 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb' }
};
export const ordinaryBinding: ResolvedTarget = {
  installation: {
    kind: 'registered', registrationId: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
    registrationRevision: 'synthetic-installation-revision-1',
    physicalId: 'synthetic-installation-physical-1', nativeTargetRef: 'synthetic-native-target-1'
  },
  profile: { kind: 'ordinary', ownerScope: 'synthetic-native-owner-1' }
};
export const isolatedBinding: ResolvedTarget = {
  installation: ordinaryBinding.installation,
  profile: { kind: 'isolated', id: 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', revision: 'synthetic-profile-revision-1' }
};
