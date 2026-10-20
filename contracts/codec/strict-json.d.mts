export const MAX_WIRE_BYTES: number;
export const MAX_WIRE_DEPTH: number;
export class WireInputError extends Error { readonly code: string; }
export function decodeStrictJson(input: string | Uint8Array): unknown;
