<script lang="ts">
  import { useBridge } from '../app';
  import { Navigation } from '../components';
  import type { WorkspaceView } from '../client';
  import type { BridgeFacade } from '../state';
  export interface Props { facade?: BridgeFacade; items?: readonly { id: WorkspaceView; label: string }[]; }
  let { facade = useBridge(), items = [
    { id: 'home', label: 'Shuttle Bay' }, { id: 'engineering', label: 'Engineering' },
    { id: 'settings', label: 'Settings' }, { id: 'data_sync', label: 'Data Sync' },
    { id: 'history', label: 'History' }, { id: 'diagnostics', label: 'Support' }, { id: 'preferences', label: 'Preferences' }
  ] }: Props = $props();
</script>
<Navigation label="Bridge workspace" {items} current={$facade.work.view} onselect={id => {
  const item = items.find(row => row.id === id); if (item) facade.navigate(item.id);
}}/>
