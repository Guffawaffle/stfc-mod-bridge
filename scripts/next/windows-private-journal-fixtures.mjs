// Selected test-only journal fixtures; no external selectors or elevation fallback.
import assert from 'node:assert/strict';
import {spawnSync} from 'node:child_process';
import {createHash,randomUUID} from 'node:crypto';
import {openSync,closeSync,fstatSync,readSync,lstatSync,mkdirSync,realpathSync,writeFileSync} from 'node:fs';
import path from 'node:path';
import {performance} from 'node:perf_hooks';
import {fingerprintInputRecords} from './input-tree.mjs';
import {ownedArtifactPath} from './owned-artifact.mjs';
import {rustContext} from './rust-context.mjs';
import {resolveHostTool} from './host-tools.mjs';
import {selectHostArtifacts} from './host-artifacts.mjs';
import {NATIVE_TESTS} from './windows-private-journal-evidence.mjs';
import {parseJournalFixtureOutput,parseJournalFixtureSuite} from './windows-private-journal-context.mjs';
import {INVENTORY} from './windows-private-journal-inventory.mjs';

const root=path.resolve(import.meta.dirname,'../..');
const relative=value=>path.relative(root,value).replaceAll('\\','/');
const sha=value=>createHash('sha256').update(value).digest('hex');
const FILE_CAP=256*1024*1024, OUTPUT_CAP=8*1024*1024, SUITE_MS=540000;
let artifactCreationAttempted=false,nativeFixtureInvocationAttempted=false;
const inputs=Object.freeze([
  'Cargo.toml','Cargo.lock','rust-toolchain.toml','.cargo/config.toml','dependencies/next-toolchain.json',
  'docs/next/PRIVATE_JOURNAL_STORAGE.md','docs/next/WINDOWS_PLATFORM.md','docs/next/NATIVE_QUALIFICATION.md',
  '.github/workflows/next-foundation.yml','docs/next/campaign.json',
  'docs/plans/rust-tauri-cross-platform/work-packages.json','scripts/next',
  'crates/bridge-platform-windows','crates/bridge-journal-io','crates/bridge-domain',
  'crates/bridge-engine','crates/bridge-toml','crates/bridge-native','crates/bridge-contracts','contracts'
]);
class DriverBlocked extends Error {constructor(code){super(code);this.name='DriverBlocked';this.code=code;}}
const block=code=>{throw new DriverBlocked(code);};
function preflight(){
  assert.equal(process.argv.length,2,'No caller fixture arguments');
  assert.deepEqual(process.execArgv,[],'No caller Node loaders');
  assert.equal(process.platform,'win32');assert.equal(process.arch,'x64');
  assert.equal(realpathSync.native(process.cwd()),realpathSync.native(root),'Canonical owning cwd required');
  assert.ok(lstatSync(root).isDirectory()&&!lstatSync(root).isSymbolicLink());
  assert.equal(process.version,'v24.14.1');
  for(const [name,value] of Object.entries(process.env)){
    const key=name.toUpperCase();
    const reserved=/^(?:BRIDGE_(?:PRIVATE_JOURNAL_|FIXTURE_|TEST_WINDOWS_|WINDOWS_CHILD_|LOCAL_HOST_CHILD_|CONFIGURATION_)|RUST_TEST_|LIBTEST_)/.test(key)
      || /^(?:RUSTC|RUSTDOC|RUSTFMT|CARGO|RUSTC_WRAPPER|RUSTC_WORKSPACE_WRAPPER|RUSTFLAGS|RUSTDOCFLAGS|RUSTUP_TOOLCHAIN|RUSTC_BOOTSTRAP|CARGO_TARGET_DIR|CARGO_INCREMENTAL|CARGO_ENCODED_RUSTFLAGS|CARGO_ENCODED_RUSTDOCFLAGS)$/.test(key)
      || /^CARGO_(?:TARGET_|PROFILE_|BUILD_|ALIAS_)/.test(key)
      || /^(?:NODE_OPTIONS|NODE_PATH|TS_NODE_.*|TSX_.*|VITE_.*|VITEST_.*|ESBUILD_BINARY_PATH|ROLLDOWN_BINDING_PATH|NAPI_RS_NATIVE_LIBRARY_PATH|NPM_CONFIG_(?:NODE_OPTIONS|SCRIPT_SHELL)|__COMPAT_LAYER|LD_PRELOAD|LD_LIBRARY_PATH|DYLD_.*)$/.test(key);
    if(reserved&&value!==undefined&&value!=='') block('CALLER_EXECUTION_OVERRIDE');
  }
  if(process.env.BRIDGE_EXPECTED_HOST!==undefined) assert.equal(process.env.BRIDGE_EXPECTED_HOST,'windows-x64');
  if(process.env.BRIDGE_WORK_PACKAGE!==undefined) assert.equal(process.env.BRIDGE_WORK_PACKAGE,'br-06');
  if(process.env.BRIDGE_ISSUE!==undefined) assert.equal(process.env.BRIDGE_ISSUE,'241');
  assert.deepEqual(NATIVE_TESTS,INVENTORY.native9,'Parser and committed inventory must agree');
  assert.equal(INVENTORY.all38.length,38);assert.equal(new Set(INVENTORY.all38).size,38);
  assert.equal(INVENTORY.default28.length,28);assert.equal(INVENTORY.skip10.length,10);
  assert.deepEqual(INVENTORY.skip10,[INVENTORY.helper,...NATIVE_TESTS]);
  assert.deepEqual([...INVENTORY.default28,...INVENTORY.skip10].sort(),[...INVENTORY.all38].sort());
}

const sameStat=(a,b)=>['dev','ino','nlink','size','mtimeNs','ctimeNs'].every(key=>a[key]===b[key]);
function boundedFile(absolute,{privateArtifact=false}={}){
  const route=lstatSync(absolute,{bigint:true});assert.ok(route.isFile()&&!route.isSymbolicLink());
  const physical=realpathSync.native(absolute);
  if(privateArtifact){assert.equal(physical,absolute);assert.equal(route.nlink,1n,'Private artifact must have one link');}
  const fd=openSync(absolute,'r');
  try{
    const first=fstatSync(fd,{bigint:true});assert.ok(first.isFile());assert.ok(sameStat(route,first));
    assert.ok(first.size>0n&&first.size<=BigInt(FILE_CAP),'256MiB bound checked before allocation/read');
    const bytes=Buffer.alloc(Number(first.size));let offset=0;
    while(offset<bytes.length){const count=readSync(fd,bytes,offset,bytes.length-offset,offset);assert.ok(count>0,'Artifact shortened during read');offset+=count;}
    assert.ok(sameStat(first,fstatSync(fd,{bigint:true})),'Open file changed during bounded read');
    assert.ok(sameStat(first,lstatSync(absolute,{bigint:true})),'File route changed during bounded read');
    assert.equal(realpathSync.native(absolute),physical);
    return {bytes,observation:{route:absolute,physical,bytes:bytes.length,sha256:sha(bytes)}};
  }finally{closeSync(fd);}
}
function peHeaders(bytes){
  assert.ok(bytes.length>=64);assert.equal(bytes.readUInt16LE(0),0x5a4d);
  const pe=bytes.readUInt32LE(0x3c);assert.ok(pe>=64&&pe+24<=bytes.length);
  assert.equal(bytes.readUInt32LE(pe),0x4550);assert.equal(bytes.readUInt16LE(pe+4),0x8664);
  const sections=bytes.readUInt16LE(pe+6),optionalBytes=bytes.readUInt16LE(pe+20),characteristics=bytes.readUInt16LE(pe+22);
  assert.ok(sections>=1&&sections<=96);assert.ok((characteristics&2)!==0&&(characteristics&0x2000)===0);
  const optional=pe+24;assert.ok(optionalBytes>=112&&optional+optionalBytes<=bytes.length);
  assert.equal(bytes.readUInt16LE(optional),0x20b);
  const directories=bytes.readUInt32LE(optional+108);assert.ok(directories<=16&&112+8*directories<=optionalBytes);
  const sectionAlignment=bytes.readUInt32LE(optional+32),fileAlignment=bytes.readUInt32LE(optional+36);
  const imageBytes=bytes.readUInt32LE(optional+56),headerBytes=bytes.readUInt32LE(optional+60);
  assert.ok(fileAlignment>=512&&fileAlignment<=65536&&(fileAlignment&(fileAlignment-1))===0);
  assert.ok(sectionAlignment>=fileAlignment&&imageBytes>0&&imageBytes%sectionAlignment===0);
  const table=optional+optionalBytes;assert.ok(table+sections*40<=bytes.length&&table+sections*40<=headerBytes&&headerBytes<=bytes.length);
  const sectionRows=[];
  for(let index=0;index<sections;index++){
    const selected=table+index*40,virtualBytes=bytes.readUInt32LE(selected+8),rva=bytes.readUInt32LE(selected+12);
    const rawBytes=bytes.readUInt32LE(selected+16),rawOffset=bytes.readUInt32LE(selected+20);
    assert.ok(rva+Math.max(virtualBytes,rawBytes)<=imageBytes);
    if(rawBytes) assert.ok(rawOffset>=headerBytes&&rawOffset+rawBytes<=bytes.length);
    sectionRows.push({virtualBytes,rva,rawBytes,rawOffset});
  }
  return {architecture:'windows-x64-pe32+',peOffset:pe,optionalHeaderBytes:optionalBytes,sections:sectionRows,headerBytes,imageBytes,directories};
}
const text=bytes=>{const value=new TextDecoder('utf-8',{fatal:true,ignoreBOM:true}).decode(bytes);assert.ok(!value.startsWith('\uFEFF'));return value;};
function cargoMessages(output){
  const lines=text(output).split(/\r?\n/).filter(Boolean);assert.ok(lines.length<=8192);
  const messages=lines.map(line=>JSON.parse(line));const finished=messages.filter(item=>item.reason==='build-finished');
  assert.equal(finished.length,1);assert.equal(finished[0].success,true);return messages;
}
function summary(output,expected){
  const rows=[...output.matchAll(/^test result: ok\. (\d+) passed; (\d+) failed; (\d+) ignored; (\d+) measured; (\d+) filtered out;[^\r\n]*\r?$/gm)];
  assert.equal(rows.length,1);assert.deepEqual(rows[0].slice(1).map(Number),expected);
}
function ownershipSource(){
  const expected=[['filesystem.rs','ReadOnlyFile','Send'],['filesystem.rs','AdmittedFileGuard','Sync'],['process.rs','ExactProcessGuard','Send'],['private_journal.rs','NativePrivateJournalStorage','Send'],['private_journal.rs','NativePrivateJournalStorage','Sync']];
  return expected.map(([name,type,trait])=>{
    const file=`crates/bridge-platform-windows/src/${name}`,source=text(boundedFile(ownedArtifactPath(root,file)).bytes).split(/\r?\n/),found=[];
    for(let index=0;index<source.length;index++) if(source[index].trim()==='/// ```compile_fail'){
      const body=[];let last=index+1;while(last<source.length&&source[last].trim()!=='/// ```'){assert.ok(source[last].trim().startsWith('///'));body.push(source[last].trim().slice(3).trim());last++;}
      if(body.join('\n')===`fn require_${trait.toLowerCase()}<T: ${trait}>() {}\nrequire_${trait.toLowerCase()}::<bridge_platform_windows::${type}>();`) found.push(index+1);
    }
    assert.equal(found.length,1,'Exact ownership source block required');
    return {path:file,symbol:`${name.slice(0,-3)}::${type}`,trait,blockLine:found[0]};
  });
}
function parseOwnership(output,blocks){
  summary(output,[5,0,0,0,0]);
  const rows=output.split(/\r?\n/).filter(line=>line.endsWith(' - compile fail ... ok'));
  assert.equal(rows.length,5);
  const actual=rows.map(row=>{const match=/^test (.+) - (\S+) \(line (\d+)\) - compile fail \.\.\. ok$/.exec(row);assert.ok(match);return {path:match[1].replaceAll('\\','/'),symbol:match[2],blockLine:Number(match[3])};});
  for(const block of blocks) assert.equal(actual.filter(row=>row.path===block.path&&row.symbol===block.symbol&&row.blockLine===block.blockLine).length,1,'Current fenced doctest block actually executed');
  return {tests:5,sourceBlocks:blocks,executed:actual};
}

async function main(){
  preflight();
  // Each selected native test observes its own process/current-thread token
  // before journal effects. This driver makes no claim about Node's token.
  const rust=rustContext({root});assert.equal(rust.pin,'1.99.0');assert.equal(rust.hostTarget,'x86_64-pc-windows-msvc');
  const metadata=text(boundedFile(ownedArtifactPath(root,'dependencies/next-toolchain.json')).bytes);
  assert.equal(JSON.parse(metadata).node,'24.14.1');
  const sourcesBefore=fingerprintInputRecords(root,inputs),checks=[],toolsBefore=[],binaries=[],nativeOutputs=[];
  const startedAt=new Date().toISOString(),deadline=performance.now()+SUITE_MS,id=randomUUID();
  const directory=ownedArtifactPath(root,`artifacts/next/windows-private-journal-fixtures/${id}`,'directory',{allowMissing:true});
  artifactCreationAttempted=true;
  mkdirSync(directory,{recursive:true});ownedArtifactPath(root,relative(directory),'directory');
  const save=(name,data)=>{const bytes=Buffer.isBuffer(data)?data:Buffer.from(data);const selected=ownedArtifactPath(root,`${relative(directory)}/${name}`,'file',{allowMissing:true});writeFileSync(selected,bytes,{flag:'wx'});return {path:relative(selected),bytes:bytes.length,sha256:sha(bytes)};};
  let toolsAfter=[],sourcesAfter=null,headBefore=null,headAfter=null,treeBefore=null,treeAfter=null,failure=null,normalExclusion=null,ownershipDocs=null,suite=null,passed=false;
  const tool=(role,route)=>({...boundedFile(realpathSync.native(route)).observation,role,route,physical:realpathSync.native(route)});
  const fence=()=>{
    assert.deepEqual(fingerprintInputRecords(root,inputs),sourcesBefore,'Source changed');
    assert.deepEqual(toolsBefore.map(item=>tool(item.role,item.route)),toolsBefore,'Tool routes/bytes changed');
    for(const binary of binaries){
      const observed=boundedFile(ownedArtifactPath(root,relative(binary.executable)),{privateArtifact:true}).observation;
      assert.equal(observed.bytes,binary.bytes);assert.equal(observed.sha256,binary.sha256);
      const retained=boundedFile(ownedArtifactPath(root,binary.retainedCopy.path),{privateArtifact:true}).observation;
      assert.equal(retained.bytes,binary.bytes);assert.equal(retained.sha256,binary.sha256);assert.equal(binary.retainedCopy.executed,false);
    }
  };
  function run(check,executable,argv,{timeout=180000,env=rust.env}={}){
    fence();const remaining=Math.floor(deadline-performance.now());assert.ok(remaining>0,'Finite suite deadline expired');
    const commandStart=new Date().toISOString();const result=spawnSync(executable,argv,{cwd:root,env,windowsHide:true,timeout:Math.min(timeout,remaining),maxBuffer:OUTPUT_CAP,encoding:null});
    const stdout=result.stdout??Buffer.alloc(0),stderr=result.stderr??Buffer.alloc(0);
    // Captured over-budget output can only produce failure; retain captured raw
    // bytes under a separate finite 16MiB failure ceiling, never passing truncation.
    assert.ok(stdout.length+stderr.length<=2*OUTPUT_CAP,'Captured failure output exceeds retained ceiling');
    const observation={id:check,executable,argv,cwd:root,startedAt:commandStart,completedAt:new Date().toISOString(),
      pid:result.pid??null,exitCode:result.status,signal:result.signal??null,error:result.error?.code??null,
      outputLimitBytes:OUTPUT_CAP,outputBoundSatisfied:stdout.length+stderr.length<=OUTPUT_CAP,captureComplete:!result.error,
      stdout:save(`${check}.stdout.log`,stdout),stderr:save(`${check}.stderr.log`,stderr)};
    checks.push({...observation,observation:save(`${check}.json`,JSON.stringify(observation,null,2)+'\n')});
    console.log(JSON.stringify({check,exitCode:result.status,error:observation.error}));
    fence();assert.equal(result.status,0);assert.equal(result.signal,null);assert.ok(!result.error&&observation.outputBoundSatisfied);assert.ok(Number.isInteger(result.pid)&&result.pid>0,'Actual spawned process identity required');
    return {stdout,stderr,observation};
  }
  try{
    const cargoPath=resolveHostTool(rust.cargo,rust.env),rustcPath=resolveHostTool(rust.rustc,rust.env),rustdocPath=resolveHostTool(rust.env.RUSTDOC,rust.env),gitPath=resolveHostTool('git.exe',rust.env);
    toolsBefore.push(tool('node',process.execPath),tool('cargo-route',cargoPath),tool('rustc-route',rustcPath),tool('rustdoc-route',rustdocPath),tool('git-route',gitPath));
    headBefore=text(run('head-before',gitPath,['--no-optional-locks','rev-parse','HEAD'],{timeout:10000}).stdout).trim();assert.match(headBefore,/^[0-9a-f]{40}$/);
    treeBefore=text(run('tree-before',gitPath,['--no-optional-locks','rev-parse','HEAD^{tree}'],{timeout:10000}).stdout).trim();assert.match(treeBefore,/^[0-9a-f]{40}$/);
    assert.equal(text(run('status-before',gitPath,['--no-optional-locks','status','--porcelain=v1'],{timeout:10000}).stdout),'','Clean tracked candidate required');
    const compiler=text(run('rustc-version',rustcPath,[`+${rust.pin}`,'-vV']).stdout);assert.ok(compiler.startsWith(`rustc ${rust.pin} `));assert.ok(compiler.split(/\r?\n/).includes(`host: ${rust.hostTarget}`));
    const cargoVersion=text(run('cargo-version',cargoPath,[`+${rust.pin}`,'--version']).stdout);assert.ok(cargoVersion.startsWith(`cargo ${rust.pin} `));
    const rustdocVersion=text(run('rustdoc-version',rustdocPath,[`+${rust.pin}`,'--version']).stdout);assert.ok(rustdocVersion.startsWith(`rustdoc ${rust.pin} `));
    const sysroot=text(run('rustc-sysroot',rustcPath,[`+${rust.pin}`,'--print','sysroot']).stdout).trim();assert.ok(path.isAbsolute(sysroot)&&!/[\r\n]/.test(sysroot));
    for(const name of ['cargo','rustc','rustdoc','rustfmt','cargo-fmt','cargo-clippy','clippy-driver']) toolsBefore.push(tool(`toolchain-${name}`,path.join(sysroot,'bin',`${name}.exe`)));
    const payload=name=>toolsBefore.find(item=>item.role===`toolchain-${name}`).physical;
    rust.env.RUSTC=payload('rustc');rust.env.RUSTDOC=payload('rustdoc');rust.env.RUSTFMT=payload('rustfmt');rust.env.CARGO=payload('cargo');
    const cargo=(check,argv,options)=>run(check,cargoPath,[`+${rust.pin}`,...argv],options);
    run('format',payload('cargo-fmt'),['fmt','-p','bridge-platform-windows','--','--check']);
    run('clippy',payload('cargo-clippy'),['clippy','--locked','--offline','-p','bridge-platform-windows','--all-targets','--target',rust.hostTarget,'--','-D','warnings']);
    const packageMetadata=JSON.parse(text(cargo('metadata',['metadata','--locked','--offline','--format-version','1','--filter-platform',rust.hostTarget]).stdout));
    const packageMap=new Map(packageMetadata.packages.map(item=>[item.id,item])),platform=packageMetadata.packages.filter(item=>item.name==='bridge-platform-windows');assert.equal(platform.length,1);
    const platformPackage=platform[0];assert.equal(realpathSync.native(platformPackage.manifest_path),ownedArtifactPath(root,'crates/bridge-platform-windows/Cargo.toml'));
    assert.deepEqual(Object.keys(platformPackage.features),[],'No public fixture feature');
    for(const name of ['bridge-engine','bridge-contracts']){
      const edges=platformPackage.dependencies.filter(item=>item.name===name);assert.equal(edges.length,1);assert.equal(edges[0].kind,'dev');assert.equal(edges[0].target,'cfg(windows)');
    }
    const windowsNormal=platformPackage.dependencies.filter(item=>item.name==='windows'&&item.kind===null),windowsDev=platformPackage.dependencies.filter(item=>item.name==='windows'&&item.kind==='dev');
    assert.equal(windowsNormal.length,1);assert.equal(windowsDev.length,1);assert.equal(windowsDev[0].target,'cfg(windows)');
    assert.equal(windowsDev[0].req,'=0.62.2');assert.deepEqual(windowsDev[0].features,['Win32_System_JobObjects']);assert.ok(!windowsNormal[0].features.includes('Win32_System_JobObjects'));
    const normalTree=text(cargo('normal-edges',['tree','--locked','--offline','--no-default-features','-p','bridge-platform-windows','--target',rust.hostTarget,'--edges','normal,build']).stdout);
    const normalFeatures=text(cargo('normal-features',['tree','--locked','--offline','--no-default-features','-p','bridge-platform-windows','--target',rust.hostTarget,'--edges','normal,build,features','--format','{p} [{f}]']).stdout);
    assert.ok(!/\bbridge-engine\b/.test(normalTree)&&!normalFeatures.includes('Win32_System_JobObjects'));assert.match(normalTree,/\bbridge-contracts v/,'Contracts remain expected indirect normal dependency');
    const normalTarget=ownedArtifactPath(root,`target/windows-private-journal-normal/${id}`,'directory',{allowMissing:true});mkdirSync(normalTarget,{recursive:true});ownedArtifactPath(root,relative(normalTarget),'directory');
    const normalEnv={...rust.env,CARGO_TARGET_DIR:normalTarget};
    const normalCompile=cargo('normal-library',['build','--locked','--offline','--no-default-features','-p','bridge-platform-windows','--lib','--target',rust.hostTarget,'--message-format','json'],{env:normalEnv});
    const normalMessages=cargoMessages(normalCompile.stdout),normalArtifacts=normalMessages.filter(item=>item.reason==='compiler-artifact');
    for(const item of normalArtifacts){
      const pkg=packageMap.get(item.package_id);assert.ok(pkg,'Current Cargo metadata package identity required');assert.notEqual(pkg.name,'bridge-engine');
      if(pkg.name==='windows') assert.ok(!item.features.includes('Win32_System_JobObjects'));
      assert.equal(item.profile.test,false);
    }
    const ownNormal=normalArtifacts.filter(item=>item.package_id===platformPackage.id&&item.target.name==='bridge_platform_windows');assert.equal(ownNormal.length,1);
    assert.deepEqual(ownNormal[0].target.kind,['lib']);assert.equal(ownNormal[0].executable,null);assert.deepEqual(ownNormal[0].features,[]);
    assert.equal(realpathSync.native(ownNormal[0].target.src_path),ownedArtifactPath(root,'crates/bridge-platform-windows/src/lib.rs'));
    assert.ok(ownNormal[0].filenames.length>=1&&ownNormal[0].filenames.length<=8);
    const normalFiles=ownNormal[0].filenames.filter(file=>/\.(?:rlib|rmeta)$/.test(file));assert.equal(normalFiles.filter(file=>file.endsWith('.rlib')).length,1);
    const needles=['STFCModBridgeNextFixtures','BRIDGE_PRIVATE_JOURNAL_FIXTURE ','BRIDGE_PRIVATE_JOURNAL_CHILD ',INVENTORY.helper,...NATIVE_TESTS,
      'BRIDGE_WINDOWS_JOURNAL_CONTEXT ','bridge-windows-journal-context/v1','native_journal_preflight','observe_native_journal_context','CreateJobObjectW','SetInformationJobObject'];
    const absent=[];
    for(const filename of normalFiles){
      const relation=path.relative(normalTarget,filename);assert.ok(relation&&!relation.startsWith('..')&&!path.isAbsolute(relation));
      const executable=ownedArtifactPath(root,relative(filename)),read=boundedFile(executable,{privateArtifact:true});
      for(const needle of needles) for(const encoding of ['utf8','utf16le']) assert.equal(read.bytes.includes(Buffer.from(needle,encoding)),false,'Fixture byte route leaked into isolated normal library');
      const retainedCopy={...save(`normal-library-${absent.length+1}${path.extname(filename)}`,read.bytes),executed:false};
      const record={target:'isolated-normal-library',executable,bytes:read.observation.bytes,sha256:read.observation.sha256,retainedCopy,compilerArtifact:ownNormal[0]};binaries.push(record);absent.push(record);
    }
    const ownerSource=text(boundedFile(ownedArtifactPath(root,'crates/bridge-platform-windows/src/private_journal.rs')).bytes);
    assert.match(ownerSource,/#\[cfg\(test\)\]\s*mod native_fixtures;/);assert.match(ownerSource,/pub fn open\(\) -> Result<Self, StorageFailure>\s*\{\s*Self::open_namespace\(Namespace::Production\)/);
    normalExclusion={isolatedTarget:relative(normalTarget),directEngineEdge:'dev-only',directContractsEdge:'dev-only',contractsIndirectNormal:true,jobObjectsNormalFeature:false,normalEdgesObserved:true,normalFeaturesObserved:true,compilerArtifactsObserved:true,cfgTestSourceObserved:true,needleEncoding:['utf8','utf16le'],needles,artifacts:absent,boundary:'selected normal library exclusion only; no full application packaging or mapped-image attestation'};
    const compile=cargo('compile-current-tests',['test','--locked','--offline','-p','bridge-platform-windows','--lib','--target',rust.hostTarget,'--no-run','--message-format','json']);
    cargoMessages(compile.stdout);
    const selected=selectHostArtifacts(text(compile.stdout),{root,targets:[{name:'bridge_platform_windows',kind:'lib',manifest:'crates/bridge-platform-windows/Cargo.toml',source:'crates/bridge-platform-windows/src/lib.rs'}]});assert.equal(selected.length,1);
    const artifact=selected[0];assert.equal(artifact.package_id,platformPackage.id);assert.deepEqual(artifact.features,[]);
    const relation=relative(artifact.executable);assert.ok(relation.startsWith(`target/${rust.hostTarget}/`));
    const executable=ownedArtifactPath(root,relation),read=boundedFile(executable,{privateArtifact:true}),headers=peHeaders(read.bytes);
    for(const needle of needles.slice(0,4)) assert.ok(read.bytes.includes(Buffer.from(needle,'utf8')),'Positive test-artifact fixture byte witness required');
    const retainedCopy={...save('compiled-library.test-artifact',read.bytes),executed:false};
    const binary={target:'bridge_platform_windows',architecture:headers.architecture,executable,bytes:read.observation.bytes,sha256:read.observation.sha256,retainedCopy,headers,compilerArtifact:artifact,discoveredTests:[],executedDefault:[]};binaries.push(binary);
    const inventory=text(run('list-current-tests',executable,INVENTORY.listArgs,{timeout:60000}).stdout);
    const lines=inventory.split(/\r?\n/).filter(Boolean),names=lines.filter(line=>line.endsWith(': test')).map(line=>line.slice(0,-6));
    assert.equal(new Set(names).size,names.length);assert.deepEqual([...names].sort(),[...INVENTORY.all38].sort());assert.equal(lines.at(-1),'38 tests, 0 benchmarks');assert.equal(lines.length,39);binary.discoveredTests=names;
    const defaultRun=text(run('execute-default28',executable,INVENTORY.defaultArgs,{timeout:90000}).stdout);summary(defaultRun,[28,0,0,0,10]);
    const completed=defaultRun.split(/\r?\n/).filter(line=>/^test \S+ \.\.\. ok$/.test(line)).map(line=>line.slice(5,-7));assert.deepEqual([...completed].sort(),[...INVENTORY.default28].sort());binary.executedDefault=completed;
    const blocks=ownershipSource(),docs=text(cargo('ownership-docs',['test','--locked','--offline','-p','bridge-platform-windows','--target',rust.hostTarget,'--doc','--','--color','never']).stdout);ownershipDocs=parseOwnership(docs,blocks);
    const parserArtifact={sha256:binary.sha256,bytes:binary.bytes};
    for(let index=0;index<NATIVE_TESTS.length;index++){
      const testName=NATIVE_TESTS[index];assert.notEqual(testName,INVENTORY.helper);
      nativeFixtureInvocationAttempted=true;
      const observed=run(`native-${index+1}`,executable,['--ignored','--exact',testName,'--test-threads=1','--nocapture','--color','never'],{timeout:60000});
      const parsed=parseJournalFixtureOutput(observed.stdout,{testName,artifact:parserArtifact,spawnedPid:observed.observation.pid});
      checks.at(-1).parsed=save(`native-${index+1}.parsed.json`,JSON.stringify(parsed,null,2)+'\n');nativeOutputs.push({testName,stdout:observed.stdout,spawnedPid:observed.observation.pid});
    }
    suite=parseJournalFixtureSuite(nativeOutputs,parserArtifact);assert.equal(suite.results.reduce((count,item)=>count+item.rows.length,0),27);assert.equal(suite.reportedUniqueNonces.length,25);
    fence();headAfter=text(run('head-after',gitPath,['--no-optional-locks','rev-parse','HEAD'],{timeout:10000}).stdout).trim();treeAfter=text(run('tree-after',gitPath,['--no-optional-locks','rev-parse','HEAD^{tree}'],{timeout:10000}).stdout).trim();
    assert.equal(headAfter,headBefore);assert.equal(treeAfter,treeBefore);assert.equal(text(run('status-after',gitPath,['--no-optional-locks','status','--porcelain=v1'],{timeout:10000}).stdout),'');
    sourcesAfter=fingerprintInputRecords(root,inputs);toolsAfter=toolsBefore.map(item=>tool(item.role,item.route));fence();passed=true;
  }catch(error){failure={name:error?.name??'Error',code:error?.code??null,message:String(error?.message??'Windows fixture observation failed').slice(0,4096)};}
  finally{
    if(sourcesAfter===null) try{sourcesAfter=fingerprintInputRecords(root,inputs);}catch(error){sourcesAfter={unavailable:error?.code??error?.name??'Error'};}
    if(toolsAfter.length===0) try{toolsAfter=toolsBefore.map(item=>tool(item.role,item.route));}catch(error){toolsAfter={unavailable:error?.code??error?.name??'Error'};}
    const receipt=save('windows-private-journal-fixtures.json',JSON.stringify({schemaVersion:'bridge-windows-private-journal-fixture-observation/v1',result:passed?'passed':'failed',startedAt,completedAt:new Date().toISOString(),
      host:{platform:process.platform,architecture:process.arch,node:process.version},toolchain:{pin:rust.pin,target:rust.hostTarget},source:{headBefore,headAfter,treeBefore,treeAfter},inputs,sourcesBefore,sourcesAfter,toolsBefore,toolsAfter,checks,binaries,ownershipDocs,normalExclusion,suite,failure,
      beforeJournalEffectsOrdinaryUserObserved:passed&&suite?.contexts.length===9,nodeCallerTokenDirectlyObserved:false,fixtureSubsetObserved:passed,normalLibraryExclusionObserved:normalExclusion!==null,
      markerCount:passed?27:null,reportedNonceCount:passed?25:null,actualTreeEnumeration:false,fixtureTreeRetentionPolicy:'retain-all-without-enumeration',
      foreignOwner:'not_observed',deferred:['reparse_hard_link','writable_mapping','broader_bounds_custody_loss','known_folder_configuration_redirection'],
      noDestructorKillAttested:false,mappedImageAttested:false,hardwarePowerLossQualified:false,packageAcceptance:false,fullBr06Accepted:false,privateJournalOwnerQualified:false,nativeRuntimeQualified:false,installedGameQualified:false,releaseQualified:false,
      boundary:'nine fixed test-only Windows private-journal fixture cases with actual raw command/source/tool/binary observations; no production owner or complete matrix qualification'
    },null,2)+'\n');
    console.log(JSON.stringify({result:passed?'passed':'failed',receipt,packageAcceptance:false,privateJournalOwnerQualified:false,nativeRuntimeQualified:false,releaseQualified:false}));process.exitCode=passed?0:1;
  }
}
main().catch(error=>{console.error(JSON.stringify({result:artifactCreationAttempted?'failed-without-complete-receipt':'blocked-before-artifacts',code:error?.code??'DRIVER_OBSERVATION_BLOCKED',artifactCreationAttempted,nativeFixtureInvocationAttempted,fixtureSubsetObserved:false,packageAcceptance:false,privateJournalOwnerQualified:false,nativeRuntimeQualified:false,releaseQualified:false}));process.exitCode=1;});
