import type {LegacyRoomStatus} from './liveApi';
export function autoClipAvailability(enabled:boolean|null,rooms:readonly Pick<LegacyRoomStatus,'monitoring'|'cacheActive'|'captureMode'|'state'|'bufferSeconds'>[]|null):{ready:boolean;label:string}{
 if(enabled===null)return {ready:false,label:'读取自动切片设置中'};
 if(!enabled)return {ready:false,label:'自动切片已关闭'};
 if(rooms===null)return {ready:false,label:'已启用 · 等待房间状态'};
 const recording=rooms.filter(room=>room.monitoring&&(room.cacheActive||(room.captureMode==='full-recording'&&room.state==='Recording')));
 if(recording.length===0)return {ready:false,label:'已启用 · 当前缓存未就绪'};
 if(!recording.some(room=>room.bufferSeconds>0))return {ready:false,label:'已启用 · 等待缓存'};
 return {ready:true,label:'已启用 · 等待热度触发'};
}
