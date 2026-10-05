import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import {createHash,randomUUID} from 'node:crypto';
import {syncBuiltinESMExports} from 'node:module';
import path from 'node:path';
import {pathToFileURL} from 'node:url';
import {NORMAL_ARCHIVE_FILE_CAP,readNormalArchive,selectNormalArchive} from '../windows-normal-archive.mjs';

const checkout=fs.realpathSync.native(path.resolve(import.meta.dirname,'../../..'));
const controls=path.join(checkout,'artifacts','next','preparation','windows-normal-archive-controls-d27d683a');
const content=Buffer.from('!<arch>\nSynthetic normal archive controls only; these bytes are never compiled or executed.\n');
const sha=bytes=>createHash('sha256').update(bytes).digest('hex');
const unsupportedLink=error=>['EPERM','EACCES','ENOSYS','ENOTSUP','EOPNOTSUPP'].includes(error?.code);
function fixture(t,{mode='copies',metadataAlias=false,linkedDeps=false}={},action){
  const directory=path.join(controls,randomUUID());fs.mkdirSync(directory,{recursive:true});
  const root=path.join(directory,'owner'),outside=path.join(directory,'outside');fs.mkdirSync(root);fs.mkdirSync(outside);
  assert.equal(fs.realpathSync.native(directory),directory);assert.equal(fs.realpathSync.native(root),root);
  const manifest=path.join(root,'crates','bridge-platform-windows','Cargo.toml'),source=path.join(root,'crates','bridge-platform-windows','src','lib.rs');
  fs.mkdirSync(path.dirname(source),{recursive:true});fs.writeFileSync(manifest,'[package]\nname = "bridge-platform-windows"\nversion = "0.1.0"\n',{flag:'wx'});fs.writeFileSync(source,'// Synthetic source ownership witness; never compiled.\n',{flag:'wx'});
  const normalTarget=path.join(root,'target','windows-private-journal-normal',randomUUID()),hostTarget='x86_64-pc-windows-msvc';
  const profile=path.join(normalTarget,hostTarget,'debug'),deps=path.join(profile,'deps');fs.mkdirSync(profile,{recursive:true});
  if(linkedDeps){
    try{fs.symlinkSync(outside,deps,process.platform==='win32'?'junction':'dir');}
    catch(error){if(unsupportedLink(error)){t.skip(`Host cannot create the native directory-link control: ${error.code}`);return;}throw error;}
  }else fs.mkdirSync(deps);
  const selected=path.join(profile,'libbridge_platform_windows.rlib'),alias=path.join(deps,'libbridge_platform_windows-3f30bf7410c0f3b3.rlib');
  const metadata=path.join(deps,'libbridge_platform_windows-3f30bf7410c0f3b3.rmeta');
  fs.writeFileSync(selected,content,{flag:'wx'});
  if(mode==='pair'){
    try{fs.linkSync(selected,alias);}catch(error){if(unsupportedLink(error)){t.skip(`Host cannot create the native same-inode hard-link control: ${error.code}`);return;}throw error;}
  }else fs.copyFileSync(selected,alias,fs.constants.COPYFILE_EXCL);
  fs.writeFileSync(metadata,'Synthetic ancillary metadata; never opened by the archive reader.\n',{flag:'wx'});
  if(metadataAlias){
    try{fs.linkSync(metadata,path.join(outside,'unresolved-metadata-alias.rmeta'));}
    catch(error){if(unsupportedLink(error)){t.skip(`Host cannot create the native ancillary hard-link control: ${error.code}`);return;}throw error;}
  }
  const platformPackage={name:'bridge-platform-windows',id:`path+${pathToFileURL(path.dirname(manifest)).href}#0.1.0`,manifest_path:manifest};
  const compilerArtifact={reason:'compiler-artifact',package_id:platformPackage.id,manifest_path:manifest,
    target:{kind:['lib'],crate_types:['lib'],name:'bridge_platform_windows',src_path:source,edition:'2024',doc:true,doctest:true,test:true},
    profile:{opt_level:'0',debuginfo:2,debug_assertions:true,overflow_checks:true,test:false},features:[],filenames:[selected,metadata],executable:null,fresh:false};
  const args={root,normalTarget,hostTarget,platformPackage,compilerArtifact};
  action({directory,root,outside,selected,alias,metadata,args});
  // Retain these fresh ignored controls; do not enumerate or remove their trees.
  fs.writeFileSync(path.join(directory,'completed.json'),JSON.stringify({schemaVersion:'bridge-normal-archive-filesystem-control/v1',test:t.name,mode,executed:false,nativeFixtureExecuted:false},null,2)+'\n',{flag:'wx'});
}
function interceptedRead(t,change,action){
  const originalRead=fs.readSync,originalOpen=fs.openSync,originalClose=fs.closeSync,opened=[],closed=[];let changed=false;
  t.mock.method(fs,'openSync',(...args)=>{const fd=originalOpen(...args);if(args[1]==='r')opened.push(fd);return fd;});
  t.mock.method(fs,'closeSync',fd=>{if(opened.includes(fd))closed.push(fd);return originalClose(fd);});
  t.mock.method(fs,'readSync',(...args)=>{const count=originalRead(...args);if(!changed){changed=true;change();}return count;});
  syncBuiltinESMExports();
  try{action({opened,closed});}finally{t.mock.restoreAll();syncBuiltinESMExports();}
}

test('actual same-inode two-link archive routes are read without granting execution',t=>fixture(t,{mode:'pair'},({args,selected,alias})=>{
  const selection=selectNormalArchive(args),read=readNormalArchive(selection);
  assert.deepEqual(read.bytes,content);assert.equal(read.observation.policy,'cargo-same-inode-two-link');
  assert.equal(read.observation.selected.route,selected);assert.equal(read.observation.alias.route,alias);
  assert.equal(read.observation.selected.identity.nlink,'2');assert.deepEqual(read.observation.selected.identity,read.observation.alias.identity);
  assert.equal(read.observation.sha256,sha(content));assert.equal(read.observation.executed,false);assert.equal(read.observation.artifactRole,'isolated-normal-library-archive');
  assert.equal(Object.isFrozen(selection),true);assert.equal(Object.isFrozen(selection.compilerArtifact.filenames),true);
  assert.equal(Object.isFrozen(read.observation),true);assert.equal(Object.isFrozen(read.observation.selected.identity),true);
  assert.deepEqual(readNormalArchive(selection).observation,read.observation);
}));
test('actual distinct one-link Cargo copies require equal complete bytes',t=>fixture(t,{},({args})=>{
  const read=readNormalArchive(selectNormalArchive(args));assert.deepEqual(read.bytes,content);
  assert.equal(read.observation.policy,'cargo-distinct-single-link-copies');assert.equal(read.observation.selected.identity.nlink,'1');assert.equal(read.observation.alias.identity.nlink,'1');
  assert.notEqual(read.observation.selected.identity.ino,read.observation.alias.identity.ino);assert.equal(read.observation.alias.sha256,read.observation.sha256);
}));
test('single-link copies may have different legitimate timestamps',t=>fixture(t,{},({args,alias})=>{
  fs.utimesSync(alias,new Date('2020-01-01T00:00:00Z'),new Date('2020-01-01T00:00:00Z'));
  assert.equal(readNormalArchive(selectNormalArchive(args)).observation.policy,'cargo-distinct-single-link-copies');
}));
test('ancillary two-link metadata is recorded but never opened or alias-qualified',t=>fixture(t,{metadataAlias:true},({args,metadata})=>{
  const originalOpen=fs.openSync,opened=[];t.mock.method(fs,'openSync',(...values)=>{assert.notEqual(values[0],metadata,'Unresolved metadata must not be opened');opened.push(values[0]);return originalOpen(...values);});syncBuiltinESMExports();
  try{
    const read=readNormalArchive(selectNormalArchive(args));assert.deepEqual(opened,[args.compilerArtifact.filenames[0],metadata.slice(0,-'.rmeta'.length)+'.rlib']);
    assert.equal(read.observation.ancillaryMetadata.identity.nlink,'2');assert.equal(read.observation.ancillaryMetadata.scanned,false);assert.equal(read.observation.ancillaryMetadata.aliasQualified,false);
    assert.equal('sha256' in read.observation.ancillaryMetadata,false);
  }finally{t.mock.restoreAll();syncBuiltinESMExports();}
}));
test('unrecognized third archive link outside the owner refuses',t=>fixture(t,{mode:'pair'},({args,selected,outside})=>{
  fs.linkSync(selected,path.join(outside,'third-archive-alias.rlib'));
  assert.throws(()=>readNormalArchive(selectNormalArchive(args)),/Unknown selected archive hard-link alias/);
}));
test('mixed one-link and two-link distinct archive routes refuse',t=>fixture(t,{},({args,alias,outside})=>{
  try{fs.linkSync(alias,path.join(outside,'extra-deps-alias.rlib'));}catch(error){if(unsupportedLink(error)){t.skip(`Host cannot create the native extra-alias control: ${error.code}`);return;}throw error;}
  assert.throws(()=>readNormalArchive(selectNormalArchive(args)),/Unknown deps archive hard-link alias/);
}));
test('different inodes with two links each refuse despite matching bytes',t=>fixture(t,{},({args,selected,alias,outside})=>{
  try{fs.linkSync(selected,path.join(outside,'extra-selected.rlib'));fs.linkSync(alias,path.join(outside,'extra-deps.rlib'));}
  catch(error){if(unsupportedLink(error)){t.skip(`Host cannot create the native two-file hard-link control: ${error.code}`);return;}throw error;}
  assert.throws(()=>readNormalArchive(selectNormalArchive(args)),/must share one physical file/);
}));
test('equal-length single-link copy byte substitution refuses',t=>fixture(t,{},({args,alias})=>{
  const changed=Buffer.from(content);changed[changed.length-2]^=1;fs.writeFileSync(alias,changed);
  assert.throws(()=>readNormalArchive(selectNormalArchive(args)),/copy bytes differ/);
}));
test('single-link copy length substitution refuses',t=>fixture(t,{},({args,alias})=>{
  fs.writeFileSync(alias,'shorter');assert.throws(()=>readNormalArchive(selectNormalArchive(args)),/equal bounded lengths/);
}));
test('empty archive refuses before allocation or read',t=>fixture(t,{mode:'pair'},({args,selected})=>{
  fs.truncateSync(selected,0);let allocated=0;const originalAlloc=Buffer.alloc;t.mock.method(Buffer,'alloc',(...values)=>{allocated++;return originalAlloc(...values);});
  try{assert.throws(()=>readNormalArchive(selectNormalArchive(args)),/bound checked before allocation\/read/);assert.equal(allocated,0);}finally{t.mock.restoreAll();}
}));
test('sparse archive above 256MiB refuses before allocation or read',t=>fixture(t,{mode:'pair'},({args,selected})=>{
  fs.truncateSync(selected,NORMAL_ARCHIVE_FILE_CAP+1);let allocated=0,readCount=0;const originalAlloc=Buffer.alloc,originalRead=fs.readSync;
  t.mock.method(Buffer,'alloc',(...values)=>{allocated++;return originalAlloc(...values);});t.mock.method(fs,'readSync',(...values)=>{readCount++;return originalRead(...values);});syncBuiltinESMExports();
  try{assert.throws(()=>readNormalArchive(selectNormalArchive(args)),/bound checked before allocation\/read/);assert.equal(allocated,0);assert.equal(readCount,0);}finally{t.mock.restoreAll();syncBuiltinESMExports();}
}));
test('physical deps junction escape refuses without following foreign ownership',t=>fixture(t,{linkedDeps:true},({args})=>{
  assert.throws(()=>selectNormalArchive(args),/links or junctions/);
}));
test('timestamp drift during a read refuses and closes both owned FDs',t=>fixture(t,{},({args,selected})=>{
  const selection=selectNormalArchive(args);
  interceptedRead(t,()=>fs.utimesSync(selected,new Date('2000-01-01T00:00:00Z'),new Date('2000-01-01T00:00:00Z')),({opened,closed})=>{
    assert.throws(()=>readNormalArchive(selection),/open archive changed during bounded read/);assert.equal(opened.length,2);assert.deepEqual(closed,[opened[1],opened[0]]);
    for(const fd of opened)assert.throws(()=>fs.fstatSync(fd),error=>error.code==='EBADF');
  });
}));
test('archive alias loss during a read refuses and closes both FDs',t=>fixture(t,{mode:'pair'},({args,alias})=>{
  const selection=selectNormalArchive(args);
  interceptedRead(t,()=>{fs.unlinkSync(alias);fs.writeFileSync(alias,content,{flag:'wx'});},({opened,closed})=>{
    assert.throws(()=>readNormalArchive(selection),/open archive changed during bounded read/);assert.equal(opened.length,2);assert.deepEqual(closed,[opened[1],opened[0]]);
  });
}));
test('ancillary metadata route substitution during archive read refuses',t=>fixture(t,{},({args,metadata})=>{
  const selection=selectNormalArchive(args);
  interceptedRead(t,()=>{fs.renameSync(metadata,metadata+'.original');fs.writeFileSync(metadata,'Changed ancillary route; never opened.\n',{flag:'wx'});},()=>{
    assert.throws(()=>readNormalArchive(selection),/Ancillary metadata route changed/);
  });
}));
test('alias replacement between route inspection and FD admission refuses',t=>fixture(t,{},({args,alias})=>{
  const selection=selectNormalArchive(args),originalOpen=fs.openSync,originalClose=fs.closeSync,opened=[],closed=[];let replaced=false;
  t.mock.method(fs,'openSync',(...values)=>{
    if(values[0]===alias&&values[1]==='r'&&!replaced){replaced=true;fs.renameSync(alias,alias+'.original');fs.writeFileSync(alias,content,{flag:'wx'});}
    const fd=originalOpen(...values);if(values[1]==='r')opened.push(fd);return fd;
  });
  t.mock.method(fs,'closeSync',fd=>{if(opened.includes(fd))closed.push(fd);return originalClose(fd);});syncBuiltinESMExports();
  try{assert.throws(()=>readNormalArchive(selection),/Deps archive route\/FD identity changed before read/);assert.equal(opened.length,2);assert.deepEqual(closed,[opened[1],opened[0]]);}
  finally{t.mock.restoreAll();syncBuiltinESMExports();}
}));
test('second FD admission failure closes the already-opened selected FD',t=>fixture(t,{},({args,alias})=>{
  const selection=selectNormalArchive(args),originalOpen=fs.openSync,originalClose=fs.closeSync,opened=[],closed=[];
  t.mock.method(fs,'openSync',(...values)=>{
    if(values[0]===alias&&values[1]==='r')throw Object.assign(new Error('Synthetic alias admission failure'),{code:'EIO'});
    const fd=originalOpen(...values);if(values[1]==='r')opened.push(fd);return fd;
  });
  t.mock.method(fs,'closeSync',fd=>{if(opened.includes(fd))closed.push(fd);return originalClose(fd);});syncBuiltinESMExports();
  try{assert.throws(()=>readNormalArchive(selection),error=>error.code==='EIO');assert.equal(opened.length,1);assert.deepEqual(closed,opened);}
  finally{t.mock.restoreAll();syncBuiltinESMExports();}
}));
test('later fences can compare the complete observation and detect updated copies',t=>fixture(t,{},({args,selected,alias})=>{
  const selection=selectNormalArchive(args),before=readNormalArchive(selection).observation;
  const changed=Buffer.from(content);changed[changed.length-2]^=1;fs.writeFileSync(selected,changed);fs.copyFileSync(selected,alias);
  const after=readNormalArchive(selection).observation;assert.notEqual(after.sha256,before.sha256);assert.notDeepEqual(after,before);
}));
test('caller-mutated Cargo row cannot change the validated selection',t=>fixture(t,{},({args})=>{
  const selection=selectNormalArchive(args),before=readNormalArchive(selection).observation;
  args.compilerArtifact.filenames[0]='foreign.exe';args.compilerArtifact.profile.test=true;
  assert.deepEqual(readNormalArchive(selection).observation,before);
}));
test('forged selection cannot grant the normal archive policy',()=>{
  assert.throws(()=>readNormalArchive({selected:'test.exe',alias:'test.exe'}),/own validated selection/);
});

const malformed=[
  ['package ID mismatch',args=>{args.compilerArtifact.package_id+='-foreign';}],
  ['foreign package metadata manifest',args=>{args.platformPackage.manifest_path+='-foreign';}],
  ['foreign artifact manifest',args=>{args.compilerArtifact.manifest_path+='-foreign';}],
  ['foreign artifact source',args=>{args.compilerArtifact.target.src_path+='-foreign';}],
  ['wrong target name',args=>{args.compilerArtifact.target.name='other';}],
  ['non-library target',args=>{args.compilerArtifact.target.kind=['bin'];}],
  ['different crate type',args=>{args.compilerArtifact.target.crate_types=['rlib'];}],
  ['executed artifact',args=>{args.compilerArtifact.executable=args.compilerArtifact.filenames[0];}],
  ['cached artifact',args=>{args.compilerArtifact.fresh=true;}],
  ['fixture feature',args=>{args.compilerArtifact.features=['fixture'];}],
  ['test profile',args=>{args.compilerArtifact.profile.test=true;}],
  ['release optimization',args=>{args.compilerArtifact.profile.opt_level='3';}],
  ['extra filename',args=>{args.compilerArtifact.filenames.push(args.compilerArtifact.filenames[0]);}],
  ['top metadata route',args=>{args.compilerArtifact.filenames[1]=args.compilerArtifact.filenames[0].replace('.rlib','.rmeta');}],
  ['wrong metadata hash stem',args=>{args.compilerArtifact.filenames[1]=args.compilerArtifact.filenames[1].replace('3f30bf7410c0f3b3','not-a-cargo-hash');}],
  ['foreign archive output',args=>{args.compilerArtifact.filenames[0]=path.join(args.root,'foreign.rlib');}],
  ['retained evidence copy output',args=>{args.compilerArtifact.filenames[0]=path.join(args.root,'artifacts','normal-library.rlib');}],
  ['foreign target directory',args=>{args.normalTarget=path.join(args.root,'target','shared');}],
  ['wrong host target',args=>{args.hostTarget='aarch64-apple-darwin';}]
];
for(const [name,change] of malformed)test(`malformed current Cargo row refuses: ${name}`,t=>fixture(t,{},({args})=>{
  change(args);assert.throws(()=>selectNormalArchive(args));
}));
