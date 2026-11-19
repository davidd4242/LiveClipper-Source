import { useState } from 'react';
import type { MusicGenerateInput } from '../../api/music';
import { navigateApp } from '../../app/appNavigation';
import { musicGenerationBlock } from './editorNextMusicApi';
import type { EditorNextMusicState, GlobalMusic } from './useEditorNextMusic';
import './r82-editor-next-music.css';
import './filmcraft-tool-panels.css';

export interface MusicPanelProps {
  state: EditorNextMusicState;
  mix: GlobalMusic | null;
  applyBlockReason: string | null;
  onRefresh: () => void;
  onPreview: (assetId: string) => void;
  onStopPreview: () => void;
  onPreviewError: () => void;
  onApply: (patch: Partial<GlobalMusic>) => void;
  onGenerate: (input: MusicGenerateInput) => void;
}
const STATUS = new Map([['queued', '排队中'], ['running', '生成中'], ['completed', '生成完成'], ['failed', '生成失败'], ['cancelled', '已取消']]);

export type MusicPanelFields = Omit<MusicGenerateInput, 'allowDownload'>;
const emptyFields = (): MusicPanelFields => ({ modelId: '', genre: '', style: '', content: '', theme: '', durationSeconds: 30 });
export function MusicPanel(props: MusicPanelProps) {
  const [fields, setFields] = useState<MusicPanelFields>(emptyFields);
  return <MusicPanelView {...props} fields={fields} onFieldsChange={setFields} />;
}

/** The controlled view keeps form state separate from engine/history state. */
export function MusicPanelView({ state, mix, applyBlockReason, onRefresh, onPreview, onStopPreview, onPreviewError, onApply, onGenerate,
  fields, onFieldsChange }: MusicPanelProps & { fields: MusicPanelFields; onFieldsChange: (fields: MusicPanelFields) => void }) {
  const status = state.modelStatus;
  const available = status?.models.filter(model => model.installed && (model.builtIn || status.pythonAvailable)) ?? [];
  const selectedModel = fields.modelId || (available.some(m => m.id === status?.defaultModelId) ? status!.defaultModelId : available[0]?.id ?? '');
  // provider 是明确选择（草稿保留在 fields）；未选时跟随已保存的 status.generationProvider。
  const online = (fields.provider ?? (status?.generationProvider === 'custom-http' ? 'custom-http' : 'local')) === 'custom-http';
  const httpConfigured = status?.httpConfigured === true;
  const { provider: _draftProvider, ...localFields } = fields;
  const input: MusicGenerateInput = online
    ? { ...localFields, provider: 'custom-http', modelId: '', allowDownload: status?.allowDownload ?? false }
    : { ...localFields, modelId: selectedModel, allowDownload: status?.allowDownload ?? false };
  const generationBlock = musicGenerationBlock(status, input);
  const busy = state.action.phase === 'checking' || state.action.phase === 'saving';
  const applyBlock = applyBlockReason || (!mix ? '当前工程配乐状态尚未就绪。' : null);
  const current = state.assets.find(a => a.id === mix?.musicAssetId);
  const changeField = (field: 'genre' | 'style' | 'content' | 'theme', value: string) => onFieldsChange({ ...fields, [field]: value });
  return <section className="editor-next-music fc-tool-panel" aria-label="整场配乐" aria-busy={state.status === 'loading'}>
    <header className="fc-tool-panel__heading"><h2>音乐</h2><p>试听、选择整场配乐，调整人声与音乐的平衡。</p></header>
    <p className="fc-tool-panel__muted">整场循环 BGM，不插入 A1–A3；试听与生成不自动应用，应用才进入工程历史。</p>
    <div className="editor-next-music__actions"><button type="button" onClick={onRefresh} disabled={state.status === 'loading'}>刷新音乐库</button></div>
    {state.message && <p role={state.status === 'error' ? 'alert' : 'status'}>{state.message}</p>}
    <fieldset disabled={busy || !!applyBlock} aria-describedby="editor-next-music-apply-block">
      <legend>当前工程 BGM</legend>
      <p>{mix?.musicAssetId ? (current?.name || `未找到库项：${mix.musicAssetId}`) : '没有选用整场配乐'}</p>
      <button type="button" disabled={!mix?.musicAssetId} onClick={() => onApply({ musicAssetId: null })}>移除整场配乐</button>
      <label>配乐音量{mix ? ` ${Math.round(mix.musicVolume * 100)}%` : '未知'}
        <input aria-label="配乐音量" type="range" min="0" max="100" step="1" value={mix ? Math.round(mix.musicVolume * 100) : 0}
          disabled={!mix} onChange={e => onApply({ musicVolume: Number(e.currentTarget.value) / 100 })} />
      </label>
      <label><input type="checkbox" checked={mix?.duckMusic ?? false} disabled={!mix}
        onChange={e => onApply({ duckMusic: e.currentTarget.checked })} />源视频有声时压低配乐</label>
    </fieldset>
    {applyBlock && <p className="editor-next-music__hint" id="editor-next-music-apply-block">{applyBlock}</p>}
    {state.action.phase !== 'idle' && <p role={['error', 'conflict'].includes(state.action.phase) ? 'alert' : 'status'}
      data-music-action={state.action.phase}>{state.action.message}</p>}
    <section className="fc-tool-panel__group" aria-label="本地音乐库">
      <div className="fc-tool-panel__meta"><h3>音乐库</h3><span>{state.assets.length} 首</span></div>
      {!state.assets.length && <div className="fc-tool-panel__empty"><strong>{state.status === 'loading' ? '正在读取音乐库…' : '音乐库还是空的'}</strong><p>可刷新已有素材，或在下方使用已安装模型生成音乐候选。</p></div>}
      <ul className="editor-next-music__assets">{state.assets.map(asset => <li key={asset.id} className="editor-next-music__asset">
        <h3>{asset.name}</h3><p>{asset.style || '未提供风格'}</p>
        {state.candidateId === asset.id && <p>新生成候选，尚未自动应用。</p>}
        <div className="editor-next-music__actions">
          <button type="button" onClick={() => onPreview(asset.id)}>试听 {asset.name}</button>
          <button type="button" className="fc-tool-panel__primary" disabled={busy || !!applyBlock || mix?.musicAssetId === asset.id}
            onClick={() => onApply({ musicAssetId: asset.id })}>{mix?.musicAssetId === asset.id ? '当前已选用' : `应用 ${asset.name}`}</button>
        </div>
      </li>)}</ul>
    </section>
    {state.preview && <div className="editor-next-music__preview">
      <p>素材试听，不改变工程配乐。</p>
      <audio key={state.preview.url} src={state.preview.url} controls preload="metadata" onError={onPreviewError} aria-label="音乐素材试听" />
      <button type="button" onClick={onStopPreview}>结束试听</button>
    </div>}
    <form onSubmit={e => { e.preventDefault(); if (!generationBlock && !state.generating) onGenerate(input); }}>
      <fieldset disabled={state.generating}>
        <legend>生成音乐候选</legend>
        {status?.engineNote && <p>{status.engineNote}</p>}
        <label>生成来源<select value={online ? 'custom-http' : 'local'} onChange={e => onFieldsChange({ ...fields, provider: e.currentTarget.value === 'custom-http' ? 'custom-http' : 'local' })}>
          <option value="local">本地 / 离线</option>
          <option value="custom-http">线上 API</option>
        </select></label>
        {online ? <>
          <p>{httpConfigured ? `线上模型：${status?.apiModel || '（未返回模型名）'}；使用设置中保存的接口，无需 Python 或本地模型。` : '线上 API 尚未配置。'}</p>
          <button type="button" onClick={() => { void navigateApp('/settings'); }}>{httpConfigured ? '去设置修改线上 API' : '去设置配置线上 API'}</button>
        </> : <>
          <label>可用模型<select value={selectedModel} onChange={e => onFieldsChange({ ...fields, modelId: e.currentTarget.value })}>
            {!available.length && <option value="">没有已安装且可用的模型</option>}
            {available.map(model => <option value={model.id} key={model.id}>{model.name}</option>)}
          </select></label>
          {status?.models.filter(model => !model.installed).map(model => <p key={model.id}>{model.name}：未安装，本面板不自动下载。</p>)}
        </>}
        {(['genre', 'style', 'content', 'theme'] as const).map((field, index) => <label key={field}>
          {['流派', '风格', '内容', '主题'][index]}（必填，最多 500 字符）
          <textarea name={field} value={fields[field]} required maxLength={500} onChange={e => changeField(field, e.currentTarget.value)} />
        </label>)}
        <label>时长（5–180 秒）<input type="number" required min={5} max={180} step={1} value={fields.durationSeconds}
          onChange={e => onFieldsChange({ ...fields, durationSeconds: e.currentTarget.valueAsNumber })} /></label>
        {online
          ? <p>线上生成没有远端完成百分比；停止等待只结束本地等待，不承诺远端取消或不计费。</p>
          : <p>模型下载授权：{typeof status?.allowDownload === 'boolean' ? (status.allowDownload ? '服务端已允许' : '服务端未允许') : '未知'}。本次使用已安装模型，不发下载请求。</p>}
        <button type="submit" className="fc-tool-panel__primary" disabled={!!generationBlock || state.generating} aria-describedby="editor-next-music-generate-block">{state.generating ? '正在提交生成…' : '生成音乐候选'}</button>
        {generationBlock && <p id="editor-next-music-generate-block" className="editor-next-music__hint">{generationBlock}</p>}
      </fieldset>
    </form>
    {state.job && <section aria-label="音乐生成任务" data-job-status={state.job.status}>
      <p>{STATUS.get(state.job.status) ?? `原始状态：${state.job.status}`}</p>
      <p>{state.job.detail}</p>
      {online && ['queued', 'running'].includes(state.job.status) && <progress aria-label="线上生成等待中（阶段提示，非远端完成比例）" />}
      {!online && state.job.status === 'running' && Number.isFinite(state.job.progress) && state.job.progress >= 0 && state.job.progress <= 100 &&
        <progress max="100" value={state.job.progress} aria-label="音乐生成进度" />}
      {state.job.error && <p role="alert">{state.job.error}</p>}
    </section>}
    <p className="editor-next-music__hint">生成会写入音乐任务与全局音乐偏好；工程撤销不会删除生成素材。应用、音量和压低配乐由同一原生历史撤销。</p>
  </section>;
}
