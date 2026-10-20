import { mount } from 'svelte';
import HomePreview from '../src/app/home-preview/HomePreview.svelte';

const target = document.getElementById('app');
if (!target) throw new Error('Home preview mount target is missing.');
mount(HomePreview, { target });
