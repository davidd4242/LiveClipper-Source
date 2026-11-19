/** One UI operation owns candidate analysis, review, copy and restore until all follow-up work settles. */
export function createIndependentOperationGate(){
 let ticket:object|null=null;
 return {
  isBusy:()=>ticket!==null,
  reset(){ticket=null;},
  async run<T>(action:()=>Promise<T>|undefined,publishBusy:()=>void){
   if(ticket)return false;
   const own={};ticket=own;publishBusy();
   try{return await action();}
   finally{if(ticket===own){ticket=null;publishBusy();}}
  },
 };
}
