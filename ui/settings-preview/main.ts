import { mount } from 'svelte';
import SettingsPreview from '../src/app/settings-preview/SettingsPreview.svelte';
const target=document.getElementById('app');if(!target)throw new Error('Settings preview mount target is missing.');mount(SettingsPreview,{target});
