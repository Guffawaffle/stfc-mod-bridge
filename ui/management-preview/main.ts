import { mount } from 'svelte';
import ManagementPreview from '../src/app/management-preview/ManagementPreview.svelte';
const target = document.getElementById('app');
if (!target)
    throw new Error('Management preview mount target is missing.');
mount(ManagementPreview, { target });
