import assert from 'node:assert/strict';
import {createHash} from 'node:crypto';
import {closeSync,fstatSync,lstatSync,openSync,readSync,realpathSync} from 'node:fs';
import path from 'node:path';
import {ownedArtifactPath} from './owned-artifact.mjs';

export const NORMAL_ARCHIVE_FILE_CAP=256*1024*1024;
const STAT_KEYS=Object.freeze(['dev','ino','nlink','size','mtimeNs','ctimeNs']);
const PROFILE=Object.freeze({opt_level:'0',debuginfo:2,debug_assertions:true,overflow_checks:true,test:false});
const selections=new WeakSet();
const sameStat=(left,right)=>STAT_KEYS.every(key=>left[key]===right[key]);
const sameFile=(left,right)=>left.dev===right.dev&&left.ino===right.ino;
const digest=bytes=>createHash('sha256').update(bytes).digest('hex');
const decimalStat=stat=>Object.freeze(Object.fromEntries(STAT_KEYS.map(key=>[key,stat[key].toString()])));
function freezeTree(value){
  if(value&&typeof value==='object'){
    for(const child of Object.values(value))freezeTree(child);
    Object.freeze(value);
  }
  return value;
}
function owned(root,absolute,kind='file'){
  assert.ok(typeof absolute==='string'&&path.isAbsolute(absolute),'Normal archive route must be absolute');
  const selected=ownedArtifactPath(root,path.relative(root,absolute),kind);
  assert.equal(selected,absolute,'Normal archive route must equal its physical owned route');
  assert.equal(realpathSync.native(absolute),absolute,'Normal archive physical route changed');
  return selected;
}
function routeObservation(selection,absolute){
  owned(selection.root,absolute);
  const stat=lstatSync(absolute,{bigint:true});
  assert.ok(stat.isFile()&&!stat.isSymbolicLink(),'Normal output must be a regular owned file');
  assert.ok(stat.dev>0n&&stat.ino>0n,'Normal output requires a nonzero physical file identity');
  assert.ok(stat.size>0n&&stat.size<=BigInt(NORMAL_ARCHIVE_FILE_CAP),'256MiB bound checked before allocation/read');
  return stat;
}
function closedCompilerArtifact(artifact){
  return freezeTree({reason:'compiler-artifact',package_id:artifact.package_id,manifest_path:artifact.manifest_path,
    target:{name:artifact.target.name,kind:[...artifact.target.kind],crate_types:[...artifact.target.crate_types],src_path:artifact.target.src_path},
    profile:{...PROFILE},features:[],filenames:[...artifact.filenames],executable:null,fresh:false});
}

// This selector is only for the fresh selected Windows dev-profile library
// archive. It grants no execution policy to a test executable or retained copy.
export function selectNormalArchive({root,normalTarget,hostTarget,platformPackage,compilerArtifact}){
  assert.ok(typeof root==='string'&&path.isAbsolute(root));
  assert.equal(realpathSync.native(root),root,'Canonical normal archive owner required');
  const rootStat=lstatSync(root);assert.ok(rootStat.isDirectory()&&!rootStat.isSymbolicLink());
  assert.equal(hostTarget,'x86_64-pc-windows-msvc');
  assert.ok(typeof normalTarget==='string'&&path.isAbsolute(normalTarget));
  const relation=path.relative(root,normalTarget).replaceAll('\\','/');
  assert.match(relation,/^target\/windows-private-journal-normal\/[a-f0-9]{8}-[a-f0-9]{4}-4[a-f0-9]{3}-[89ab][a-f0-9]{3}-[a-f0-9]{12}$/,
    'Normal archive requires its exact isolated UUIDv4 target');
  owned(root,normalTarget,'directory');
  assert.equal(platformPackage?.name,'bridge-platform-windows');assert.equal(typeof platformPackage.id,'string');assert.ok(platformPackage.id);
  const manifest=ownedArtifactPath(root,'crates/bridge-platform-windows/Cargo.toml');
  const source=ownedArtifactPath(root,'crates/bridge-platform-windows/src/lib.rs');
  assert.equal(platformPackage.manifest_path,manifest,'Normal package manifest is foreign');
  const artifact=compilerArtifact;
  assert.equal(artifact?.reason,'compiler-artifact');assert.equal(artifact.package_id,platformPackage.id,'Normal package identity differs from current metadata');
  assert.equal(artifact.manifest_path,manifest,'Normal Cargo manifest is foreign');
  assert.equal(artifact.target?.name,'bridge_platform_windows');assert.deepEqual(artifact.target.kind,['lib']);assert.deepEqual(artifact.target.crate_types,['lib']);
  assert.equal(artifact.target.src_path,source,'Normal Cargo source is foreign');
  assert.equal(artifact.executable,null,'Normal archive cannot be an executable');assert.equal(artifact.fresh,false,'Fresh isolated normal compilation required');
  assert.deepEqual(artifact.features,[]);assert.deepEqual(artifact.profile,PROFILE,'Normal archive requires the observed dev-profile settings');
  assert.ok(Array.isArray(artifact.filenames));assert.equal(artifact.filenames.length,2,'Require the exact Cargo-listed archive and metadata routes');
  assert.ok(artifact.filenames.every(file=>typeof file==='string'&&path.isAbsolute(file)));
  const profile=path.join(normalTarget,hostTarget,'debug');owned(root,profile,'directory');
  const selected=path.join(profile,'libbridge_platform_windows.rlib');
  const archives=artifact.filenames.filter(file=>file.endsWith('.rlib')),metadata=artifact.filenames.filter(file=>file.endsWith('.rmeta'));
  assert.equal(archives.length,1);assert.equal(metadata.length,1);assert.equal(archives[0],selected,'Cargo-selected archive route differs from its owned profile');
  assert.equal(path.dirname(metadata[0]),path.join(profile,'deps'));
  assert.match(path.basename(metadata[0]),/^libbridge_platform_windows-[a-f0-9]{16}\.rmeta$/,'Require the exact current Cargo metadata stem');
  const alias=metadata[0].slice(0,-'.rmeta'.length)+'.rlib';
  for(const route of [selected,alias,metadata[0]])owned(root,route);
  const selection=freezeTree({root,normalTarget,hostTarget,selected,alias,metadata:metadata[0],compilerArtifact:closedCompilerArtifact(artifact)});
  selections.add(selection);return selection;
}

function readExactly(fd,bytes){
  let offset=0;
  while(offset<bytes.length){
    const count=readSync(fd,bytes,offset,bytes.length-offset,offset);
    assert.ok(count>0,'Normal archive shortened during bounded read');offset+=count;
  }
}
function hashExactly(fd,length){
  const buffer=Buffer.alloc(Math.min(length,64*1024)),hash=createHash('sha256');let offset=0;
  while(offset<length){
    const count=readSync(fd,buffer,0,Math.min(buffer.length,length-offset),offset);
    assert.ok(count>0,'Normal archive copy shortened during bounded read');hash.update(buffer.subarray(0,count));offset+=count;
  }
  return hash.digest('hex');
}
function observedRoute(absolute,stat,sha256){
  return Object.freeze({route:absolute,physical:absolute,identity:decimalStat(stat),bytes:Number(stat.size),sha256});
}

// Both allowed archive names are opened read-only and fenced before/after.
// Ancillary standalone metadata is never opened, scanned, or alias-qualified.
// Reuse the selection and compare complete observations at every later fence.
export function readNormalArchive(selection){
  assert.ok(selections.has(selection),'Normal archive reader requires its own validated selection');
  const firstSelected=routeObservation(selection,selection.selected),firstAlias=routeObservation(selection,selection.alias);
  const firstMetadata=routeObservation(selection,selection.metadata);
  let policy;
  if(firstSelected.nlink===2n&&firstAlias.nlink===2n){
    assert.ok(sameFile(firstSelected,firstAlias),'Cargo two-link archive routes must share one physical file');
    assert.ok(sameStat(firstSelected,firstAlias),'Cargo two-link archive observations differ');policy='cargo-same-inode-two-link';
  }else{
    assert.equal(firstSelected.nlink,1n,'Unknown selected archive hard-link alias');assert.equal(firstAlias.nlink,1n,'Unknown deps archive hard-link alias');
    assert.ok(!sameFile(firstSelected,firstAlias),'Single-link archive copies must be distinct files');
    assert.equal(firstSelected.size,firstAlias.size,'Cargo archive copies must have equal bounded lengths');policy='cargo-distinct-single-link-copies';
  }
  let selectedFd,aliasFd;
  try{
    selectedFd=openSync(selection.selected,'r');aliasFd=openSync(selection.alias,'r');
    const openedSelected=fstatSync(selectedFd,{bigint:true}),openedAlias=fstatSync(aliasFd,{bigint:true});
    assert.ok(openedSelected.isFile()&&sameStat(firstSelected,openedSelected),'Selected archive route/FD identity changed before read');
    assert.ok(openedAlias.isFile()&&sameStat(firstAlias,openedAlias),'Deps archive route/FD identity changed before read');
    // Check both open resource bounds again before either allocation.
    for(const opened of [openedSelected,openedAlias])assert.ok(opened.size>0n&&opened.size<=BigInt(NORMAL_ARCHIVE_FILE_CAP),'256MiB bound checked before allocation/read');
    const bytes=Buffer.alloc(Number(openedSelected.size));readExactly(selectedFd,bytes);
    const sha256=digest(bytes),aliasSha256=policy==='cargo-same-inode-two-link'?sha256:hashExactly(aliasFd,Number(openedAlias.size));
    assert.equal(aliasSha256,sha256,'Cargo archive copy bytes differ');
    assert.ok(sameStat(openedSelected,fstatSync(selectedFd,{bigint:true})),'Selected open archive changed during bounded read');
    assert.ok(sameStat(openedAlias,fstatSync(aliasFd,{bigint:true})),'Deps open archive changed during bounded read');
    assert.ok(sameStat(openedSelected,routeObservation(selection,selection.selected)),'Selected archive route changed during bounded read');
    assert.ok(sameStat(openedAlias,routeObservation(selection,selection.alias)),'Deps archive route changed during bounded read');
    assert.ok(sameStat(firstMetadata,routeObservation(selection,selection.metadata)),'Ancillary metadata route changed during archive observation');
    const observation=freezeTree({schemaVersion:'bridge-windows-normal-archive-observation/v1',artifactRole:'isolated-normal-library-archive',executed:false,policy,
      compilerArtifact:selection.compilerArtifact,selected:observedRoute(selection.selected,openedSelected,sha256),alias:observedRoute(selection.alias,openedAlias,aliasSha256),
      ancillaryMetadata:{route:selection.metadata,physical:selection.metadata,identity:decimalStat(firstMetadata),bytes:Number(firstMetadata.size),scanned:false,aliasQualified:false},
      bytes:bytes.length,sha256,boundary:'selected normal rlib archive bytes only; no standalone metadata alias, execution, packaging or mapped-image attestation'});
    return {bytes,observation};
  }finally{
    try{if(aliasFd!==undefined)closeSync(aliasFd);}finally{if(selectedFd!==undefined)closeSync(selectedFd);}
  }
}
