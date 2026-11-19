import type {LegacyRoomStatus} from './liveApi';
import {formatDuration} from './format';
import {roomHeatDisplay} from './roomHeatDisplay';
export function RoomHeatDetails({room,comparisonSeconds}:{room:LegacyRoomStatus;comparisonSeconds:number|null}){
 return <div className="lw-room-heat-details" aria-label="动态基准详情">
  <p>当前基准 {room.baseline.toFixed(3)} 条/秒 · {comparisonSeconds===null?'比较窗口':`近 ${comparisonSeconds} 秒`}速率 {room.messageRate.toFixed(3)} 条/秒 · 基准比率 {roomHeatDisplay(room).label}</p>
  <p>最近成功重评：{room.heatRebaseAt==null?'尚无成功重评':`会话 ${formatDuration(room.heatRebaseAt)} 处`}</p>
  <p>最近评估说明：{room.heatRebaseReason||'尚未返回评估信息'}</p>
  <p>当前评估周期内近 20 分钟独立自动高光：{room.heatRecentTriggers??'—'}（合并只计一次、手动不计；重评后重新计数，不代表生成文件数）</p>
  <p>初始基准抑制明显开场瞬时峰，不排除持续高热。低于基准持续超过 10 分钟，或近 20 分钟出现第 3 个独立自动高光时，使用近 5 分钟样本评估；成功后渐进调整。</p>
 </div>;
}
