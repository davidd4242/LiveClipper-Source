import type {LegacyRoomStatus} from './liveApi';
export type DanmakuStatus='idle'|'connecting'|'streaming'|'waiting'|'error';
export function noRecordingSessionMessage(room:Pick<LegacyRoomStatus,'monitoring'|'autoRecord'|'cacheEnabled'|'isLive'>):string{
 if(!room.monitoring)return '该房间未在监控，当前没有录制会话。';
 if(!room.autoRecord&&!room.cacheEnabled)return '该房间为纯预览，高光缓存和整场留存均关闭；没有采集会话，不提供会话弹幕快照。';
 if(!room.isLive)return '该房间正在监控，等待开播建立采集会话。';
 return '该房间正在监控，已启用高光缓存或整场留存，等待采集会话就绪。';
}
export function danmakuSnapshotBadge(state:DanmakuStatus):{tone:'green'|'blue'|'red'|'idle';label:string}{
 switch(state){case 'streaming':return {tone:'green',label:'快照读取正常'};case 'connecting':return {tone:'blue',label:'正在读取'};case 'error':return {tone:'red',label:'读取失败，重试中'};case 'waiting':return {tone:'idle',label:'暂无录制会话'};default:return {tone:'idle',label:'未读取'};}
}
