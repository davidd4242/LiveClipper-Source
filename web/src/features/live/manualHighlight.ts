import type { LegacyHighlightEvent, LegacyRoomStatus, LegacyStatus } from './liveApi';

type ManualPolicy = Pick<LegacyStatus, 'manualPreRollSeconds' | 'manualPostRollSeconds'>;
type ManualRoom = Pick<LegacyRoomStatus, 'state' | 'bufferSeconds' | 'cacheActive' | 'captureMode'>;
const seconds = (value: number) => Number(value.toFixed(2)).toString();

/** The segment span is not proof of continuous coverage. Never promise unavailable history. */
export function manualHighlightAvailability(room: ManualRoom | null, policy: ManualPolicy | null) {
  if (!room || !(room.cacheActive || (room.captureMode==='full-recording'&&room.state==='Recording'))) return { enabled: false, description: '当前没有正在采集的高光缓存或整场录播，请开启高光临时缓存并等待就绪。' };
  if (!policy) return { enabled: false, description: '正在读取手动高光时长配置。' };
  const target = `目标前 ${seconds(policy.manualPreRollSeconds)} 秒 + 后 ${seconds(policy.manualPostRollSeconds)} 秒`;
  if (room.bufferSeconds <= 0) return { enabled: false, description: `${target}；尚无已完成的录制缓存，等待首个分片后再标记。` };
  return { enabled: true, description: `${target}；当前缓存跨度 ${seconds(room.bufferSeconds)} 秒。只保留实际可用的前段，后段需继续录制；相邻标记可能合并，不会补出缺失内容。` };
}

/** A mark receipt describes the requested event, not a completed/exported clip. */
export function manualHighlightReceipt(event: LegacyHighlightEvent): string {
  return `已提交手动高光 ${event.id}：计划收集录制时间 ${seconds(event.start)}–${seconds(event.end)} 秒，提交时状态 ${event.state}（此回执不随进度刷新；最新状态见本场高光列表）。相邻标记可能合并；实际内容以可用录制分片和后续生成结果为准。`;
}
