import { mount } from 'svelte';
import App from './App.svelte';
import { nativeObservations } from './client-instance';

const target = document.getElementById('app');
if (!target) throw new Error('Bridge mount target is missing.');
mount(App, { target, props: { autoRefresh: nativeObservations } });
