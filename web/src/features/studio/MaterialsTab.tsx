import { getVideoUrl } from '../../api/client';
import type { ClipDto } from '../../api/types';
import type { AudioAsset } from '../../api/music';
import { getAudioFileUrl } from '../../api/music';
import { VideoThumbnail } from '../../components/VideoThumbnail';
import { clipTitle, dateLabel, thumbStyle } from './workbench';

interface MaterialsTabProps {
  clips: ClipDto[];
  assets: AudioAsset[];
  selectedClipId: string | null;
  loading: boolean;
  error: string | null;
  onSelectClip: (clipId: string) => void;
  onOpenEditor: (clipId: string) => void;
  onRefresh: () => void;
}

export function MaterialsTab({
  clips,
  assets,
  selectedClipId,
  loading,
  error,
  onSelectClip,
  onOpenEditor,
  onRefresh,
}: MaterialsTabProps) {
  const musicAssets = assets.filter((asset) => asset.kind === 'music');
  const effectAssets = assets.filter((asset) => asset.kind === 'effect');

  return (
    <section className="wb-tab" aria-label="素材库">
      <header className="wb-detail-head">
        <div className="wb-detail-title">
          <span className="wb-kicker">MATERIALS</span>
          <h2>素材库</h2>
          <p className="wb-detail-path">
            视频素材来自 /api/clips，音频素材来自 /api/assets；缩略图由浏览器端 canvas 从视频帧生成。
          </p>
        </div>
        <div className="wb-actions">
          <span className="wb-count">
            {clips.length} 视频 · {assets.length} 音频
          </span>
          <button type="button" className="wb-button wb-button--ghost" onClick={onRefresh}>
            刷新素材
          </button>
        </div>
      </header>

      {error ? (
        <div className="wb-error">
          <strong>素材读取失败</strong>
          <span>{error}</span>
          <button type="button" className="wb-button" onClick={onRefresh}>
            重试
          </button>
        </div>
      ) : null}

      <div className="wb-section">
        <header className="wb-section-head">
          <h3>视频素材</h3>
          <span>来源 /api/clips · {clips.length} 条</span>
        </header>
        {loading && clips.length === 0 ? (
          <div className="wb-empty">
            <strong>正在读取视频素材…</strong>
          </div>
        ) : clips.length === 0 ? (
          <div className="wb-empty">
            <strong>还没有视频素材</strong>
            <span>用顶部「导入素材」选择本地视频与字幕文件，或从直播录制入库。</span>
          </div>
        ) : (
          <div className="wb-media-grid">
            {clips.map((clip, index) => (
              <article
                key={clip.id}
                className={`wb-media-card${clip.id === selectedClipId ? ' is-selected' : ''}`}
                style={thumbStyle(index)}
              >
                <div className="wb-media-thumb">
                  <VideoThumbnail src={getVideoUrl(clip.id)} alt={`${clipTitle(clip)} 缩略图`} seekRatio={0.18} />
                  <span className="wb-media-tag">{clip.srtPath ? '含字幕' : '视频'}</span>
                </div>
                <div className="wb-media-body">
                  <strong className="wb-ellipsis" title={clipTitle(clip)}>
                    {clipTitle(clip)}
                  </strong>
                  <span className="wb-media-meta wb-ellipsis" title={clip.path}>
                    {clip.path}
                  </span>
                  <span className="wb-media-meta">{dateLabel(clip.createdAt)}</span>
                </div>
                <div className="wb-media-actions">
                  <button type="button" className="wb-button wb-button--ghost" onClick={() => onSelectClip(clip.id)}>
                    查看详情
                  </button>
                  <button type="button" className="wb-button wb-button--ghost" onClick={() => onOpenEditor(clip.id)}>
                    打开编辑器
                  </button>
                </div>
              </article>
            ))}
          </div>
        )}
      </div>

      <div className="wb-section">
        <header className="wb-section-head">
          <h3>音频素材</h3>
          <span>来源 /api/assets · 音乐 {musicAssets.length} · 音效 {effectAssets.length}</span>
        </header>
        {assets.length === 0 ? (
          <div className="wb-empty">
            <strong>音频库为空</strong>
            <span>可在「音乐工作室」生成或导入音频后回到这里试听。</span>
          </div>
        ) : (
          <div className="wb-audio-list">
            {assets.map((asset) => (
              <div key={asset.id} className="wb-audio-row">
                <div className="wb-audio-text">
                  <strong className="wb-ellipsis" title={asset.name}>
                    {asset.name}
                  </strong>
                  <span className="wb-media-meta">
                    {asset.kind === 'music' ? '音乐' : '音效'}
                    {asset.style ? ` · ${asset.style}` : ''}
                    {asset.builtIn ? ' · 内置' : ' · 本地'}
                  </span>
                </div>
                <audio className="wb-audio-player" controls preload="none" src={getAudioFileUrl(asset.id)}>
                  浏览器不支持音频播放
                </audio>
              </div>
            ))}
          </div>
        )}
      </div>
    </section>
  );
}
