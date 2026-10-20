/* Generated schema type guards; Rust owns cross-field/native semantic validation. */
import type { Request, Reply, Event } from './protocol.js';
export function validateRequest(value: unknown): value is Request;
export function validateReply(value: unknown): value is Reply;
export function validateEvent(value: unknown): value is Event;
