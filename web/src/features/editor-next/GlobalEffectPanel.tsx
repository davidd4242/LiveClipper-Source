import { useEffect, useRef, useState } from 'react';
import { getAudioAssets, getAudioFileUrl } from '../../api/music';
import type { AudioAsset } from '../../api/music';

export interface GlobalEffect { effectAssetId: string | null; effectAt: number; effectVolume: number }
export interface NativeEffectBridge {
  getGlobalEffect?: () => { ok: boolean; mix?: GlobalEffect; duration?: number; generation?: number; engineRevision?: number; error?: string };
  setGlobalEffect?: (patch: GlobalEffect, expected: { generation: number; engineRevision: number }, signal: AbortSignal) => Promise<{ ok: boolean; error?: string }>;
}
interface Props {
  ownerKey: string; generation: number; disabled: boolean;
  getBridge: () => NativeEffectBridge | null;
  getSaveSession: () => { flush: () => Promise<boolean> } | null;
  onBusy: (busy: boolean) => void;
}
export function GlobalEffectPanel(props: Props) {
  const latest = useRef(props); latest.current = props;
  const alive = useRef(true), pending = useRef(false), abort = useRef<AbortController | null>(null);
  const [assets, setAssets] = useState<AudioAsset[]>([]), [message, setMessage] = useState('');
  const [busy, setBusy] = useState(false), [preview, setPreview] = useState<string | null>(null);
  const current = props.getBridge()?.getGlobalEffect?.();
  const mix = current?.ok ? current.mix : undefined;
  const [draft, setDraft] = useState<GlobalEffect>({ effectAssetId: null, effectAt: 0, effectVolume: 0.5 });
  useEffect(() => { if (mix) setDraft({ ...mix }); }, [mix?.effectAssetId, mix?.effectAt, mix?.effectVolume]);
  useEffect(() => {
    alive.current = true;
    void getAudioAssets().then(items => { if (alive.current) setAssets(items.filter(x => x.kind === 'effect')); })
      .catch(error => { if (alive.current) setMessage(String(error)); });
    return () => { alive.current = false; abort.current?.abort(); props.onBusy(false); };
  }, []);
  const apply = async (next: GlobalEffect) => {
    if (pending.current || props.disabled) return;
    const owner = props.ownerKey, bridge = props.getBridge(), session = props.getSaveSession();
    const baseline = bridge?.getGlobalEffect?.();
    if (!session || !baseline?.ok || baseline.generation !== props.generation || !Number.isSafeInteger(baseline.engineRevision) || !bridge?.setGlobalEffect) {
      setMessage('当前工程音效入口尚未就绪。'); return;
    }
    if (next.effectAssetId !== null && !assets.some(x => x.id === next.effectAssetId)) { setMessage('请选择音效库中的资源。'); return; }
    if (!Number.isFinite(next.effectAt) || next.effectAt < 0 || next.effectAt > (baseline.duration ?? 0) ||
      !Number.isFinite(next.effectVolume) || next.effectVolume < 0 || next.effectVolume > 2) { setMessage('落点须在序列内，音量须在 0–200% 之间。'); return; }
    const controller = new AbortController(); abort.current = controller;
    pending.current = true; setBusy(true); props.onBusy(true); setMessage('正在准备音效…');
    const isCurrent = () => alive.current && latest.current.ownerKey === owner && !controller.signal.aborted;
    try {
      const result = await bridge.setGlobalEffect(next, { generation: baseline.generation!, engineRevision: baseline.engineRevision! }, controller.signal);
      if (!isCurrent()) return;
      if (!result.ok) throw new Error(result.error || '音效应用失败');
      setMessage('音效已进入原生撤销历史，正在保存…');
      const saved = await session.flush();
      if (isCurrent()) setMessage(saved ? '全局音效已保存。可播放序列检查落点，也可撤销。' : '原生音效已更新，但保存未确认，请处理保存栏错误。');
    } catch (error) { if (isCurrent()) setMessage(`音效未确认完成：${error instanceof Error ? error.message : String(error)}`); }
    finally { pending.current = false; if (alive.current) { setBusy(false); props.onBusy(false); } }
  };
  return <section className="fc-tool-panel" aria-label="全局音效">
    <h3>全局音效</h3><p>在指定秒数播放一次，不循环、不占 A 轨；应用后进入原生撤销与工程保存。</p>
    <fieldset disabled={props.disabled || busy || !mix}>
      <label>音效资源<select aria-label="全局音效资源" value={draft.effectAssetId ?? ''} onChange={e => setDraft({ ...draft, effectAssetId: e.target.value || null })}>
        <option value="">未选择</option>{assets.map(a => <option key={a.id} value={a.id}>{a.name}</option>)}
      </select></label>
      <label>落点（秒）<input aria-label="音效落点秒" type="number" min="0" max={current?.duration ?? 0} step="0.001" value={draft.effectAt} onChange={e => setDraft({ ...draft, effectAt: Number(e.target.value) })} /></label>
      <label>音量（%）<input aria-label="音效音量百分比" type="number" min="0" max="200" step="1" value={Math.round(draft.effectVolume * 100)} onChange={e => setDraft({ ...draft, effectVolume: Number(e.target.value) / 100 })} /></label>
      <button type="button" disabled={!draft.effectAssetId} onClick={() => void apply(draft)}>应用全局音效</button>
      <button type="button" disabled={!mix?.effectAssetId && !mix?.effectAt} onClick={() => void apply({ effectAssetId: null, effectAt: 0, effectVolume: 0.5 })}>移除全局音效</button>
      <button type="button" disabled={!draft.effectAssetId} onClick={() => setPreview(draft.effectAssetId)}>试听音效素材</button>
    </fieldset>
    {preview && <audio key={preview} controls src={getAudioFileUrl(preview)} onError={() => setMessage('音效素材试听失败。')} aria-label="音效素材试听" />}
    {message && <p role="status">{message}</p>}
  </section>;
}
