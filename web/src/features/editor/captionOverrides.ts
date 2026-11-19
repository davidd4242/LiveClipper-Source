/**
 * P1.6-a：源媒体秒（source seconds）字幕覆盖的编辑态 helper。
 *
 * 权威数据是 `EditorProject.captionOverrides`（用户编辑），`sourceTranscripts` 只是后端
 * Snapshot 冻结出来的派生快照。这里只做 reading/writing，不做 trim / filter / 排序：
 * 编辑中的空行和光标顺序都属于用户。
 */
import type { EditorProject, TranscriptSegment, TimelineCaption } from '../../api/types';

const hasOwn = (value: object, key: string): boolean =>
  Object.prototype.hasOwnProperty.call(value, key);

const cloneSegments = (segments: readonly TranscriptSegment[]): TranscriptSegment[] =>
  segments.map((segment) => ({ start: segment.start, end: segment.end, text: segment.text }));

/**
 * 解析某个源当前生效的字幕：override → sourceTranscripts → fallback。
 * own-key 判定（`key: []` 是显式清空，不回落到 store）；不读原型链。
 */
export function captionsFor(
  project: EditorProject | null | undefined,
  clipId: string,
  fallback: readonly TranscriptSegment[] | null | undefined,
): TranscriptSegment[] {
  const overrides = project?.captionOverrides;
  if (overrides && clipId && hasOwn(overrides, clipId)) {
    const value = overrides[clipId];
    if (Array.isArray(value)) return cloneSegments(value);
  }
  const frozen = project?.sourceTranscripts;
  if (frozen && clipId && hasOwn(frozen, clipId)) {
    const value = frozen[clipId];
    if (Array.isArray(value)) return cloneSegments(value);
  }
  return fallback ? cloneSegments(fallback) : [];
}

/**
 * 写入某个源的字幕覆盖：`segments === null` 删除键并清掉该源冻结的派生
 * `sourceTranscripts`（否则仍会命中旧精修，回不到 fallback 原转写）；
 * `[]` 保留为显式清空。返回新对象，不改入参。
 */
export function setClipCaptions(
  project: EditorProject,
  clipId: string,
  segments: readonly TranscriptSegment[] | null,
): EditorProject {
  const next: Record<string, TranscriptSegment[]> = { ...(project.captionOverrides ?? {}) };
  if (segments === null) delete next[clipId];
  else next[clipId] = cloneSegments(segments);

  const result: EditorProject = { ...project };
  if (
    segments === null
    && project.sourceTranscripts
    && hasOwn(project.sourceTranscripts, clipId)
  ) {
    const frozen: Record<string, TranscriptSegment[]> = { ...project.sourceTranscripts };
    delete frozen[clipId];
    result.sourceTranscripts = frozen;
  }
  if (Object.keys(next).length === 0) delete result.captionOverrides;
  else result.captionOverrides = next;
  return result;
}

/** 是否有显式覆盖（含空数组）；own-key 判定，不查原型。 */
export function hasCaptionOverride(project: EditorProject, clipId: string): boolean {
  const overrides = project.captionOverrides;
  return Boolean(overrides) && Boolean(clipId) && hasOwn(overrides!, clipId);
}

/** Explicit replacement only: latest payload transcript, not captionsFor's frozen/override result. */
export function replacePrimarySourceCaptions(project:EditorProject,sourceId:string,latest:readonly TranscriptSegment[],document:unknown){
 if(!latest.length||latest.some(c=>!Number.isFinite(c.start)||!Number.isFinite(c.end)||c.start<0||c.end<=c.start||!c.text.trim()))throw Error('当前源没有有效的新转录，未清空任何字幕。');
 const seq=(document as {sequence?:unknown})?.sequence as {fps:number;videoTracks:{clips:{key:string;sourceId?:string;start:number;end:number;sourceIn:number;speed?:number;enabled?:boolean;nestedSequenceId?:string}[]}[]}|undefined;
 if(!seq||!Number.isFinite(seq.fps)||seq.fps<=0)throw Error('缺少已保存的原生时间线映射。');
 const ownItems=new Set((project.timeline??[]).filter(c=>c.clipId===sourceId).map(c=>c.id));
 if((project.timeline??[]).some(c=>c.clipId===sourceId&&c.nestedSequenceId))throw Error('当前主源包含嵌套片段，暂不支持替换。');
 if(project.timelineCaptions==null&&(project.timeline??[]).some(c=>c.clipId&&c.clipId!==sourceId))throw Error('多源工程尚无独立字幕快照，请先放置其他源字幕再替换。');
 const old=project.timelineCaptions??[],kept=old.filter(c=>c.sourceClipId?c.sourceClipId!==sourceId:!c.sequenceItemId||!ownItems.has(c.sequenceItemId));
 const added:TimelineCaption[]=[],seen=new Set<string>(),ids=new Set(kept.map(c=>c.id)),fps=seq.fps;
 for(const track of seq.videoTracks)for(const clip of track.clips){
  if(clip.sourceId!==sourceId||clip.enabled===false)continue;
  if(!ownItems.has(clip.key)||clip.nestedSequenceId)throw Error('主源片段归属不一致或存在嵌套，未替换。');
  const speed=clip.speed??1,start=Math.round(clip.start),end=Math.round(clip.end),sourceIn=Math.round(clip.sourceIn);
  if(!Number.isFinite(speed)||speed<.25||speed>4||!Number.isFinite(start)||!Number.isFinite(end)||!Number.isFinite(sourceIn)||end<=start)throw Error('主源速度或时间范围不支持，未替换。');
  const from=sourceIn/fps,to=from+(end-start)/fps*speed;
  latest.forEach((cue,i)=>{const a=Math.max(from,cue.start),b=Math.min(to,cue.end);if(b-a<.001)return;const first=Math.round(start+(a*fps-sourceIn)/speed),last=Math.round(start+(b*fps-sourceIn)/speed);if(last<=first||first<0)return;const key=`${first}:${last}:${cue.text.trim()}`;if(seen.has(key))return;seen.add(key);const id=`hostcap_${sourceId}_${clip.key}_${i}`;if(ids.has(id))throw Error('字幕编号冲突，未替换。');ids.add(id);added.push({id,start:first/fps,end:last/fps,text:cue.text.trim(),sourceClipId:sourceId,sequenceItemId:clip.key,sourceCueIndex:i,sourceStart:cue.start,sourceEnd:cue.end,manual:false});});
 }
 if(!added.length)throw Error('新转录与当前主源视频片段没有有效交集，未清空旧字幕。');
 if(kept.length+added.length>2000)throw Error('替换后字幕超过2000条，未保存。');
 const overrides={...(project.captionOverrides??{})};delete overrides[sourceId];
 return {project:{...project,sourceTranscripts:{...(project.sourceTranscripts??{}),[sourceId]:cloneSegments(latest)},captionOverrides:overrides,timelineCaptions:[...kept,...added]},removed:old.length-kept.length,added:added.length};
}

export async function commitPrimaryTranscriptReplacement(options:{clipId:string;epoch:string;session:{flush:()=>Promise<boolean>;readState:()=>{hasUnsaved:boolean;saving:boolean;conflict:boolean;error?:string;revision:number|null};snapshot:()=>{clipId:string;epoch:string;serverRevision:number;generation:number;engineRevision:number;project:EditorProject;document:unknown}};getLatest:()=>Promise<{clip:{id:string};epoch?:string;revision?:number;transcript:{segments:TranscriptSegment[]}|null}>;confirm:(count:number)=>boolean;isCurrent:()=>boolean;put:(project:EditorProject,revision:number,epoch:string)=>Promise<{saved:boolean}>;reload:()=>void}){
 const before=options.session.readState();if(before.hasUnsaved||before.saving||before.conflict||before.error)throw Error('请先保存并处理冲突或错误，再替换字幕。');
 if(!await options.session.flush()||!options.isCurrent())throw Error('保存状态或工程身份已变化，未替换。');
 const pin=options.session.snapshot(),latest=await options.getLatest();
 if(latest.clip.id!==options.clipId||latest.epoch!==options.epoch||latest.revision!==pin.serverRevision)throw Error('最新转录所属工程、修订或纪元已变化，请重载后再试。');
 const cues=latest.transcript?.segments??[];if(!cues.length)throw Error('当前源尚无最新成功转录，未清空旧字幕。');
 const result=replacePrimarySourceCaptions(pin.project,options.clipId,cues,pin.document);
 if(!options.confirm(cues.length))return false;
 const now=options.session.snapshot();if(!options.isCurrent()||now.clipId!==pin.clipId||now.epoch!==pin.epoch||now.serverRevision!==pin.serverRevision||now.generation!==pin.generation||now.engineRevision!==pin.engineRevision)throw Error('确认期间工程已变化，未替换。');
 const saved=await options.put(result.project,pin.serverRevision,options.epoch);if(!saved.saved)throw Error('保存未确认成功，请重载核对，未自动重试。');
 if(options.isCurrent())options.reload();return true;
}
