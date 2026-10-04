import assert from 'node:assert/strict';
import path from 'node:path';

export function verifyDependencyBoundary(metadata) {
  const packages = new Map(metadata.packages.map(p => [p.id, p]));
  const nodes = new Map(metadata.resolve.nodes.map(n => [n.id, n]));
  const roots = ['bridge-contracts', 'bridge-domain', 'bridge-engine', 'bridge-app'];
  const observed = {};
  for (const name of roots) {
    const root = metadata.packages.find(p => p.name === name && metadata.workspace_members.includes(p.id));
    assert.ok(root, `Missing workspace crate ${name}`);
    const visited = new Set();
    const visit = id => {
      if (visited.has(id)) return;
      visited.add(id);
      assert.ok(nodes.has(id) && packages.has(id), `Unresolved dependency ${id}`);
      const dependency = packages.get(id).name;
      assert.ok(!/^(tauri(?:-|$)|wry$|tao$|webview2(?:-|$)|serialize-to-javascript(?:-|$)|html5ever$)/.test(dependency), `${name} depends on renderer package ${dependency}`);
      for (const child of nodes.get(id).dependencies) visit(child);
    };
    visit(root.id);
    observed[name] = [...visited].map(id => packages.get(id).name).sort();
  }
  return observed;
}

export function selectShellArtifact({ output, packageId, root, hostTarget, platform }) {
  const messages = output.split(/\r?\n/).filter(line => line.trim()).map(line => JSON.parse(line));
  const artifacts = messages.filter(m => m.reason === 'compiler-artifact' && m.package_id === packageId && m.target?.name === 'bridge-desktop' && m.target.kind?.includes('bin') && typeof m.executable === 'string');
  assert.equal(artifacts.length, 1, 'Cargo must identify exactly one built desktop executable');
  const artifact = artifacts[0];
  assert.ok(artifact.features.includes('custom-protocol'), 'Foundation shell must embed production frontend assets');
  const executable = path.resolve(artifact.executable);
  const expected = path.join(root, 'target', hostTarget, 'release', platform === 'win32' ? 'bridge-desktop.exe' : 'bridge-desktop');
  assert.equal(executable, path.resolve(expected), 'Cargo executable escaped the explicitly bound native output route');
  return { executable, fresh: artifact.fresh, features: artifact.features };
}
