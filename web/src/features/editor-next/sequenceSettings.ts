import type { FullEngineBridge } from '../../engine/fullEngineSession';
export interface SequenceSettings { width:number; height:number; fps:number }
export interface SequenceSettingsSnapshot extends SequenceSettings { expected:{generation:number;engineRevision:number} }
export function readSequenceSettings(bridge:FullEngineBridge):SequenceSettingsSnapshot {
  const snapshot=bridge.hostSnapshot();
  if(!snapshot.ok||snapshot.generation===undefined||snapshot.engineRevision===undefined)throw Error('当前序列尚未就绪。');
  const sequence=(snapshot.document as {sequence:SequenceSettings}).sequence;
  return {width:sequence.width,height:sequence.height,fps:sequence.fps,expected:{generation:snapshot.generation,engineRevision:snapshot.engineRevision}};
}
export function applySequenceSettings(bridge:FullEngineBridge,initial:SequenceSettingsSnapshot,settings:SequenceSettings){
  if(!Number.isInteger(settings.width)||!Number.isInteger(settings.height)||settings.width%2!==0||settings.height%2!==0||settings.width<16||settings.height<16||settings.width>16384||settings.height>16384||![24,25,30,50,60].includes(settings.fps))throw Error('宽高需为 16–16384 的偶数，请选择支持的帧率。');
  if(!bridge.hostSetSequenceSettings)throw Error('当前引擎尚不支持序列设置，请更新后重试。');
  const result=bridge.hostSetSequenceSettings({...settings,expected:initial.expected});
  if(!result.ok)throw Error(result.error||'序列设置未应用。');
  return result;
}
