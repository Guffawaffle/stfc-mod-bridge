import test from 'node:test';
import assert from 'node:assert/strict';
import {mkdtempSync,writeFileSync,mkdirSync,symlinkSync,lstatSync,realpathSync,rmSync} from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import {resolveHostTool} from '../host-tools.mjs';

const root=path.join(os.tmpdir(),'bridge-host-tool-model'), bin=path.join(root,'cargo','bin');
test('linked shim resolves the physical regular payload but preserves the Cargo invocation name',()=>{
  const route=path.join(bin,'cargo'), payload=path.join(bin,'rustup'), calls=[];
  assert.equal(resolveHostTool('cargo',{PATH:bin},{exists:value=>value===route,
    physical:value=>{calls.push(['physical',value]);return payload;},
    stat:value=>{calls.push(['stat',value]);return {isFile:()=>value===payload};}}),route);
  assert.deepEqual(calls,[['physical',route],['stat',payload]]);
  assert.notEqual(route,payload);
});
test('missing, nonregular and ambiguous routes refuse instead of qualifying an unknown tool',()=>{
  assert.throws(()=>resolveHostTool('cargo',{}),/one child PATH/);
  assert.throws(()=>resolveHostTool('cargo',{PATH:bin,Path:bin},{platform:'win32'}),/one child PATH/);
  assert.throws(()=>resolveHostTool('cargo',{PATH:bin},{exists:()=>false}),/cannot be resolved/);
  assert.throws(()=>resolveHostTool('cargo',{PATH:bin},{exists:()=>true,physical:value=>value,stat:()=>({isFile:()=>false})}),/cannot be resolved/);
  assert.throws(()=>resolveHostTool('cargo',{PATH:bin},{exists:()=>true,physical:()=>{throw new Error('changed link');}}),/changed link/);
});
test('relative PATH entries are skipped and absolute scoped invocation routes stay intact',()=>{
  const route=path.join(bin,'cargo');
  assert.equal(resolveHostTool('cargo',{PATH:['relative','',bin].join(path.delimiter)},{exists:value=>value===route,physical:value=>value,stat:()=>({isFile:()=>true})}),route);
  assert.equal(resolveHostTool(route,{}, {physical:()=>assert.fail('Outer observer owns scoped payload validation')}),route);
});
test('actual filesystem symlink to a regular payload keeps the shim route',t=>{
  const temporary=realpathSync(os.tmpdir()), leaf=mkdtempSync(path.join(temporary,'bridge-host-tool-link-'));
  try {
    const directory=path.join(leaf,'bin');mkdirSync(directory);
    const payload=path.join(directory,'rustup'), route=path.join(directory,'cargo');writeFileSync(payload,'Test-only inert payload; never executed.');
    try {symlinkSync(payload,route,'file');} catch(error) {if(process.platform==='win32'&&['EPERM','EACCES'].includes(error.code)){t.skip('This Windows host does not permit a synthetic file symlink.');return;}throw error;}
    assert.equal(lstatSync(route).isFile(),false);assert.equal(lstatSync(route).isSymbolicLink(),true);
    assert.equal(resolveHostTool('cargo',{PATH:directory}),route);
    assert.equal(realpathSync.native(route),realpathSync.native(payload));
  } finally {assert.equal(path.dirname(realpathSync(leaf)),temporary);assert.ok(path.basename(leaf).startsWith('bridge-host-tool-link-'));rmSync(leaf,{recursive:true,force:true});}
});
