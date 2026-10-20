import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import os from 'node:os';
import { selectHostArtifacts } from '../host-artifacts.mjs';

const root = path.join(os.tmpdir(), 'bridge-host-artifact-selection');
const physical = value => path.resolve(value);
const targets = [{name:'bridge_host_adapter', kind:'lib', manifest:'crates/bridge-host-adapter/Cargo.toml', source:'crates/bridge-host-adapter/src/lib.rs'}];
const subject = {reason:'compiler-artifact', target:{name:targets[0].name, kind:['lib'], src_path:path.join(root,targets[0].source)}, manifest_path:path.join(root,targets[0].manifest), executable:path.join(root,'target/native/registry-test'), profile:{test:true}};
const finished = {reason:'build-finished', success:true};
const encoded = rows => rows.map(row=>JSON.stringify(row)).join('\n');
const select = rows => selectHostArtifacts(encoded(rows), {root,targets,physical});

test('separate library test selection binds the actual declared source and manifest',()=>{
  assert.deepEqual(select([subject,finished]),[subject]);
  const target={...targets[0],name:'embedded_host',kind:'test',manifest:'crates/bridge-engine/Cargo.toml',source:'crates/bridge-engine/tests/embedded_host.rs'};
  const artifact={...subject,target:{name:target.name,kind:['test'],src_path:path.join(root,target.source)},manifest_path:path.join(root,target.manifest)};
  assert.deepEqual(selectHostArtifacts(encoded([artifact,finished]),{root,targets:[target],physical}),[artifact]);
});
test('missing, failed, duplicate or incomplete current build results refuse selection',()=>{
  for(const rows of [[subject],[subject,{...finished,success:false}],[subject,finished,finished],[finished],[subject,subject,finished]]) assert.throws(()=>select(rows));
});
test('foreign manifest, source, kind, name and extra executable cannot qualify the declared registry',()=>{
  for(const changed of [
    {...subject,manifest_path:path.join(root,'crates/bridge-engine/Cargo.toml')},
    {...subject,target:{...subject.target,src_path:path.join(root,'crates/bridge-engine/src/lib.rs')}},
    {...subject,target:{...subject.target,kind:['test']}},
    {...subject,target:{...subject.target,kind:['lib','cdylib']}},
    {...subject,target:{...subject.target,name:'other'}},
    {...subject,profile:{test:false}},
    {...subject,executable:'relative-test'}
  ]) assert.throws(()=>select([changed,finished]));
  assert.throws(()=>select([subject,{...subject,target:{...subject.target,name:'extra'}},finished]));
});
