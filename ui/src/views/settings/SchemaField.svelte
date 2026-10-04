<script lang="ts">
  import { Button, Select } from '../../components';
  import type { DeepReadonly, WorkState } from '../../client';
  import type { DocumentSnapshot, FieldDefinition, KeyChord, Modifier, NotificationPolicy, PublicConfigValue, SupportedPlatform } from '../../generated/protocol';
  import type { SettingsController } from './controller';
  import { applyLabel, fieldLabel, fieldText, presentField } from './presentation';
  export interface Props { field: DeepReadonly<FieldDefinition>; work: WorkState; controller: SettingsController; document?: DeepReadonly<DocumentSnapshot>; platform?: SupportedPlatform; index: number; disabled?: boolean; idPrefix?: string; }
  let { field, work, controller, document, platform, index, disabled = false, idPrefix = 'schema' }: Props = $props();
  const id = $derived(`${idPrefix}-field-${index}`), label = $derived(fieldLabel(field));
  const presented = $derived(presentField(field, work, document, platform));
  const blocked = $derived(disabled || !presented.supported);
  const chords: readonly DeepReadonly<KeyChord>[] = $derived(presented.value?.kind === 'keybinding' ? presented.value.value : []);
  const notification = $derived(presented.value?.kind === 'notification_policy' ? presented.value.value : undefined);
  const modifiers: readonly Modifier[] = ['control', 'alt', 'shift', 'meta'];
  function set(value: DeepReadonly<PublicConfigValue>): void { controller.setPublic(field.fieldId, value); }
  function setChords(value: readonly DeepReadonly<KeyChord>[]): void {
    if (value.length <= 8) set({ kind: 'keybinding', value: value as DeepReadonly<Extract<PublicConfigValue, { kind: 'keybinding' }>['value']> });
  }
  function replaceChord(position: number, key?: string, modifier?: Modifier, checked?: boolean): void {
    const next = chords.map((chord, row) => row !== position ? chord : { key: key ?? chord.key, modifiers: modifier ? modifiers.filter(value => value === modifier ? checked : chord.modifiers.some(item => item === value)) as KeyChord['modifiers'] : chord.modifiers });
    setChords(next);
  }
  function setPolicy(kind: NotificationPolicy['kind']): void {
    if (kind === 'channels' && field.valueType.kind === 'notification_policy') {
      if (!field.valueType.sounds.length) return;
      set({ kind: 'notification_policy', value: { kind, system: true, audio: false, sound: field.valueType.sounds[0] } });
    } else if (kind !== 'channels') set({ kind: 'notification_policy', value: { kind } });
  }
  function channel(change: Partial<{ system: boolean; audio: boolean; sound: string }>): void {
    if (notification?.kind === 'channels') set({ kind: 'notification_policy', value: { ...notification, ...change } });
  }
</script>
<article class="schema-field" aria-labelledby={`${id}-title`}>
  <header><h3 id={`${id}-title`}>{label}</h3><p class="source">{presented.state}</p></header>
  <div id={`${id}-description`} class="description"><p>{applyLabel(field.apply)}</p>
    {#if field.deprecated}<p>Deprecated by the provider. Existing intent remains visible.</p>{/if}
    {#if !presented.supported}<p>This field is unavailable for the current platform.</p>{/if}
  </div>
  {#if field.sensitivity !== 'public'}
    <p class="protected">{presented.protectedConfigured ? 'Protected value configured or replacement staged.' : 'No protected value is observed.'} Values stay hidden here. Replace them through secure entry.</p>
    <Button disabled={blocked} ariaLabel={`Capture protected replacement for ${label}`} busy={controller.state.capturing === field.fieldId} onclick={async () => { await controller.captureProtected(field.fieldId); }}>
      {field.sensitivity === 'secret' ? 'Capture secret replacement' : 'Capture private replacement'}</Button>
    {#if field.sensitivity === 'secret'}<Button disabled={blocked} ariaLabel={`Clear secret for ${label}`} onclick={() => { controller.clearSecret(field.fieldId); }}>Clear secret</Button>{/if}
  {:else if field.valueType.kind === 'boolean'}
    <label class="toggle"><input type="checkbox" checked={presented.value?.kind === 'boolean' && presented.value.value} disabled={blocked}
      aria-describedby={`${id}-description`} onchange={(event) => set({ kind: 'boolean', value: event.currentTarget.checked })}/><span>{label}</span></label>
    {#if !presented.value}<p>No effective value is observed. Choose an explicit override.</p>{/if}
  {:else if field.valueType.kind === 'integer' || field.valueType.kind === 'number'}
    <label for={id}>{label}</label>
    <input {id} type="text" inputmode={field.valueType.kind === 'integer' ? 'numeric' : 'decimal'} maxlength="128" disabled={blocked}
      value={presented.numericText ?? fieldText(presented.value)} aria-invalid={presented.numericText !== undefined || presented.error ? 'true' : undefined}
      aria-describedby={`${id}-description ${id}-constraints${presented.numericText !== undefined || presented.error ? ` ${id}-error` : ''}`}
      oninput={(event) => { controller.setNumeric(field.fieldId, event.currentTarget.value); }}/>
    <p id={`${id}-constraints`} class="description">{field.valueType.minimum != null ? `Minimum ${field.valueType.minimum}. ` : ''}{field.valueType.maximum != null ? `Maximum ${field.valueType.maximum}. ` : ''}Enter a complete decimal value. Values retain their exact precision.</p>
    {#if presented.numericText !== undefined}<Button disabled={blocked} ariaLabel={`Reset unfinished value for ${label}`} onclick={() => { controller.resetNumeric(field.fieldId); }}>Reset unfinished value</Button>{/if}
  {:else if field.valueType.kind === 'string'}
    <label for={id}>{label}</label><input {id} type="text" value={fieldText(presented.value)} disabled={blocked}
      maxlength={Math.min(Number(field.valueType.maximumLength), 4096) * 2} aria-describedby={`${id}-description`} aria-invalid={presented.error ? 'true' : undefined}
      oninput={(event) => set({ kind: 'string', value: event.currentTarget.value })}/>
    <p class="description">Up to {field.valueType.maximumLength} characters.</p>
  {:else if field.valueType.kind === 'enum'}
    <Select {id} {label} value={fieldText(presented.value)} disabled={blocked} error={presented.error || undefined}
      description="Choose a value published by this provider schema." options={[{ value: '', label: 'Choose an override', disabled: true }, ...field.valueType.values.map(value => ({ value, label: value }))]}
      onchange={value => set({ kind: 'enum', value })}/>
  {:else if field.valueType.kind === 'keybinding'}
    <fieldset disabled={blocked}><legend>{label}</legend>
      {#each chords as chord, row (row)}
        <div class="chord"><Select id={`${id}-key-${row}`} label={`Key ${row + 1}`} value={chord.key}
          options={field.valueType.keys.map(value => ({ value, label: value }))} onchange={value => replaceChord(row, value)}/>
          <fieldset class="modifiers"><legend>Modifiers for key {row + 1}</legend>{#each modifiers as modifier}
            <label class="toggle"><input type="checkbox" checked={chord.modifiers.some(value => value === modifier)} onchange={event => replaceChord(row, undefined, modifier, event.currentTarget.checked)}/><span>{modifier}</span></label>{/each}</fieldset>
          <Button ariaLabel={`Remove keybinding ${row + 1}`} onclick={() => setChords(chords.filter((_, position) => position !== row))}>Remove keybinding</Button>
        </div>
      {/each}
      <Button disabled={!field.valueType.keys.length || chords.length >= (field.valueType.multiple ? 8 : 1)} onclick={() => {
        if (field.valueType.kind === 'keybinding' && field.valueType.keys.length) setChords([...chords, { key: field.valueType.keys[0], modifiers: [] }]);
      }}>Add keybinding</Button>
    </fieldset>
  {:else if field.valueType.kind === 'notification_policy'}
    <Select {id} {label} value={notification?.kind ?? ''} disabled={blocked} options={[{value:'',label:'Choose a notification policy',disabled:true},{value:'disabled',label:'Disabled'},
      {value:'system_only',label:'System notifications only'},{value:'channels',label:'Choose notification channels',disabled:!field.valueType.sounds.length}]} onchange={value => setPolicy(value as NotificationPolicy['kind'])}/>
    {#if notification?.kind === 'channels'}
      <fieldset disabled={blocked}><legend>Notification channels</legend><label class="toggle"><input type="checkbox" checked={notification.system} onchange={event => channel({ system: event.currentTarget.checked })}/><span>System notifications</span></label>
        <label class="toggle"><input type="checkbox" checked={notification.audio} onchange={event => channel({ audio: event.currentTarget.checked })}/><span>Audio notifications</span></label>
        <Select id={`${id}-sound`} label="Notification sound" value={notification.sound} options={field.valueType.sounds.map(value => ({value,label:value}))} onchange={value => channel({sound:value})}/>
      </fieldset>
    {/if}
  {/if}
  {#if presented.error || presented.numericText !== undefined}<p id={`${id}-error`} class="error">{presented.error || 'This numeric input is unfinished or outside its constraints. Finish it or choose Reset.'}</p>{/if}
  <footer><Button disabled={blocked || presented.numericText !== undefined} ariaLabel={`Use provider default for ${label}`} onclick={() => { controller.removeOverride(field.fieldId); }}>Use provider default</Button>
    {#if work.edits.some(edit => 'fieldId' in edit && edit.fieldId === field.fieldId)}<Button disabled={blocked} ariaLabel={`Undo staged change for ${label}`} onclick={() => { controller.undoField(field.fieldId); }}>Undo staged field change</Button>{/if}</footer>
</article>
<style>
  .schema-field { display: grid; gap: .7rem; padding: 1.1rem; border: 1px solid var(--bridge-border); border-radius: var(--bridge-radius); min-inline-size: 0; }
  header, footer { display: flex; flex-wrap: wrap; gap: .7rem; align-items: center; justify-content: space-between; } h3 { margin: 0; font-size: 1.05rem; }
  p { margin: 0; overflow-wrap: anywhere; } .source, .description { color: var(--bridge-muted); font-size: .9rem; } .description { display: grid; gap: .25rem; }
  label { font-weight: 600; } input[type='text'] { min-block-size: 2.75rem; inline-size: 100%; padding: .65rem .75rem; font: inherit; color: var(--bridge-text); background: var(--bridge-surface); border: 1px solid var(--bridge-border); border-radius: var(--bridge-radius); }
  input[aria-invalid='true'] { border-width: 2px; border-color: var(--bridge-danger-text); } .error { color: var(--bridge-danger-text); } .toggle { display: flex; align-items: center; gap: .7rem; min-block-size: 2.75rem; }
  input[type='checkbox'] { inline-size: 1.25rem; block-size: 1.25rem; flex-shrink: 0; } fieldset { display: grid; gap: .7rem; min-inline-size: 0; border: 1px solid var(--bridge-border); border-radius: var(--bridge-radius); }
  .chord { display: grid; gap: .65rem; padding-block: .6rem; } .modifiers { display: flex; flex-wrap: wrap; } .protected { color: var(--bridge-muted); }
  @media (forced-colors: active) { .error { color: CanvasText; } }
</style>
