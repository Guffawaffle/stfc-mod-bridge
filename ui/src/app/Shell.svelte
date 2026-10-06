<script lang="ts">
  import type { Snippet } from 'svelte';
  import type { AnnouncementController } from '../state';
  import LiveAnnouncements from './LiveAnnouncements.svelte';
  import '../styles/tokens.css';
  let { theme = 'system', platform = 'windows', brand = 'STFC Mod Bridge', header, navigation, children, announcements }: {
    theme?: 'system' | 'light' | 'dark'; platform?: 'windows' | 'macos'; brand?: string;
    header?: Snippet; navigation?: Snippet; children?: Snippet; announcements: AnnouncementController;
  } = $props();
</script>
<div class="bridge-theme bridge-shell" data-theme={theme} data-platform={platform}>
  <a class="bridge-skip" href="#bridge-main">Skip to content</a>
  <header class="bridge-shell-header"><strong>{brand}</strong>{@render header?.()}</header>
  <div class="bridge-shell-body" data-navigation={navigation ? 'true' : undefined}>
    {#if navigation}<aside class="bridge-shell-navigation">{@render navigation()}</aside>{/if}
    <main id="bridge-main" tabindex="-1">{@render children?.()}</main>
  </div>
  <LiveAnnouncements controller={announcements}/>
</div>
<style>
  .bridge-shell { min-height: 100dvh; }
  .bridge-shell-header { display: flex; flex-wrap: wrap; align-items: center; gap: 1rem; padding: 1.25rem 1.5rem; border-bottom: 1px solid var(--bridge-border); }
  .bridge-shell-body { display: grid; grid-template-columns: minmax(0, 1fr); }
  .bridge-shell-body[data-navigation='true'] { grid-template-columns: minmax(12rem, 16rem) minmax(0, 1fr); }
  .bridge-shell-navigation { padding: 1.5rem 1rem; border-right: 1px solid var(--bridge-border); }
  main { min-width: 0; padding: clamp(1.25rem, 4vw, 3rem); }
  .bridge-skip { position: fixed; top: .5rem; left: .5rem; transform: translateY(-200%); z-index: 10; padding: .75rem; background: var(--bridge-surface); }
  .bridge-skip:focus { transform: none; }
  @media (max-width: 48rem) { .bridge-shell-body[data-navigation='true'] { grid-template-columns: minmax(0, 1fr); } .bridge-shell-navigation { border-right: 0; border-bottom: 1px solid var(--bridge-border); } }
</style>
