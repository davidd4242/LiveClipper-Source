import type {LegacyRoomStatus} from './liveApi';
/** Presentation only: the backend owns trigger readiness and its low-rate floor. */
export function roomHeatDisplay(room:Pick<LegacyRoomStatus,'messageRate'|'baseline'>){
 if(room.baseline<=0)return {percent:null,width:0,label:'尚无有效基准',tone:'is-unbased'};
 const percent=room.messageRate/room.baseline*100;
 return {percent,width:Math.min(100,percent/140*100),label:`${percent.toFixed(0)}%`,tone:percent>=140?'is-display-line':percent>=100?'is-baseline':'is-below-baseline'};
}
