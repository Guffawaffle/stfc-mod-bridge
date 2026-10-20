import { defineConfig, type Plugin } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';
import { mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { ownedArtifactPath } from '../scripts/next/owned-artifact.mjs';

const owningRoot = path.resolve(import.meta.dirname, '..');

const productionGraph: Plugin = {
  name: 'bridge-production-graph', apply: 'build',
  configResolved(config) {
    if (path.resolve(config.root, config.build.outDir) !== path.join(owningRoot, 'ui/dist')) {
      throw new Error('Production output must remain in the owning UI directory');
    }
    ownedArtifactPath(owningRoot, 'ui/dist', 'directory', { allowMissing: true });
    ownedArtifactPath(owningRoot, 'artifacts/next/frontend', 'directory', { allowMissing: true });
  },
  generateBundle(_options, bundle) {
    const modules = [...this.getModuleIds()].sort();
    for (const module of modules) {
      if (/[/\\](?:mocks|scenarios|fixtures|gallery|home-preview|settings-preview|management-preview)[/\\]|@wdio|webdriver|playwright/i.test(module)) {
        this.error('Production graph includes a development host or fixture');
      }
    }
    const directory = ownedArtifactPath(owningRoot, 'artifacts/next/frontend', 'directory', { allowMissing: true });
    mkdirSync(directory, { recursive: true });
    const graph = ownedArtifactPath(owningRoot, 'artifacts/next/frontend/production-graph.json', 'file', { allowMissing: true });
    writeFileSync(graph, JSON.stringify({
      schemaVersion: 'bridge-production-graph/v1', modules,
      outputs: Object.keys(bundle).sort(), productionMockHostPresent: false
    }, null, 2) + '\n');
  }
};

export default defineConfig({
  plugins: [svelte(), productionGraph],
  clearScreen: false,
  server: { host: '127.0.0.1', port: 1420, strictPort: true },
  build: { target: ['es2022', 'chrome110', 'safari16'] }
});
