import type {LiveResolution,ResolvedLiveUser} from './liveApi';
export interface RoomCandidate {roomId:number;name:string;title:string;status:string;from:string[]}
export interface RoomLookupState {input:string;busy:boolean;candidates:RoomCandidate[];selected:number|null;error:string;warnings:string[]}
export function parseLiveInput(raw:string):{value:string;kind:'both'|'room'|'uid'}|null{
 const text=raw.trim();let value:string|undefined,kind:'both'|'room'|'uid'='both';
 if(/^\d+$/.test(text))value=text;
 else {const match=text.match(/^(?:https?:\/\/)?(live|space)\.bilibili\.com\/(\d+)(?:[/?#].*)?$/i);if(match){value=match[2];kind=match[1].toLowerCase()==='live'?'room':'uid';}}
 if(!value||BigInt(value)<=0n||BigInt(value)>9223372036854775807n)return null;
 return {value,kind};
}
export function createRoomLookup(api:{room:(id:string,signal:AbortSignal)=>Promise<LiveResolution>;uid:(id:string,signal:AbortSignal)=>Promise<ResolvedLiveUser>},notify:(state:RoomLookupState)=>void){
 let state:RoomLookupState={input:'',busy:false,candidates:[],selected:null,error:'',warnings:[]},pending:AbortController|null=null,disposed=false;
 const emit=(patch:Partial<RoomLookupState>)=>{state={...state,...patch};if(!disposed)notify(state);};
 return {getState:()=>state,
  change(input:string){pending?.abort();pending=null;emit({input,busy:false,candidates:[],selected:null,error:'',warnings:[]});},
  select(roomId:number){if(state.candidates.some(c=>c.roomId===roomId))emit({selected:roomId});},
  async lookup(){
   if(disposed||pending)return;const parsed=parseLiveInput(state.input);
   if(!parsed){emit({error:'请输入正整数房间号、UID，或 B站直播间／个人空间链接。'});return;}
   const request=new AbortController();pending=request;emit({busy:true,candidates:[],selected:null,error:'',warnings:[]});
   const kinds=parsed.kind==='both'?['room','uid'] as const:[parsed.kind];
   const responses=await Promise.allSettled(kinds.map(kind=>kind==='room'?api.room(parsed.value,request.signal):api.uid(parsed.value,request.signal)));
   if(disposed||pending!==request)return;
   const candidates:RoomCandidate[]=[],warnings:string[]=[];let uid:ResolvedLiveUser|null=null;
   responses.forEach((response,i)=>{if(response.status==='fulfilled'&&kinds[i]==='uid')uid=response.value as ResolvedLiveUser;});
   responses.forEach((response,i)=>{
    const kind=kinds[i];if(response.status==='rejected'){if(response.reason?.status===404)return;warnings.push(`${kind==='room'?'房间':'UID'}查询未完成：${response.reason instanceof Error?response.reason.message:String(response.reason)}`);return;}
    let candidate:RoomCandidate;
    if(kind==='room'){
     const room=response.value as LiveResolution;
     if(room.resolvedFrom!=='room')return;
     const id=uid&&(room.roomId===uid.roomId||room.roomId===uid.canonicalRoomId)?uid.canonicalRoomId:room.roomId;
     candidate={roomId:id,name:room.streamer,title:room.title,status:room.availability==='Live'?'正在直播':room.availability==='Offline'?'未开播':'状态未知',from:['房间号']};
    }else{
     const user=response.value as ResolvedLiveUser;
     candidate={roomId:user.canonicalRoomId,name:user.userName,title:user.title,status:user.isLive?'正在直播':'未开播',from:['UID']};
    }
    if(!Number.isSafeInteger(candidate.roomId)||candidate.roomId<=0)return;
    const existing=candidates.find(c=>c.roomId===candidate.roomId);if(existing)existing.from.push(...candidate.from);else candidates.push(candidate);
   });
   pending=null;emit({busy:false,candidates,warnings,selected:candidates.length===1?candidates[0].roomId:null,error:candidates.length?'':'未找到可用直播间，请核对输入或稍后重试。'});
  },dispose(){disposed=true;pending?.abort();pending=null;},
 };
}
