import { createRawSnippet } from 'svelte';
import { render } from 'svelte/server';
import { readFileSync } from 'node:fs';
import { describe, expect, test } from 'vitest';
import { Button, Dialog, Field, Navigation, Notice, Progress, Select, TargetSummary } from '../../src/components';
import { createModalController, type FocusTarget, type ModalPort } from '../../src/components/modal';
import { presentProgress } from '../../src/components/progress';

const text = (content: string) => createRawSnippet(() => ({ render: () => `<span>${content}</span>` }));

describe('BR12 SC-17 native semantic primitives (SSR)', () => {
  test('button uses a native disabled busy guard and explicit action type', () => {
    const { body } = render(Button, { props: { children: text('Save reviewed changes'), busy: true, variant: 'primary' } });
    expect(body).toMatch(/<button[^>]+type="button"/);
    expect(body).toMatch(/<button[^>]+disabled/);
    expect(body).toContain('aria-busy="true"');
    expect(body).toContain('Save reviewed changes');
    expect(body).not.toContain('role="button"');
  });

  test('field associates visible label, instructions, required and invalid explanation with its input', () => {
    const { body } = render(Field, { props: { id: 'profile-name', label: 'Profile display name', value: 'Ready',
      description: 'Names do not change identity.', error: 'Use a shorter name.', required: true } });
    expect(body).toContain('<label for="profile-name"');
    expect(body).toContain('(required)');
    expect(body).toMatch(/<input[^>]+id="profile-name"/);
    expect(body).toContain('aria-describedby="profile-name-description profile-name-error"');
    expect(body).toContain('aria-invalid="true"');
    expect(body).toContain('id="profile-name-error"');
    expect(body).toContain('value="Ready"');
  });

  test('select remains native with a visible label and disabled options', () => {
    const { body } = render(Select, { props: { id: 'appearance', label: 'Appearance', value: 'system',
      options: [{ value: 'system', label: 'Follow system appearance' },
        { value: 'dark', label: 'Dark appearance unavailable', disabled: true }],
      description: 'Choose the Bridge appearance.' } });
    expect(body).toContain('<label for="appearance"');
    expect(body).toMatch(/<select[^>]+id="appearance"/);
    expect(body).toContain('aria-describedby="appearance-description"');
    expect(body).toMatch(/<option[^>]+value="dark"[^>]+disabled/);
    expect(body).not.toContain('role="listbox"');
  });

  test('notice announces only when requested and gives a warning a visible textual title', () => {
    const props = { title: 'Observation stale', tone: 'warning' as const, children: text('Refresh before reviewing work.') };
    const quiet = render(Notice, { props }).body;
    const live = render(Notice, { props: { ...props, live: 'polite' } }).body;
    expect(quiet).toContain('role="region"');
    expect(quiet).not.toContain('aria-live');
    expect(live).toContain('role="status"');
    expect(live).toContain('aria-live="polite"');
    expect(live).toContain('aria-atomic="true"');
    expect(live).toContain('Observation stale');
    expect(live).toContain('Refresh before reviewing work.');
  });

  test('navigation identifies the current view without tab-widget keyboard assumptions', () => {
    const { body } = render(Navigation, { props: { label: 'Workspace views', current: 'shuttle', onselect: () => {},
      items: [{ id: 'shuttle', label: 'Shuttle Bay' }, { id: 'engineering', label: 'Engineering' },
        { id: 'history', label: 'History unavailable', disabled: true }] } });
    expect(body).toContain('<nav aria-label="Workspace views"');
    expect(body.match(/aria-current="page"/g)).toHaveLength(1);
    expect(body).toContain('History unavailable');
    expect(body).not.toMatch(/role="(?:tab|tablist)"/);
  });

  test('target summary labels installation, profile and exact session separately', () => {
    const { body } = render(TargetSummary, { props: { installation: 'Windows test installation',
      profile: 'Ordinary OS-user state', session: 'Observed process 1042', status: 'Current observation' } });
    expect(body).toContain('Selected target');
    expect(body).toMatch(/<dt[^>]*>Installation<\/dt>/);
    expect(body).toMatch(/<dt[^>]*>Profile<\/dt>/);
    expect(body).toMatch(/<dt[^>]*>Session<\/dt>/);
    expect(body).toContain('Ordinary OS-user state');
    expect(body).toContain('Observed process 1042');
  });

  test('dialog has a native dialog element, named body and guaranteed Stay action', () => {
    const { body } = render(Dialog, { props: { open: true, title: 'Review target change',
      description: 'Choose what to do with these staged edits.', busy: true, onstay: () => {},
      children: text('The captured target remains Ordinary.'), actions: text('Save or Discard') } });
    expect(body).toMatch(/<dialog[^>]+aria-modal="true"/);
    expect(body).toContain('aria-labelledby=');
    expect(body).toContain('aria-describedby=');
    expect(body).toContain('Review target change');
    expect(body).toMatch(/<fieldset[^>]+disabled/);
    expect(body).toContain('Stay');
    // Opening happens only through showModal after mount, so no modeless open attribute is emitted.
    expect(body).not.toMatch(/<dialog[^>]+\sopen(?:\s|=|>)/);
  });

  test('current valid progress emits the observed value and maximum', () => {
    const { body } = render(Progress, { props: { label: 'Reading the snapshot', value: 3, max: 8, detail: 'Observation only' } });
    expect(body).toMatch(/<progress[^>]+max="8"[^>]+value="3"/);
    expect(body).toContain('38%');
    expect(body).toContain('Reading the snapshot');
    expect(body).toContain('Observation only');
  });

  test.each(['unknown', 'stale'] as const)('%s progress never emits a known percentage or determinate value', confidence => {
    const { body } = render(Progress, { props: { label: 'Reading the snapshot', value: 73, max: 100, confidence } });
    expect(body).not.toMatch(/<progress[^>]+\svalue=/);
    expect(body).not.toContain('73%');
    expect(body).toContain(confidence === 'stale' ? 'Observation stale' : 'Progress unknown');
  });

  test('long labels and HTML-like content render as text', () => {
    const long = 'A long installation display label '.repeat(20) + '<script>wrong()</script>';
    const { body } = render(TargetSummary, { props: { installation: long, profile: 'Ordinary' } });
    expect(body).toContain('A long installation display label');
    expect(body).toContain('&lt;script>wrong()&lt;/script>');
    expect(body).not.toContain('<script>wrong()');
  });
});

describe('BR12 SC-17 progress honesty', () => {
  test.each([
    [undefined, 100], [NaN, 100], [Infinity, 100], [-1, 100], [101, 100],
    [1, 0], [1, -1], [1, Infinity], [1, NaN], [Number.MAX_SAFE_INTEGER + 1, Number.MAX_SAFE_INTEGER + 1]
  ])('absent or invalid progress %s / %s remains unknown', (value, max) => {
    expect(presentProgress(value, max)).toEqual({ determinate: false, max: 100, text: 'Progress unknown' });
  });

  test('zero is observed progress and the safe maximum is handled without coercion', () => {
    expect(presentProgress(0, 8)).toEqual({ determinate: true, value: 0, max: 8, text: '0%' });
    expect(presentProgress(Number.MAX_SAFE_INTEGER, Number.MAX_SAFE_INTEGER)).toEqual({
      determinate: true, value: Number.MAX_SAFE_INTEGER, max: Number.MAX_SAFE_INTEGER, text: '100%'
    });
  });
});

function modalHarness() {
  let active: FocusTarget | null = null;
  let open = false;
  let busy = false;
  let stays = 0;
  let shows = 0;
  let closes = 0;
  let dialogFocus = 0;
  const targets = ['opener', 'first', 'last', 'outside'].map(name => ({
    name, isConnected: true, focuses: 0,
    focus() { this.focuses++; active = this; }
  }));
  const [opener, first, last, outside] = targets;
  let candidates: FocusTarget[] = [first, last];
  active = opener;
  const port: ModalPort = {
    get open() { return open; },
    showModal() { shows++; open = true; },
    close() { closes++; open = false; },
    focus() { dialogFocus++; active = null; },
    activeElement: () => active,
    contains: target => target === first || target === last,
    focusable: () => candidates
  };
  const controller = createModalController(port, { onstay: () => { stays++; }, busy: () => busy });
  return { controller, opener, first, last, outside, port,
    get active() { return active; }, get stays() { return stays; }, get shows() { return shows; },
    get closes() { return closes; }, get dialogFocus() { return dialogFocus; },
    setBusy(value: boolean) { busy = value; }, setCandidates(value: FocusTarget[]) { candidates = value; },
    closeNatively() { open = false; controller.closed(); } };
}

function key(key = 'Tab', shiftKey = false) {
  return { key, shiftKey, prevented: false, preventDefault() { this.prevented = true; } };
}

describe('BR12 SC-17 native modal lifecycle through a narrow fake port', () => {
  test('showModal opens once and closing restores the connected opener once', () => {
    const h = modalHarness();
    h.controller.sync(true); h.controller.sync(true);
    expect(h.shows).toBe(1);
    expect(h.active).toBe(h.first);
    h.controller.sync(false); h.controller.sync(false);
    expect(h.closes).toBe(1);
    expect(h.opener.focuses).toBe(1);
    h.controller.destroy(); h.controller.destroy();
    expect(h.closes).toBe(1);
  });

  test('Escape prevents native dismissal and requests semantic Stay only once', () => {
    const h = modalHarness(); const event = key('Escape');
    h.controller.sync(true); h.controller.cancel(event); h.controller.cancel(event);
    expect(event.prevented).toBe(true);
    expect(h.stays).toBe(1);
    expect(h.port.open).toBe(true);
    h.controller.sync(false);
    expect(h.opener.focuses).toBe(1);
  });

  test('busy prevents Escape and Stay; native accidental close reopens the same modal', () => {
    const h = modalHarness(); const event = key('Escape');
    h.controller.sync(true); h.setBusy(true);
    h.controller.cancel(event); h.controller.stay(); h.closeNatively();
    expect(event.prevented).toBe(true);
    expect(h.stays).toBe(0);
    expect(h.port.open).toBe(true);
    expect(h.shows).toBe(2);
    expect(h.opener.focuses).toBe(0);
    h.setBusy(false); h.controller.stay();
    expect(h.stays).toBe(1);
  });

  test('Tab and Shift Tab wrap focus and leave intermediate native focus handling alone', () => {
    const h = modalHarness(); h.controller.sync(true);
    h.last.focus(); const forward = key(); h.controller.keydown(forward);
    expect(forward.prevented).toBe(true); expect(h.active).toBe(h.first);
    const backward = key('Tab', true); h.controller.keydown(backward);
    expect(backward.prevented).toBe(true); expect(h.active).toBe(h.last);
    h.first.focus(); const ordinary = key(); h.controller.keydown(ordinary);
    expect(ordinary.prevented).toBe(false);
  });

  test('focus outside the modal and an empty enabled-control set remain contained', () => {
    const h = modalHarness(); h.controller.sync(true); h.outside.focus();
    const outside = key(); h.controller.keydown(outside);
    expect(outside.prevented).toBe(true); expect(h.active).toBe(h.first);
    h.setCandidates([]); const empty = key(); h.controller.keydown(empty);
    expect(empty.prevented).toBe(true); expect(h.dialogFocus).toBe(1);
  });

  test('unmount closes modal and a disconnected opener is never focused', () => {
    const h = modalHarness(); h.controller.sync(true); h.opener.isConnected = false;
    h.controller.destroy(); h.controller.sync(true); h.controller.stay();
    expect(h.closes).toBe(1); expect(h.opener.focuses).toBe(0); expect(h.stays).toBe(0);
    expect(h.shows).toBe(1);
  });

  test('initial focus accepts only a connected element within this modal', () => {
    const h = modalHarness();
    const preferred = createModalController(h.port, { onstay() {}, busy: () => false, initialFocus: () => h.last });
    preferred.sync(true); expect(h.active).toBe(h.last); preferred.sync(false);
    const outside = createModalController(h.port, { onstay() {}, busy: () => false, initialFocus: () => h.outside });
    outside.sync(true); expect(h.active).toBe(h.first); outside.sync(false);
    h.last.isConnected = false;
    const detached = createModalController(h.port, { onstay() {}, busy: () => false, initialFocus: () => h.last });
    detached.sync(true); expect(h.active).toBe(h.first);
  });

  test('unexpected nonbusy native dismissal requests Stay and restores focus', () => {
    const h = modalHarness(); h.controller.sync(true); h.closeNatively();
    expect(h.stays).toBe(1); expect(h.opener.focuses).toBe(1); expect(h.closes).toBe(0);
  });

  test('queued close event from a previous opening cannot dismiss a reopened modal', () => {
    const h = modalHarness(); h.controller.sync(true); h.controller.sync(false); h.controller.sync(true);
    h.controller.closed();
    expect(h.stays).toBe(0); expect(h.port.open).toBe(true); expect(h.shows).toBe(2);
    expect(h.closes).toBe(1);
  });
});

function luminance(hex: string): number {
  const channels = [1, 3, 5].map(start => Number.parseInt(hex.slice(start, start + 2), 16) / 255)
    .map(channel => channel <= .04045 ? channel / 12.92 : ((channel + .055) / 1.055) ** 2.4);
  return .2126 * channels[0] + .7152 * channels[1] + .0722 * channels[2];
}

function contrast(left: string, right: string): number {
  const values = [luminance(left), luminance(right)].sort((a, b) => b - a);
  return (values[0] + .05) / (values[1] + .05);
}

describe('BR12 SC-17 palette readability', () => {
  const css = readFileSync(new URL('../../src/styles/tokens.css', import.meta.url), 'utf8');
  const palettes = [
    { name: 'light', selector: /:root, \.bridge-theme \{([\s\S]+?)\n\}/ },
    { name: 'dark', selector: /:root\[data-theme='dark'\], \.bridge-theme\[data-theme='dark'\] \{([\s\S]+?)\n\}/ }
  ];
  const pairs = [
    ['text', 'surface'], ['text', 'background'], ['muted', 'surface'],
    ['muted', 'accent-soft'], ['muted', 'warning-soft'], ['accent-text', 'accent-soft'],
    ['on-accent', 'accent'], ['on-danger', 'danger'], ['danger-text', 'danger-soft']
  ];
  for (const palette of palettes) {
    const block = css.match(palette.selector)?.[1];
    const tokens = Object.fromEntries(Array.from((block ?? '').matchAll(/--bridge-([a-z-]+):\s*(#[0-9a-f]{6});/g),
      match => [match[1], match[2]]));
    for (const [foreground, background] of pairs) {
      test(`${palette.name} ${foreground} on ${background} retains normal text contrast`, () => {
        expect(tokens[foreground]).toMatch(/^#[0-9a-f]{6}$/);
        expect(tokens[background]).toMatch(/^#[0-9a-f]{6}$/);
        expect(contrast(tokens[foreground], tokens[background])).toBeGreaterThanOrEqual(4.5);
      });
    }
    test(`${palette.name} field boundary and focus retain nontext contrast`, () => {
      expect(contrast(tokens.border, tokens.surface)).toBeGreaterThanOrEqual(3);
      expect(contrast(tokens.focus, tokens.surface)).toBeGreaterThanOrEqual(3);
    });
  }
});
