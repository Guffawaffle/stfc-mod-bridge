// Only implemented gates belong here. The planning graph is not executable evidence.
export const registry = {
  'host-adapter-foundation': {
    host: 'any', nativeProbeHosts: ['windows-x64', 'macos-arm64-native'], timeoutMs: 600000, packageAcceptanceAvailable: false,
    argv: ['scripts/next/host-adapter-foundation.mjs'],
    inputs: ['Cargo.toml', 'Cargo.lock', 'rust-toolchain.toml', '.cargo/config.toml', 'dependencies/next-toolchain.json',
      'package.json', 'pnpm-workspace.yaml', 'pnpm-lock.yaml', '.github/workflows/next-foundation.yml', 'docs/next/HOST_ADAPTER_FOUNDATION.md',
      'scripts/next', 'crates/bridge-engine', 'crates/bridge-host-adapter', 'crates/bridge-contracts', 'crates/bridge-domain', 'crates/bridge-toml', 'crates/bridge-native', 'crates/bridge-journal-io',
      'contracts', 'ui/package.json', 'ui/tsconfig.json', 'ui/vite.config.ts', 'ui/svelte.config.js', 'ui/src', 'ui/tests'],
    criteria: ['BR21-FND-01', 'BR21-FND-02', 'BR21-FND-03', 'BR21-FND-04', 'BR21-FND-05', 'BR21-FND-06'],
    boundary: 'Portable embedded owner and host registration registry, actual current native-architecture controlled host/kernel/registration test artifacts, compile-fail ownership controls and typed frontend adapter with injected invoke promises. No production owner, native Tauri invocation, GUI or full br-21 acceptance.'
  },
  'macos-platform-fixtures': {
    host: 'macos-arm64-native', timeoutMs: 600000, packageAcceptanceAvailable: false,
    argv: ['scripts/next/macos-platform-fixtures.mjs'],
    inputs: ['Cargo.toml', 'Cargo.lock', 'rust-toolchain.toml', '.cargo/config.toml', 'dependencies/next-toolchain.json',
      '.github/workflows/next-foundation.yml', 'docs/next/MAC_PLATFORM_FIXTURES.md', 'docs/next/NATIVE_QUALIFICATION.md',
      'scripts/next', 'crates/bridge-platform-macos', 'crates/bridge-domain', 'crates/bridge-contracts'],
    criteria: ['BR07-FIX-01', 'BR07-FIX-02', 'BR07-FIX-03', 'BR07-FIX-04', 'BR07-FIX-05', 'BR07-FIX-06', 'BR07-FIX-07', 'BR07-FIX-08'],
    boundary: 'Selected native Apple Silicon private fixtures only: exact source/tools/artifact inventories, one APFS volume, owned helper containment, native provider calls and ownership docs. Excludes Keychain, GUI, installed game, full br-07 acceptance and release qualification.'
  },
  'scope-contract': {
    host: 'any',
    argv: ['--test', 'scripts/next/tests/qualification.test.mjs', 'scripts/next/tests/prerequisites.test.mjs', 'scripts/next/tests/input-tree.test.mjs'],
    inputs: ['AGENTS.md', 'docs/next/campaign.json', 'docs/next/OPERATING_CONTRACT.md', 'docs/next/SCENARIOS.md', 'docs/next/NATIVE_QUALIFICATION.md', 'docs/next/DEPENDENCY_OWNERSHIP.md', 'scripts/next/qualification.mjs', 'scripts/next/input-tree.mjs', 'scripts/next/qualify.mjs', 'scripts/next/gate-registry.mjs', 'scripts/next/prerequisites.mjs', 'scripts/next/tests/qualification.test.mjs', 'scripts/next/tests/prerequisites.test.mjs', 'scripts/next/tests/input-tree.test.mjs'],
    criteria: ['BR00-01', 'BR00-02', 'BR00-03', 'BR00-04', 'BR00-05', 'BR00-06', 'BR00-07', 'BR00-08', 'BR00-09', 'BR00-10', 'BR00-11', 'BR00-12', 'BR00-13'],
    boundary: 'Scope/ownership contract completeness and fail-closed dispatcher tests; no native runtime or release qualification.'
  },
  'workspace-foundation': {
    host: 'any', timeoutMs: 900000,
    argv: ['scripts/next/foundation.mjs'],
    inputs: ['.gitignore', '.cargo/config.toml', '.github/workflows/next-foundation.yml', 'Cargo.toml', 'Cargo.lock', 'rust-toolchain.toml', 'package.json', 'pnpm-workspace.yaml', 'pnpm-lock.yaml', 'dependencies/next-toolchain.json', 'docs/next/DEVELOPMENT.md', 'scripts/next', 'crates', 'contracts', 'apps/desktop/src-tauri/Cargo.toml', 'apps/desktop/src-tauri/build.rs', 'apps/desktop/src-tauri/src', 'apps/desktop/src-tauri/tauri.conf.json', 'apps/desktop/src-tauri/capabilities', 'assets/stfc-mod-bridge.png', 'src/STFCCommunityMod.Launcher/Assets/stfc-mod-bridge.ico', 'ui/package.json', 'ui/index.html', 'ui/vite.config.ts', 'ui/svelte.config.js', 'ui/tsconfig.json', 'ui/src', 'ui/tests', 'ui/scenarios', 'ui/gallery'],
    criteria: ['BR01-01', 'BR01-02', 'BR01-03', 'BR01-04', 'BR01-05'],
    boundary: 'Locked workspace, browser-only development and actual current-host unsigned unbundled shell build; native automation supply-chain issue 235 pending. No installed game, ABI, accessibility or release qualification.'
  },
  'protocol-contract': {
    host: 'any', timeoutMs: 600000,
    argv: ['scripts/next/protocol.mjs'],
    inputs: ['Cargo.toml', 'Cargo.lock', 'rust-toolchain.toml', '.cargo/config.toml', 'package.json', 'pnpm-workspace.yaml', 'pnpm-lock.yaml', 'dependencies/next-toolchain.json', 'docs/next/PROTOCOL.md', 'docs/next/SCENARIOS.md', 'scripts/next', 'crates/bridge-contracts', 'contracts', 'ui/src/generated', 'ui/package.json', 'ui/tsconfig.json', 'ui/svelte.config.js'],
    criteria: ['BR02-01', 'BR02-02', 'BR02-03', 'BR02-04', 'BR02-05'],
    boundary: 'Actual strict Rust and browser framing checks, adversarial DTO tests, generated schema/type and typed catalog source drift; no dispatcher, durable admission, native custody or release qualification.'
  },
  'scenario-catalog': {
    host: 'any', timeoutMs: 360000,
    argv: ['scripts/next/protocol-fixtures.mjs', '--complete'],
    inputs: ['Cargo.toml', 'Cargo.lock', 'rust-toolchain.toml', '.cargo/config.toml', 'package.json', 'pnpm-workspace.yaml', 'pnpm-lock.yaml', 'dependencies/next-toolchain.json', 'docs/next/PROTOCOL.md', 'docs/next/SCENARIOS.md', 'scripts/next', 'crates/bridge-contracts', 'contracts', 'ui/src/generated'],
    criteria: ['BR02-02', 'BR02-03', 'BR02-04', 'BR02-06'],
    boundary: 'Complete synthetic 18-scenario catalog through this invocation\'s native Rust codec, strict browser schemas, normalized round trips and paired transcript relationships. No engine execution, native runtime or release qualification.'
  },
  'mock-contract': {
    host: 'any', timeoutMs: 300000,
    argv: ['scripts/next/mock-contract.mjs'],
    inputs: ['package.json', 'pnpm-workspace.yaml', 'pnpm-lock.yaml', 'dependencies/next-toolchain.json', 'docs/next/FRONTEND_CLIENT.md', 'scripts/next', 'contracts', 'ui/package.json', 'ui/tsconfig.json', 'ui/svelte.config.js', 'ui/src', 'ui/tests', 'ui/scenarios'],
    criteria: ['BR03-01', 'BR03-02', 'BR03-03'],
    boundary: 'Actual frontend-only type/guard/golden/client/mock/observation tests; scripted outcomes retain exact shared capture and metadata. No Rust build, policy implementation, native custody or release qualification.'
  },
  'mock-development': {
    host: 'any', timeoutMs: 300000,
    argv: ['scripts/next/mock-development.mjs'],
    inputs: ['package.json', 'pnpm-workspace.yaml', 'pnpm-lock.yaml', 'dependencies/next-toolchain.json', 'docs/next/FRONTEND_CLIENT.md', 'scripts/next', 'contracts', 'ui/package.json', 'ui/index.html', 'ui/vite.config.ts', 'ui/svelte.config.js', 'ui/tsconfig.json', 'ui/src', 'ui/tests', 'ui/scenarios', 'ui/gallery'],
    criteria: ['BR03-01', 'BR03-04'],
    boundary: 'Actual pinned project browser journeys and production dependency graph/output inspection without Rust or a game. Developer workbench proof; no finished product UX, native webview, installed runtime or release qualification.'
  },
  'operation-contention': {
    host: 'any', timeoutMs: 300000,
    argv: ['scripts/next/kernel.mjs', 'operation'],
    inputs: ['Cargo.toml', 'Cargo.lock', 'rust-toolchain.toml', '.cargo/config.toml', 'dependencies/next-toolchain.json', 'docs/next/OPERATION_KERNEL.md', 'scripts/next', 'crates/bridge-contracts', 'crates/bridge-domain', 'crates/bridge-engine', 'crates/bridge-journal-io'],
    criteria: ['BR04-01', 'BR04-02', 'BR04-03', 'BR04-04', 'BR04-05', 'BR04-06'],
    boundary: 'Actual current native engine test artifact, strict Clippy and synthetic canonical owner contention/capture/replay/cancel/close/event tests. No native domain services, installed game or release qualification.'
  },
  'crash-recovery': {
    host: 'any', timeoutMs: 300000,
    argv: ['scripts/next/kernel.mjs', 'recovery'],
    inputs: ['Cargo.toml', 'Cargo.lock', 'rust-toolchain.toml', '.cargo/config.toml', 'dependencies/next-toolchain.json', 'docs/next/OPERATION_KERNEL.md', 'scripts/next', 'crates/bridge-contracts', 'crates/bridge-domain', 'crates/bridge-engine', 'crates/bridge-journal-io'],
    criteria: ['BR04-04', 'BR04-05', 'BR04-06', 'BR04-07'],
    boundary: 'Actual private fixture filesystem journal/fault tests and child kill/restart at admission/staging/native commit. Port-owned game recovery, platform private-directory provisioning and canonical native exclusion remain unqualified.'
  },
  'native-ffi-contract': {
    host: 'any', nativeProbeHosts: ['windows-x64', 'macos-arm64-native'], timeoutMs: 600000,
    argv: ['scripts/next/native-ffi.mjs'],
    inputs: ['Cargo.toml', 'Cargo.lock', 'rust-toolchain.toml', '.cargo/config.toml', 'dependencies/next-toolchain.json', 'dependencies/next-native-inputs.json', 'docs/next/NATIVE_CONSUMERS.md', 'scripts/next', 'crates/bridge-native', 'crates/bridge-profiles', 'crates/bridge-toml'],
    criteria: ['BR05-01', 'BR05-02', 'BR05-03', 'BR05-04'],
    boundary: 'Actual selected native host ABI, export origin and originating allocation/lease ownership. Explicit historical probe inputs retain recipe gaps; independent Mac evidence, platform actor integration, installed game and release remain unqualified. Single-host suite receipt cannot accept this matrix package.'
  },
  'windows-platform': {
    host: 'windows-x64', timeoutMs: 600000,
    argv: ['scripts/next/windows-platform.mjs'],
    inputs: ['Cargo.toml', 'Cargo.lock', 'rust-toolchain.toml', '.cargo/config.toml', 'dependencies/next-toolchain.json', 'dependencies/next-windows-signature-fixture.json', 'docs/next/WINDOWS_PLATFORM.md', 'docs/next/PRIVATE_JOURNAL_STORAGE.md', 'scripts/next', 'contracts', 'crates/bridge-engine', 'crates/bridge-toml', 'crates/bridge-native', 'crates/bridge-domain', 'crates/bridge-contracts', 'crates/bridge-journal-io', 'crates/bridge-platform-windows'],
    criteria: ['BR06-01', 'BR06-02', 'BR06-03', 'BR06-04'],
    boundary: 'Actual Windows native physical/process identity, private retained reparse/replace/DPAPI/signature/owned-focus/shortcut fixtures. Token elevation/integrity is not directly observed here. Namespace exclusion and recovery remain application-service responsibilities. No game, account, catalog, Mac or release qualification.'
  },
  'windows-private-journal-fixtures': {
    host: 'windows-x64', timeoutMs: 600000, packageAcceptanceAvailable: false,
    argv: ['scripts/next/windows-private-journal-fixtures.mjs'],
    inputs: ['Cargo.toml', 'Cargo.lock', 'rust-toolchain.toml', '.cargo/config.toml', 'dependencies/next-toolchain.json',
      '.github/workflows/next-foundation.yml', 'docs/next/PRIVATE_JOURNAL_STORAGE.md', 'docs/next/WINDOWS_PLATFORM.md', 'docs/next/NATIVE_QUALIFICATION.md', 'docs/next/WINDOWS_JOURNAL_CI.md',
      'scripts/next', 'contracts', 'crates/bridge-platform-windows', 'crates/bridge-engine', 'crates/bridge-domain', 'crates/bridge-contracts', 'crates/bridge-journal-io', 'crates/bridge-toml', 'crates/bridge-native'],
    criteria: ['BR06-WJ01', 'BR06-WJ02', 'BR06-WJ03', 'BR06-WJ04', 'BR06-WJ05', 'BR06-WJ06', 'BR06-WJ07', 'BR06-WJ08', 'BR06-WJ09'],
    boundary: 'Selected retained Windows private-journal fixtures only: fresh test namespace, exact source/tools/artifact inventories, actual ACL/sharing/constructor-flush and child interruption observations. Broader custody-loss and namespace matrix, production owner adoption, full br-06, installed game, power-loss durability and release remain unqualified.'
  },
  'capability-projection': {
    host: 'any', timeoutMs: 300000,
    argv: ['scripts/next/domain-projections.mjs'],
    inputs: ['Cargo.toml', 'Cargo.lock', 'rust-toolchain.toml', '.cargo/config.toml', 'dependencies/next-toolchain.json', 'docs/next/DOMAIN_PROJECTIONS.md', 'scripts/next', 'crates/bridge-contracts', 'crates/bridge-domain'],
    criteria: ['BR13-01', 'BR13-02', 'BR13-03', 'BR13-04'],
    boundary: 'Actual current native domain test artifact and pure immutable capability/action/session/diagnostic projections with strict dependency and identity/provenance tests. No catalog, native service, engine execution or release qualification.'
  },
  'configuration-workspace': {
    host: 'any', timeoutMs: 600000, packageAcceptanceAvailable: false,
    argv: ['scripts/next/configuration-workspace.mjs'],
    inputs: ['Cargo.toml', 'Cargo.lock', 'rust-toolchain.toml', '.cargo/config.toml', 'dependencies/next-toolchain.json',
      'docs/next/CONFIGURATION_WORKSPACE.md', 'scripts/next/configuration-workspace.mjs', 'scripts/next/input-tree.mjs',
      'scripts/next/owned-artifact.mjs', 'scripts/next/rust-context.mjs', 'crates/bridge-engine', 'crates/bridge-contracts',
      'crates/bridge-domain', 'crates/bridge-native', 'crates/bridge-toml', 'contracts/fixtures/sc08-open-clean-draft-reply.json'],
    criteria: ['BR14-01', 'BR14-02', 'BR14-03', 'BR14-04', 'BR14-05', 'BR14-06'],
    boundary: 'Actual native-host test artifacts for portable draft, semantic preparation, transaction and restart models, with strict Clippy and stable source/tool/binary inventories. Synthetic owner/schema/sensitive-entry/TOML ports; actual native owner, producer adoption, OperationPorts composition and package acceptance remain unqualified.'
  },
  'frontend-home-targets': {
    host: 'any', timeoutMs: 600000,
    argv: ['scripts/next/frontend-home.mjs'],
    inputs: ['package.json', 'pnpm-workspace.yaml', 'pnpm-lock.yaml', 'dependencies/next-toolchain.json', 'docs/next/FRONTEND_HOME.md', 'scripts/next', 'contracts', 'ui/package.json', 'ui/index.html', 'ui/vite.config.ts', 'ui/svelte.config.js', 'ui/tsconfig.json', 'ui/src', 'ui/tests/home', 'ui/tests/home-preview', 'ui/home-preview', 'assets/stfc-mod-bridge.png'],
    criteria: ['BR18-01', 'BR18-02', 'BR18-03', 'BR18-04'],
    boundary: 'Actual browser-only typed Home and shared navigation/action custody, fixed shared fixture composition and production graph exclusion. No engine route, native webview, Mac execution, game or release qualification.'
  },
  'frontend-settings-drafts': {
    host: 'any', timeoutMs: 600000,
    argv: ['scripts/next/frontend-settings.mjs'],
    inputs: ['package.json', 'pnpm-workspace.yaml', 'pnpm-lock.yaml', 'dependencies/next-toolchain.json', 'docs/next/FRONTEND_SETTINGS.md', 'scripts/next', 'contracts', 'ui/package.json', 'ui/index.html', 'ui/vite.config.ts', 'ui/svelte.config.js', 'ui/tsconfig.json', 'ui/src', 'ui/tests', 'ui/settings-preview', 'ui/home-preview', 'ui/management-preview', 'assets/stfc-mod-bridge.png'],
    criteria: ['BR19-01', 'BR19-02', 'BR19-03', 'BR19-04', 'BR19-05', 'BR19-06'],
    boundary: 'Actual schema-driven Settings and Data Sync source tests, real App composition over bounded synthetic ports, pinned-browser review/save/reopen/stale/focus/theme/scale journeys and production graph exclusion. No native configuration owner, protected entry, installed webview, game, Mac or release qualification.'
  },
  'frontend-management-feedback': {
    host: 'any', timeoutMs: 600000,
    argv: ['scripts/next/frontend-management.mjs'],
    inputs: ['package.json', 'pnpm-workspace.yaml', 'pnpm-lock.yaml', 'dependencies/next-toolchain.json', 'docs/next/FRONTEND_MANAGEMENT.md', 'scripts/next', 'contracts', 'ui/package.json', 'ui/index.html', 'ui/vite.config.ts', 'ui/svelte.config.js', 'ui/tsconfig.json', 'ui/src', 'ui/tests', 'ui/management-preview', 'ui/settings-preview', 'ui/home-preview', 'assets/stfc-mod-bridge.png'],
    criteria: ['BR20-01', 'BR20-02', 'BR20-03', 'BR20-04', 'BR20-05', 'BR20-06'],
    boundary: 'Actual Management and Support source tests, real App composition over explicit synthetic targets and metadata, pinned-browser profile/install/update/history/cancel/recovery/privacy/theme/scale journeys and production graph exclusion. Native metadata bootstrap and refresh, installed webviews, game, Mac and release remain unqualified.'
  },
  'frontend-components': {
    host: 'any', timeoutMs: 300000,
    argv: ['scripts/next/frontend-components.mjs'],
    inputs: ['package.json', 'pnpm-workspace.yaml', 'pnpm-lock.yaml', 'dependencies/next-toolchain.json', 'docs/next/FRONTEND_COMPONENTS.md', 'scripts/next', 'contracts', 'ui/package.json', 'ui/tsconfig.json', 'ui/svelte.config.js', 'ui/src', 'ui/tests/components', 'ui/gallery'],
    criteria: ['BR12-01', 'BR12-03'],
    boundary: 'Actual Svelte type checks, primitive semantic/contrast and frozen shared facade tests. Synthetic client outcomes; no native custody, finished product screens, assistive-technology user study or release qualification.'
  },
  'frontend-accessibility': {
    host: 'any', timeoutMs: 300000,
    argv: ['scripts/next/frontend-accessibility.mjs'],
    inputs: ['package.json', 'pnpm-workspace.yaml', 'pnpm-lock.yaml', 'dependencies/next-toolchain.json', 'docs/next/FRONTEND_COMPONENTS.md', 'scripts/next', 'contracts', 'ui/package.json', 'ui/index.html', 'ui/vite.config.ts', 'ui/svelte.config.js', 'ui/tsconfig.json', 'ui/src', 'ui/tests/components', 'ui/gallery'],
    criteria: ['BR12-01', 'BR12-02', 'BR12-04'],
    boundary: 'Actual private pinned-browser native semantic/modal keyboard/focus/theme/scale scenarios and retained images for human UX review. Windows/macOS presentation modes are synthetic conventions; actual installed webviews, Narrator, VoiceOver and release remain unqualified.'
  }
};

// Separate development entries remain part of their source inventories even
// when the production graph correctly excludes them.
for (const id of ['workspace-foundation', 'mock-development']) {
  registry[id].inputs.push('ui/home-preview');
}
