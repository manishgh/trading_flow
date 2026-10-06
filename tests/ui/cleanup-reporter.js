import fs from 'node:fs/promises';
import path from 'node:path';
import {fileURLToPath} from 'node:url';

const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..');

// Playwright calls onExit after task/plugin teardown and onEnd reporters, so its
// owned Web host has released SQLite and failure artifacts have finished writing.
export default class CleanupReporter {
 printsToStdio(){return false;}
 async onExit(){
  const id=process.env.TRADINGFLOW_UI_OWNED_RUN_ID;
  if(!id||!/^[0-9a-f-]{36}$/i.test(id))throw Error('Missing owned test-run ID; refuse data deletion');
  for(const parent of [path.join(root,'.tmp/ui-tests/test-runs'),path.join(root,'.tmp/ui-test-artifacts')]){
   const target=path.resolve(parent,'run-'+id);
   if(path.dirname(target)!==path.resolve(parent))throw Error('Test cleanup escaped its owned namespace');
   const stat=await fs.lstat(target).catch(e=>{if(e.code==='ENOENT')return null;throw e;});
   if(stat?.isSymbolicLink())throw Error('Refuse test-root symlink cleanup');
   if(stat){
    const actual=await fs.realpath(target);const boundary=await fs.realpath(parent);
    const workspace=await fs.realpath(root);
    const expected=path.join(workspace,path.relative(root,parent));
    if(boundary.toLowerCase()!==expected.toLowerCase())throw Error('Test namespace resolved through a junction');
    if(path.dirname(actual).toLowerCase()!==boundary.toLowerCase())throw Error('Test cleanup resolved outside its owned namespace');
   }
   await fs.rm(target,{recursive:true,force:true,maxRetries:20,retryDelay:250});
  }
  console.log('Deleted owned test database, cache, runs and artifacts after host shutdown.');
 }
}
