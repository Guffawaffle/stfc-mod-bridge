import type { DeepReadonly } from '../../client';
import type { DiagnosticPreview, DiagnosticFact } from '../../generated/protocol';
export interface DiagnosticRow {
    label: string;
    value: string;
}
/** Public allowlist. Never stringify arbitrary facts, account data, native
 * custody references or captured private/secret values. */
export function diagnosticRows(preview: DeepReadonly<DiagnosticPreview>): readonly DiagnosticRow[] {
    const rows: DiagnosticRow[] = [];
    for (const fact of preview.content.facts) {
        switch (fact.kind) {
            case 'target':
                rows.push({ label: 'Profile', value: fact.value.profile.kind === 'ordinary' ? 'Ordinary profile' : `Isolated profile ${fact.value.profile.id}` }, { label: 'Installation', value: fact.value.installation.kind === 'registered' ? fact.value.installation.registrationId : 'Explicit directory installation' });
                break;
            case 'session':
                rows.push({ label: 'Session', value: fact.value.status === 'observed' ? `PID ${fact.value.value.process.pid} · ${fact.value.value.process.architecture}` : status(fact) });
                break;
            case 'runtime':
                rows.push({ label: 'Runtime ownership', value: fact.value.status === 'observed' ? fact.value.value.kind.replaceAll('_', ' ') : status(fact) });
                break;
            case 'game':
                rows.push({ label: 'Game client', value: fact.value.status === 'observed' ? `${fact.value.value.version} · ${fact.value.value.architecture}` : status(fact) });
                break;
            case 'bridge':
                rows.push({ label: 'Bridge application', value: fact.value.status === 'observed' ? `${fact.value.value.applicationId} · ${fact.value.value.channelId} · ${fact.value.value.architecture}` : status(fact) });
                break;
            case 'capability':
                rows.push({ label: 'Capability', value: `${fact.value.id} · ${fact.value.status.status.replaceAll('_', ' ')}` });
                break;
            case 'issue':
                rows.push({ label: 'Observed issue', value: fact.value.code.replaceAll('_', ' ') });
                break;
        }
    }
    return rows;
}
function status(fact: Extract<DeepReadonly<DiagnosticFact>, {
    kind: 'session' | 'runtime' | 'game' | 'bridge';
}>): string { return fact.value.status === 'missing' ? 'Not present' : fact.value.status === 'unknown' ? 'Unknown' : fact.value.status === 'unavailable' ? 'Unavailable' : 'Observed'; }
export function disclosedPaths(preview: DeepReadonly<DiagnosticPreview> | undefined): readonly DiagnosticRow[] {
    if (preview?.content.disclosure !== 'include_paths' || preview.reference.disclosure !== 'include_paths' || !preview.content.paths)
        return [];
    return [{ label: 'Installation path', value: preview.content.paths.installation.value }, ...(preview.content.paths.profile ? [{ label: 'Profile path', value: preview.content.paths.profile.value }] : [])];
}
