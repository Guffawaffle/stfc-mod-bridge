import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createHash,randomUUID } from 'node:crypto';
import { mkdirSync,readFileSync,readdirSync,realpathSync,writeFileSync } from 'node:fs';
import path from 'node:path';
import { ownedArtifactPath } from './owned-artifact.mjs';
import { vitestEvidence } from './test-evidence.mjs';
const root=path.resolve(import.meta.dirname,'../..');assert.equal(realpathSync(process.cwd()),realpathSync(root));assert.equal(process.argv.length,2);
assert.equal(process.version,`v${JSON.parse(readFileSync(path.join(root,'package.json'))).engines.node}`);
const required={
  'ui/tests/settings/schema-presentation.test.ts':[
    'schema drives categories and search terms including aliases without a handwritten field list','unobserved baseline never pretends an effective default was observed',
    'sparse remove restores provider default while saved value and unrelated edits remain preserved','complete staged apply timings include Settings and Data Sync together',
    'private and secret presentation never emits opaque handle IDs or protected contents','platform availability and backend validation are explicit per schema field',
    'legacy feed timing stays next launch despite unrelated hidden mode fields','proxy destination timing stays next launch despite protected field role timings'],
  'ui/tests/settings/schema-controller.test.ts':[
    'public edits replace one field atomically and retain unrelated full-set Data Sync intent','unknown enum, oversized strings, unsupported keys and sounds refuse without replacing existing intent',
    'integer and exact decimal partial input retain shared dirty custody until valid completion','field removal and undo preserve other edits and create no wire operation',
    'refresh same bound document preserves the exact draft and avoids opening another draft','late configuration observation after target transition cannot open or bind the previous target',
    'capture cancellation preserves existing edits and does not fabricate protected reference','a refreshed target identity cannot relabel or mutate retained old draft edits',
    'unavailable observation retains the draft and does not open a replacement','externally replaced document marks conflict and preserves exact current edits',
    'a changed Bridge host blocks field and protected entry while retaining exact draft edits and numeric text','a clean same-document refresh reopens the current host draft instead of retaining old host custody'],
  'ui/tests/settings/schema-controls.test.ts':[
    'all public semantic field types render native labelled controls driven by the schema','unfinished numeric input exposes invalid text with a labelled error and reset action',
    'notification channels preserve separate native system/audio toggles and provider sound options','Settings shows target labels, categories, search, full-set staged count and mixed apply notices',
    'Data Sync exposes creatable modes and hides dormant modes without activating network work','loading state is visible and keeps mutation controls disabled until a bound observation arrives',
    'failed Save synchronization leaves editing visible and exposes the retained outcome','no-change and stale states remain distinguishable with explicit retained draft notices',
    'both configuration views announce old host custody and disable retained draft mutation'],
  'ui/tests/settings/sync-controller.test.ts':[
    'existing destination feeds and proxy stage sparse changes in the shared full set','schema rejects unpublished feeds, unsupported global proxy and hidden destination mutation',
    'destination removal is staged only and an existing removal can be undone','creation consumes exact protected role captures into one typed mode-bound edit preserving Settings',
    'creation requires both protected captures, refuses non-creatable and hidden modes without partial changes','creation refuses stale captured draft revision and removing new destination cancels only staged creation'],
  'ui/tests/settings-preview/session.test.ts':[
    'Settings preview modes bind every source hash and validate every recorded wire frame','Settings preview Save remains admitted until exact completion then reopens authoritative clean baseline',
    'Settings preview unfinished numeric text survives view changes and blocks Save until deliberate reset','Settings preview Discard confirmation and Stay retain or clear only the reviewed draft',
    'Settings preview protected captures create one mode-bound destination then stage feed and proxy in the shared draft','Settings preview lost delivery permits only exact explicit replay and then authoritative completion',
    'Settings preview failed and stale Save preserve synchronized edits and permit editing without implicit retry','Settings preview disposal ends owned clocks and observations without accepting replacement session state',
    'Settings preview structural Sync acknowledgements keep next-launch timing for add remove feed and proxy']};
// Literal acceptance inventory captured from the frozen root-owned custody suites.
Object.assign(required,{
  "ui/tests/settings/in-place-custody.test.ts": [
    "in-place review captures purpose without queued navigation and rejects foreign review ownership",
    "in-place Save requires explicit confirmation and admission retains the selected draft and view",
    "completed in-place Save reopens only the backend-observed document binding under the original target",
    "authoritative in-place no-change completion reopens the observed virtual baseline without fabricating a file",
    "in-place sent rejection and replay rejection retain exact uncertain custody until authoritative completion",
    "in-place timeout and proved-unsent replay cannot release uncertainty or permit Stay",
    "proved-unsent in-place Stay releases only its owned replay and restores the registered opener",
    "two completed in-place Saves retain bounded one-entry replay capacity",
    "in-place Discard reviews before delivery and Stay sends no discard command",
    "confirmed in-place Discard preserves selection and view then reopens the authoritative baseline",
    "foreign in-place Discard receipt preserves the draft and sends no baseline reopen",
    "failed baseline observation preserves confirmed Save while exposing unavailable editor data and explicit retry",
    "foreign baseline target refuses reopening without fabricating a draft or changing the selected target",
    "in-place double input cannot start competing Save Discard or action preparations",
    "reentrant disposal during reopened draft observation cannot adopt late editor state",
    "in-place baseline queries never manufacture an event cursor or resnapshot confidence",
    "in-place synchronization refuses an omitted protected-reference transfer and retains the old draft",
    "in-place replay conflict and Stay preserve another callers preexisting uncertain capture",
    "in-place terminal failure retains the reviewed draft and never starts a baseline reopen",
    "a late baseline token cannot release or replace a newer observation owner",
    "synchronous disposal before the facade records a new review releases only that review owner",
    "synchronous disposal at confirmed completion releases the reserved baseline observation without reopening",
    "reentrant host replacement during baseline draft observation preserves confirmed Save and required refresh",
    "reentrant target binding replacement during baseline draft observation preserves confirmed Save and required refresh"
  ],
  "ui/tests/settings/public-input-custody.test.ts": [
    "incomplete public numeric text makes an otherwise clean draft dirty without inventing typed edits",
    "Settings and Data Sync view navigation retains the exact incomplete numeric input and scope",
    "a target change with only unfinished numeric text queues review and Stay preserves the original target and opener",
    "close with only unresolved public text cannot bypass Save Discard Stay custody",
    "both navigation and in-place Save preparation refuse unresolved numeric buffers without sending",
    "published integer and decimal range failures retain their exact text and do not stage or round",
    "atomic public integer resolution preserves values above Number safe precision exactly",
    "canonical decimal resolution compares negative and fractional bounds with BigInt precision",
    "noncanonical numeric text remains buffered rather than being coerced by frontend presentation",
    "foreign draft revision document schema field type and sensitivity cannot receive a public input",
    "private secret and nonnumeric public fields never expose the public numeric text entry API",
    "public input capture refuses duplicate field definitions or mismatched document schema binding",
    "public buffer size Unicode and accessor refusals leave prior text intact and invoke no conversion hook",
    "the numeric buffer count is bounded while updating an existing buffer at capacity remains possible",
    "stale same-draft callbacks cannot overwrite newer text or clear buffers after another full edit change",
    "resolution requires exact buffered text and one matching typed public numeric edit",
    "atomic resolution publishes typed edits and buffer removal together before reentrant target navigation",
    "resolving one numeric field retains other buffered fields and nonnumeric edits",
    "external schema draft and document conflict observations cannot overwrite unfinished public text",
    "a successful explicit draft-open acknowledgement cannot overwrite retained unfinished input without confirmed intent",
    "Discard review freezes the captured public buffers and Stay sends nothing while retaining them",
    "public input APIs refuse competing action preparation without erasing unfinished numeric text",
    "refused or foreign Discard acknowledgement retains captured public text and typed edits",
    "exact navigation Discard acknowledgement clears only captured old buffers then applies the queued target",
    "exact in-place Discard clears old public buffers and reopens only the authoritative baseline under the same target",
    "a foreign review token cannot clear buffers or release a newer owned Discard attempt",
    "even a schema-valid completed Save observation cannot clear a review containing unresolved numeric text",
    "an explicit Reset removes only the current captured numeric text and never discards other staged edits",
    "backend field validation remains authoritative after exact public numeric presentation resolution",
    "facade disposal explicitly retains owned raw buffers and refuses subsequent input mutation",
    "reentrant disposal during public buffer publication cannot clear text or authorize a later mutation",
    "dispose during an in-flight Discard observation preserves exact captured public text and queued close"
  ],
  "ui/tests/client-draft-acknowledgement.test.ts": [
    "protected golden uses explicit private/secret transfers without plaintext or input mutation",
    "closed transfers are order independent while accepted edit order stays exact",
    "saved private document reference is preserved without transfer",
    "distinct private sources and fresh destinations each consume their own transfer",
    "a fresh private destination cannot alias a saved handle elsewhere in the accepted edits",
    "separate set_private and replace_secret references use the same closed transfer rules",
    "one protected private handle reused in two proxy positions needs one used transfer",
    "a custom proxy inside add_sync_destination consumes its explicit private transfer",
    "a saved private custom proxy is preserved without an additional transfer",
    "a private successor cannot alias another source that is also transferred",
    "successor generation above the JavaScript integer boundary uses exact decimal arithmetic",
    "the final representable successor succeeds but a saturated generation cannot wrap",
    "refuses a well-scoped but unused transfer",
    "refuses duplicate source handles",
    "refuses duplicate destination handles even when both transfers are used",
    "generated wire boundary refuses plaintext smuggled into a reference",
    "accepts Rust None equivalence in a private transfer document binding",
    "accepts Rust None equivalence in a secret successor draft binding",
    "accepts Rust None equivalence when Rust echoes accepted protected reference bindings",
    "saved private capturedFor:null remains the same Rust None as its absent echo",
    "successor saved private binding may serialize a Rust None explicitly",
    "Rust None equivalence never erases a changed non-null binding identity",
    "accepts actual generated acknowledgement: sc08-stage-dirty-draft",
    "accepts actual generated acknowledgement: sc09-schema-supported-sync-draft",
    "refuses a saved private handle outside the captured document: document_revision",
    "refuses a saved private handle outside the captured document: installation",
    "refuses a saved private handle outside the captured document: profile",
    "refuses private handle field scope inconsistent with its outer edit",
    "refuses secret handle field scope inconsistent with its outer edit",
    "refuses custom proxy reference drift: foreign_document",
    "refuses custom proxy reference drift: changed_saved_id",
    "refuses custom proxy reference drift: captured_as_saved",
    "accepted old generation stays correlated with the exact request: revision",
    "accepted old generation stays correlated with the exact request: host",
    "accepted old generation stays correlated with the exact request: document",
    "accepted old generation stays correlated with the exact request: draft_id",
    "refuses missing private transfer",
    "refuses missing secret transfer",
    "refuses missing all transfer",
    "refuses private destination that aliases its prior accepted handle",
    "refuses secret destination that aliases its prior accepted handle",
    "refuses private transfer drift: old_revision",
    "refuses private transfer drift: next_revision",
    "refuses private transfer drift: field",
    "refuses private transfer drift: document",
    "refuses private transfer drift: opaque_revision",
    "refuses secret transfer drift: old_revision",
    "refuses secret transfer drift: next_revision",
    "refuses secret transfer drift: field",
    "refuses secret transfer drift: host",
    "refuses successor retargeting: draft",
    "refuses successor retargeting: host",
    "refuses successor retargeting: same_revision",
    "refuses successor retargeting: skipped_revision",
    "refuses successor retargeting: document",
    "refuses successor retargeting: physical_installation",
    "refuses successor retargeting: schema",
    "refuses successor retargeting: profile",
    "preserves exact non-reference intent: accepted_public",
    "preserves exact non-reference intent: snapshot_public",
    "preserves exact non-reference intent: destination_id",
    "preserves exact non-reference intent: mode",
    "preserves exact non-reference intent: feed_value",
    "preserves exact non-reference intent: feed_order",
    "preserves exact non-reference intent: proxy",
    "refuses rewriting saved private intent: value_id",
    "refuses rewriting saved private intent: revision",
    "refuses rewriting saved private intent: document"
  ]
});
const directory=ownedArtifactPath(root,`artifacts/next/frontend-settings-tests/${randomUUID()}`,'directory',{allowMissing:true});mkdirSync(directory,{recursive:true});
const sha=bytes=>createHash('sha256').update(bytes).digest('hex');
function files(relative){return readdirSync(ownedArtifactPath(root,relative,'directory'),{withFileTypes:true}).sort((a,b)=>a.name.localeCompare(b.name)).flatMap(entry=>{assert.ok(entry.isFile()||entry.isDirectory());return entry.isDirectory()?files(`${relative}/${entry.name}`):[`${relative}/${entry.name}`];});}
const sourcePaths=()=>[...new Set(['package.json','pnpm-lock.yaml','ui/package.json','ui/svelte.config.js','ui/tsconfig.json','ui/vite.config.ts','contracts/codec/strict-json.mjs',
  ...files('ui/src'),...files('ui/tests'),...files('ui/settings-preview'),...files('contracts/fixtures'),
  'scripts/next/frontend-settings-tests.mjs','scripts/next/test-evidence.mjs','scripts/next/owned-artifact.mjs'])].sort();
const hashSources=()=>sourcePaths().map(relative=>{const bytes=readFileSync(ownedArtifactPath(root,relative));return{path:relative,bytes:bytes.length,sha256:sha(bytes)};});const before=hashSources(),checks=[];
function run(id,argv){const start=Date.now(),result=spawnSync(process.execPath,argv,{cwd:root,windowsHide:true,encoding:'utf8',timeout:180000,maxBuffer:8*1024*1024});
  const observed={id,executable:process.execPath,argv,cwd:root,startedAt:new Date(start).toISOString(),durationMs:Date.now()-start,exitCode:result.status,error:result.error?.code??null,stdout:result.stdout??'',stderr:result.stderr??''};checks.push(observed);writeFileSync(path.join(directory,id+'.json'),JSON.stringify(observed,null,2)+'\n',{flag:'wx'});assert.equal(result.status,0,`Settings ${id} failed; retain diagnostic`);assert.ok(!result.error);return observed.stdout;}
assert.match(run('types',['scripts/next/pnpm.mjs','--dir','ui','check']),/svelte-check found 0 errors and 0 warnings/);
const report=path.join(directory,'vitest.json');run('focused-tests',['scripts/next/pnpm.mjs','--dir','ui','exec','vitest','run',...Object.keys(required).map(file=>file.slice(3)),'--reporter=json',`--outputFile=${report}`]);
const result=JSON.parse(readFileSync(report,'utf8')),inventory=vitestEvidence(result,{root,required});assert.equal(inventory.tests,168);assert.equal(inventory.files.length,8);
for(const file of result.testResults){const name=path.relative(root,file.name).replaceAll('\\','/');assert.ok(Object.hasOwn(required,name));assert.deepEqual(file.assertionResults.map(value=>value.title).sort(),[...required[name]].sort());}
assert.deepEqual(hashSources(),before);const receipt=path.join(directory,'settings-tests.json');writeFileSync(receipt,JSON.stringify({schemaVersion:'bridge-frontend-settings-tests/v1',result:'passed',completedAt:new Date().toISOString(),checks,sources:before,inventory,required,report,
  boundary:'Actual typed fixture oracle, shared draft custody and SSR schema presentation; browser, native services and installed release qualify independently.',browserTestsExecuted:false,rustBuildInvoked:false,nativeRuntimeQualified:false,releaseQualified:false},null,2)+'\n',{flag:'wx'});
console.log(JSON.stringify({result:'passed',tests:inventory.tests,receipt,nativeRuntimeQualified:false,releaseQualified:false}));
