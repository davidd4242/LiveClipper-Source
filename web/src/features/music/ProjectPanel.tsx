import { useEffect, useState } from 'react';
import { AlertTriangle, ExternalLink, FolderOpen, LoaderCircle, RefreshCw } from 'lucide-react';
import type { ClipDto, EditorProject } from '../../api/types';
import {
  getAudioFileUrl,
  type AudioAsset,
  type MusicAssetAnalysis,
  type MusicDuckingEnvelope,
} from '../../api/music';
import { EnvelopeCurve } from './EnvelopeCurve';
import { baseName, formatGain } from './utils';

const LUFS_TARGETS = [-14, -16, -23] as const;
const LUFS_TARGET_STORAGE_KEY = 'liveclipper.music-lufs-target';

function duckingTargetLabel(ducking: MusicDuckingEnvelope): string {
  switch (ducking.target) {
    case 'volume-keyframes':
      return `时间线配乐片段（volumeKeyframes，${ducking.appliedItemIds.length} 个片段）`;
    case 'project-document':
      return '工程级包络文档（studio_documents，渲染端尚未消费）';
    case 'none':
      return '未写入（降级）';
    default:
      return ducking.target || '—';
  }
}

interface ProjectPanelProps {
  clips: ClipDto[];
  clipId: string;
  project: EditorProject | null;
  projectLoading: boolean;
  projectError: string | null;
  assets: AudioAsset[];
  applying: boolean;
  musicAnalysis: MusicAssetAnalysis | null;
  onSelectClip: (clipId: string) => void;
  onReloadProject: () => Promise<void>;
  onApplyMusic: (assetId: string | null) => Promise<void>;
  onApplyEffect: (assetId: string | null) => Promise<void>;
  ducking: MusicDuckingEnvelope | null;
  duckingLoading: boolean;
  onGenerateDucking: () => Promise<void>;
}

export function ProjectPanel({
  clips,
  clipId,
  project,
  projectLoading,
  projectError,
  assets,
  applying,
  musicAnalysis,
  onSelectClip,
  onReloadProject,
  onApplyMusic,
  onApplyEffect,
  ducking,
  duckingLoading,
  onGenerateDucking,
}: ProjectPanelProps) {
  const [musicChoice, setMusicChoice] = useState('');
  const [effectChoice, setEffectChoice] = useState('');
  const [lufsTarget, setLufsTarget] = useState<number>(() => {
    if (typeof window === 'undefined') {
      return -14;
    }
    const parsed = Number(window.localStorage.getItem(LUFS_TARGET_STORAGE_KEY));
    return (LUFS_TARGETS as readonly number[]).includes(parsed) ? parsed : -14;
  });

  useEffect(() => {
    setMusicChoice(project?.musicAssetId ?? '');
    setEffectChoice(project?.effectAssetId ?? '');
  }, [project?.musicAssetId, project?.effectAssetId, clipId]);

  useEffect(() => {
    window.localStorage.setItem(LUFS_TARGET_STORAGE_KEY, String(lufsTarget));
  }, [lufsTarget]);


  const musicAssets = assets.filter((asset) => asset.kind === 'music');
  const effectAssets = assets.filter((asset) => asset.kind === 'effect');
  const currentClip = clips.find((clip) => clip.id === clipId) ?? null;
  const currentMusic = project?.musicAssetId
    ? assets.find((asset) => asset.id === project.musicAssetId) ?? null
    : null;
  const currentEffect = project?.effectAssetId
    ? assets.find((asset) => asset.id === project.effectAssetId) ?? null
    : null;

  return (
    <section className="music-panel music-project" aria-label="工程配乐">
      <header className="music-panel-head">
        <div className="music-panel-title">
          <FolderOpen size={14} aria-hidden="true" />
          <span>工程配乐</span>
        </div>
        <span className="music-panel-meta">{clips.length} 个项目</span>
      </header>

      <div className="music-panel-body">
        {clips.length === 0 ? (
          <div className="music-empty-block">
            <p className="music-empty">还没有可用的项目。</p>
            <a className="music-button music-button--ghost" href="/studio">
              去项目库
            </a>
          </div>
        ) : (
          <>
            <div className="music-section">
              <div className="music-section-head">
                <h3>选择项目</h3>
                <button
                  type="button"
                  className="music-icon-button"
                  title="重新读取工程"
                  aria-label="重新读取工程"
                  disabled={projectLoading}
                  onClick={() => void onReloadProject()}
                >
                  <RefreshCw
                    size={13}
                    className={projectLoading ? 'music-spin' : undefined}
                    aria-hidden="true"
                  />
                </button>
              </div>
              <select
                className="music-select music-select--block"
                aria-label="选择工程"
                value={clipId}
                onChange={(event) => onSelectClip(event.target.value)}
              >
                {clips.map((clip) => (
                  <option key={clip.id} value={clip.id}>
                    {clip.title || baseName(clip.path)}
                  </option>
                ))}
              </select>
              {currentClip ? (
                <p className="music-hint music-ellipsis" title={currentClip.path}>
                  {currentClip.path}
                </p>
              ) : null}
              <div className="music-actions">
                <a
                  className="music-button music-button--ghost"
                  href={`/?clip=${encodeURIComponent(clipId)}`}
                >
                  <ExternalLink size={13} aria-hidden="true" />
                  在编辑器中打开
                </a>
              </div>
            </div>

            <div className="music-divider" />

            {projectError ? (
              <div className="music-empty-block">
                <p className="music-error">{projectError}</p>
                <button
                  type="button"
                  className="music-button music-button--ghost"
                  onClick={() => void onReloadProject()}
                >
                  重试
                </button>
              </div>
            ) : projectLoading && !project ? (
              <p className="music-empty">正在读取工程…</p>
            ) : project ? (
              <div className="music-section">
                <div className="music-section-head">
                  <h3>当前配乐配置</h3>
                  <span>来自真实工程文件，只读展示</span>
                </div>

                <dl className="music-kv">
                  <div>
                    <dt>配乐</dt>
                    <dd>
                      {currentMusic ? (
                        <>
                          <span className="music-badge">音乐</span>
                          {currentMusic.name}
                        </>
                      ) : project.musicAssetId ? (
                        <span className="music-mono">{project.musicAssetId}</span>
                      ) : (
                        <span className="music-muted">未配置</span>
                      )}
                    </dd>
                  </div>
                  <div>
                    <dt>音效</dt>
                    <dd>
                      {currentEffect ? (
                        <>
                          <span className="music-badge">音效</span>
                          {currentEffect.name}
                        </>
                      ) : project.effectAssetId ? (
                        <span className="music-mono">{project.effectAssetId}</span>
                      ) : (
                        <span className="music-muted">未配置</span>
                      )}
                    </dd>
                  </div>
                  <div>
                    <dt>音乐音量</dt>
                    <dd className="music-mono">{formatGain(project.musicVolume)}</dd>
                  </div>
                  <div>
                    <dt>音效音量</dt>
                    <dd className="music-mono">{formatGain(project.effectVolume)}</dd>
                  </div>
                  <div>
                    <dt>主输出</dt>
                    <dd className="music-mono">{formatGain(project.masterVolume)}</dd>
                  </div>
                  <div>
                    <dt>压低音乐</dt>
                    <dd>{project.duckMusic ? '开（人声出现时压低）' : '关'}</dd>
                  </div>
                  <div>
                    <dt>纯人声</dt>
                    <dd>{project.voiceOnly ? '是（忽略音乐与音效）' : '否'}</dd>
                  </div>
                </dl>

                {project.musicAssetId ? (
                  <audio
                    className="music-audio-player"
                    controls
                    preload="none"
                    src={getAudioFileUrl(project.musicAssetId)}
                  />
                ) : null}

                <div className="music-divider" />

                <div className="music-section-head">
                  <h3>写入配乐</h3>
                  <span>重新拉取最新工程后，只改配乐字段再保存</span>
                </div>

                <div className="music-stack">
                  <label className="music-field">
                    <span>音乐素材</span>
                    <select
                      className="music-select"
                      value={musicChoice}
                      onChange={(event) => setMusicChoice(event.target.value)}
                    >
                      <option value="">不设置</option>
                      {musicAssets.map((asset) => (
                        <option key={asset.id} value={asset.id}>
                          {asset.name}（{asset.style}）
                        </option>
                      ))}
                    </select>
                  </label>
                  <div className="music-actions">
                    <button
                      type="button"
                      className="music-button music-button--primary"
                      disabled={applying || !musicChoice || musicChoice === project.musicAssetId}
                      onClick={() => void onApplyMusic(musicChoice || null)}
                    >
                      {applying ? (
                        <LoaderCircle size={13} className="music-spin" aria-hidden="true" />
                      ) : null}
                      应用为配乐
                    </button>
                    <button
                      type="button"
                      className="music-button music-button--ghost"
                      disabled={applying || !project.musicAssetId}
                      onClick={() => void onApplyMusic(null)}
                    >
                      清除配乐
                    </button>
                  </div>

                  <label className="music-field">
                    <span>音效素材</span>
                    <select
                      className="music-select"
                      value={effectChoice}
                      onChange={(event) => setEffectChoice(event.target.value)}
                    >
                      <option value="">不设置</option>
                      {effectAssets.map((asset) => (
                        <option key={asset.id} value={asset.id}>
                          {asset.name}（{asset.style}）
                        </option>
                      ))}
                    </select>
                  </label>
                  <div className="music-actions">
                    <button
                      type="button"
                      className="music-button music-button--ghost"
                      disabled={applying || !effectChoice || effectChoice === project.effectAssetId}
                      onClick={() => void onApplyEffect(effectChoice || null)}
                    >
                      应用为音效
                    </button>
                    <button
                      type="button"
                      className="music-button music-button--ghost"
                      disabled={applying || !project.effectAssetId}
                      onClick={() => void onApplyEffect(null)}
                    >
                      清除音效
                    </button>
                  </div>
                </div>

                <div className="music-divider" />

                <div className="music-section-head">
                  <h3>响度（只测不归一化）</h3>
                  <span>ffmpeg loudnorm 测量 · 参考 I=-14</span>
                </div>

                {musicAnalysis &&
                musicAnalysis.measuredLufs != null &&
                typeof musicAnalysis.measuredLufs === 'number' ? (
                  <div className="music-loudness">
                    <div className="music-loudness-row">
                      <span>
                        实测 <strong>{musicAnalysis.measuredLufs.toFixed(1)} LUFS</strong>
                      </span>
                      <span className="music-badge music-badge--muted">目标 {lufsTarget} LUFS</span>
                      <span className="music-hint">
                        差值 {(musicAnalysis.measuredLufs - lufsTarget >= 0 ? '+' : '') +
                          (musicAnalysis.measuredLufs - lufsTarget).toFixed(1)}{' '}
                        LU · {musicAnalysis.loudnessMethod ?? '测量方法未记录'}
                      </span>
                    </div>
                    <div className="music-loudness-targets" role="radiogroup" aria-label="响度目标（仅对照）">
                      {LUFS_TARGETS.map((target) => (
                        <button
                          key={target}
                          type="button"
                          role="radio"
                          aria-checked={lufsTarget === target}
                          className={`music-mini-button ${lufsTarget === target ? 'is-active' : ''}`}
                          onClick={() => setLufsTarget(target)}
                        >
                          {target} LUFS
                        </button>
                      ))}
                    </div>
                    <p className="music-hint">
                      目标只用于对照，不写入工程、不改音频。后端只测量响度，没有归一化接口，渲染端也尚未消费该目标。
                    </p>
                  </div>
                ) : musicAnalysis ? (
                  <p className="music-empty">
                    配乐已完成节拍分析，但响度测量不可用（ffmpeg loudnorm 未成功）。目标选择仅用于对照。
                  </p>
                ) : (
                  <p className="music-empty">
                    当前配乐还没有分析数据。到左侧素材库对这首音乐点「分析节拍」，完成后这里会显示实测响度。
                  </p>
                )}

                <div className="music-divider" />

                <div className="music-section-head">
                  <h3>自动 ducking</h3>
                  <span>按人声区间生成包络；没有真实人声时诚实降级</span>
                </div>

                {ducking ? (
                  <>
                    <dl className="music-kv">
                      <div>
                        <dt>写入目标</dt>
                        <dd>{duckingTargetLabel(ducking)}</dd>
                      </div>
                      <div>
                        <dt>人声区间</dt>
                        <dd>{ducking.voiceIntervals.length} 段</dd>
                      </div>
                      <div>
                        <dt>关键帧</dt>
                        <dd>
                          {ducking.keyframes.length} 个 · {ducking.targetDb} dB
                        </dd>
                      </div>
                    </dl>

                    {ducking.keyframes.length > 0 ? (
                      <>
                        <EnvelopeCurve
                          keyframes={ducking.keyframes}
                          voiceIntervals={ducking.voiceIntervals}
                          targetDb={ducking.targetDb}
                          durationSeconds={Math.max(
                            project?.end ?? 0,
                            ducking.keyframes[ducking.keyframes.length - 1].time,
                            1,
                          )}
                        />
                        <p className="music-envelope-meta">
                          虚线 = 目标 {ducking.targetDb} dB · 绿线 = 增益包络 · 蓝块 = 人声区间 ·{' '}
                          {ducking.keyframes.length} 个关键帧
                        </p>
                      </>
                    ) : (
                      <p className="music-envelope-empty">还没有关键帧：本次生成降级了，没有可预览的包络。</p>
                    )}

                    {ducking.warnings.length > 0 ? (
                      <ul className="music-warning-list">
                        {ducking.warnings.map((warning) => (
                          <li key={warning}>{warning}</li>
                        ))}
                      </ul>
                    ) : null}
                    {ducking.keyframes.length === 0 && ducking.target === 'none' ? (
                      <p className="music-note music-note--warning">
                        <AlertTriangle size={13} aria-hidden="true" />
                        没有真实人声转写，后端未生成 ducking 包络。先到编辑器「字幕与转写」完成转写，再回来生成。
                      </p>
                    ) : null}
                  </>
                ) : (
                  <p className="music-empty">
                    还没有生成 ducking 包络。生成后会显示包络曲线（需要工程已有配乐与人声转写）。
                  </p>
                )}

                <div className="music-actions">
                  <button
                    type="button"
                    className="music-button music-button--primary"
                    disabled={duckingLoading || applying || !project.musicAssetId}
                    onClick={() => void onGenerateDucking()}
                  >
                    {duckingLoading ? (
                      <LoaderCircle size={13} className="music-spin" aria-hidden="true" />
                    ) : null}
                    {ducking ? '重新生成 ducking 包络' : '一键生成 ducking 包络'}
                  </button>
                </div>
                {!project.musicAssetId ? (
                  <p className="music-note">先应用配乐，再生成 ducking 包络。</p>
                ) : null}

                <p className="music-note music-note--warning">
                  <AlertTriangle size={13} aria-hidden="true" />
                  保存会调用 PUT /api/studio/clips/{'{id}'}/editor 覆盖该工程的整个工程文件（先取最新版本再只改配乐字段）。
                  若同时在编辑器里修改此工程，后保存的一方会覆盖前者；写完后编辑器需重新载入才能看到新配乐。
                </p>
              </div>
            ) : null}
          </>
        )}
      </div>
    </section>
  );
}
