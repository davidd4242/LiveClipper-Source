import { useMemo, useRef, useState } from 'react';
import {
  ChevronDown,
  Download,
  ExternalLink,
  LoaderCircle,
  Music,
  Pause,
  Play,
  RefreshCw,
  Search,
  Upload,
} from 'lucide-react';
import {
  analyzeMusicAsset,
  getAudioFileUrl,
  importAudioFile,
  importOnlineAudio,
  searchOnlineAudio,
  type AudioAssetKind,
  type MusicLibraryAsset,
  type OnlineAudio,
  type OnlineAudioLicense,
  type OnlineAudioSearchResult,
  type OnlineAudioSource,
} from '../../api/music';
import { SegmentedTabs } from '../../design';
import { AssetBeatPanel } from './AssetBeatPanel';
import { errorMessage, formatSeconds } from './utils';

export type ToastTone = 'info' | 'success' | 'error';

interface LibraryPanelProps {
  assets: MusicLibraryAsset[];
  loading: boolean;
  onRefresh: () => Promise<void>;
  onToast: (tone: ToastTone, text: string) => void;
  projectMusicAssetId: string | null;
  applying: boolean;
  onApplyMusic: (assetId: string) => void;
}

const SOURCE_OPTIONS: Array<{ value: OnlineAudioSource; label: string }> = [
  { value: 'all', label: '全部来源' },
  { value: 'jamendo', label: 'Jamendo（音乐）' },
  { value: 'freesound', label: 'Freesound（音效）' },
];

const LICENSE_OPTIONS: Array<{ value: OnlineAudioLicense; label: string }> = [
  { value: 'usable', label: '可商用优先' },
  { value: 'cc0', label: '仅 CC0 公有领域' },
];

export function LibraryPanel({
  assets,
  loading,
  onRefresh,
  onToast,
  projectMusicAssetId,
  applying,
  onApplyMusic,
}: LibraryPanelProps) {
  const [tab, setTab] = useState<AudioAssetKind>('music');
  const [filter, setFilter] = useState('');
  const [playingId, setPlayingId] = useState<string | null>(null);
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const audioRef = useRef<HTMLAudioElement | null>(null);

  const [onlineOpen, setOnlineOpen] = useState(false);
  const [query, setQuery] = useState('');
  const [onlineKind, setOnlineKind] = useState<AudioAssetKind>('music');
  const [source, setSource] = useState<OnlineAudioSource>('all');
  const [license, setLicense] = useState<OnlineAudioLicense>('usable');
  const [searching, setSearching] = useState(false);
  const [result, setResult] = useState<OnlineAudioSearchResult | null>(null);
  const [importingOnlineId, setImportingOnlineId] = useState<string | null>(null);
  const [onlineError, setOnlineError] = useState<string | null>(null);
  const [analyzingId, setAnalyzingId] = useState<string | null>(null);

  const fileRef = useRef<HTMLInputElement | null>(null);
  const [importKind, setImportKind] = useState<AudioAssetKind>('music');
  const [importStyle, setImportStyle] = useState('');
  const [importing, setImporting] = useState(false);
  const [importError, setImportError] = useState<string | null>(null);

  const counts = useMemo(
    () => ({
      music: assets.filter((asset) => asset.kind === 'music').length,
      effect: assets.filter((asset) => asset.kind === 'effect').length,
    }),
    [assets],
  );

  const visibleAssets = useMemo(() => {
    const keyword = filter.trim().toLowerCase();
    return assets
      .filter((asset) => asset.kind === tab)
      .filter(
        (asset) =>
          !keyword || `${asset.name} ${asset.style}`.toLowerCase().includes(keyword),
      );
  }, [assets, tab, filter]);

  function togglePlay(id: string, url: string) {
    const audio = audioRef.current;
    if (!audio) return;
    if (playingId === id) {
      audio.pause();
      setPlayingId(null);
      return;
    }
    audio.src = url;
    audio.currentTime = 0;
    void audio
      .play()
      .then(() => setPlayingId(id))
      .catch((error: unknown) => {
        setPlayingId(null);
        onToast('error', `无法播放音频：${errorMessage(error)}`);
      });
  }

  function stopPlayback() {
    audioRef.current?.pause();
    setPlayingId(null);
  }

  async function runSearch(nextPage: number) {
    if (!query.trim()) {
      setOnlineError('请输入搜索关键词（中文关键词会由后端翻译后检索）。');
      return;
    }
    setSearching(true);
    setOnlineError(null);
    try {
      const payload = await searchOnlineAudio({
        query: query.trim(),
        kind: onlineKind,
        source,
        license,
        page: nextPage,
      });
      setResult(payload);
    } catch (error) {
      setResult(null);
      setOnlineError(errorMessage(error));
    } finally {
      setSearching(false);
    }
  }

  async function handleOnlineImport(item: OnlineAudio) {
    setImportingOnlineId(item.id);
    setOnlineError(null);
    try {
      const asset = await importOnlineAudio(item.id, item.kind);
      onToast('success', `已导入「${asset.name}」到本地音频库`);
      setResult((current) =>
        current ? { ...current, results: current.results.filter((row) => row.id !== item.id) } : current,
      );
      await onRefresh();
    } catch (error) {
      setOnlineError(errorMessage(error));
    } finally {
      setImportingOnlineId(null);
    }
  }

  async function handleFile(file: File | undefined) {
    if (!file) return;
    setImporting(true);
    setImportError(null);
    try {
      const asset = await importAudioFile(file, importKind, importStyle);
      onToast('success', `已导入「${asset.name}」到本地音频库`);
      await onRefresh();
    } catch (error) {
      setImportError(errorMessage(error));
    } finally {
      setImporting(false);
      if (fileRef.current) fileRef.current.value = '';
    }
  }

  async function handleAnalyze(asset: MusicLibraryAsset) {
    setAnalyzingId(asset.id);
    try {
      const analysis = await analyzeMusicAsset(asset.id);
      if ('degraded' in analysis && analysis.degraded) {
        onToast('info', `「${asset.name}」已降级：${analysis.reason ?? '当前环境无法分析节拍'}`);
      } else {
        onToast('success', `已分析「${asset.name}」：BPM ${analysis.bpm ?? '—'}`);
      }
      await onRefresh();
    } catch (error) {
      onToast('error', `节拍分析失败：${errorMessage(error)}`);
    } finally {
      setAnalyzingId(null);
    }
  }

  return (
    <section className="music-panel music-library" aria-label="音频素材库">
      <header className="music-panel-head">
        <div className="music-panel-title">
          <Music size={14} aria-hidden="true" />
          <span>音频素材库</span>
          <span className="music-panel-count">{assets.length}</span>
        </div>
        <button
          type="button"
          className="music-icon-button"
          title="刷新素材库"
          aria-label="刷新素材库"
          disabled={loading}
          onClick={() => {
            stopPlayback();
            void onRefresh();
          }}
        >
          <RefreshCw size={13} className={loading ? 'music-spin' : undefined} aria-hidden="true" />
        </button>
      </header>

      <div className="music-panel-body">
        <SegmentedTabs
          ariaLabel="音频素材类型"
          value={tab}
          onChange={setTab}
          items={[
            { value: 'music', label: '音乐', count: counts.music },
            { value: 'effect', label: '音效', count: counts.effect },
          ]}
        />

        <div className="music-search-row">
          <Search size={13} aria-hidden="true" />
          <input
            className="music-input"
            type="search"
            placeholder="筛选名称或风格"
            aria-label="筛选本地音频素材"
            value={filter}
            onChange={(event) => setFilter(event.target.value)}
          />
        </div>

        <div className="music-audio-list">
          {loading && assets.length === 0 ? (
            <p className="music-empty">正在读取本地音频库…</p>
          ) : visibleAssets.length === 0 ? (
            <p className="music-empty">
              {filter.trim()
                ? '没有匹配的素材，换个关键词试试。'
                : tab === 'music'
                  ? '音频库里还没有音乐素材，可以导入本地文件或在线搜索。'
                  : '音频库里还没有音效素材，可以导入本地文件。'}
            </p>
          ) : (
            visibleAssets.map((asset) => {
              const expanded = expandedId === asset.id;
              const isMusic = asset.kind === 'music';
              return (
                <div className={`music-audio-item ${expanded ? 'is-expanded' : ''}`} key={asset.id}>
                  <div className={`music-audio-row ${expanded ? 'is-expanded' : ''}`}>
                    <button
                      type="button"
                      className={`music-play ${playingId === asset.id ? 'is-playing' : ''}`}
                      aria-label={playingId === asset.id ? `暂停 ${asset.name}` : `试听 ${asset.name}`}
                      aria-pressed={playingId === asset.id}
                      onClick={() => togglePlay(asset.id, getAudioFileUrl(asset.id))}
                    >
                      {playingId === asset.id ? (
                        <Pause size={12} aria-hidden="true" />
                      ) : (
                        <Play size={12} aria-hidden="true" />
                      )}
                    </button>
                    <div className="music-audio-text">
                      <strong>{asset.name}</strong>
                      <span>
                        {asset.style}
                        {asset.builtIn ? ' · 内置' : ' · 本地文件'}
                      </span>
                    </div>
                    <div className="music-audio-meta">
                      {isMusic && asset.analysis?.bpm ? (
                        <span
                          className="music-badge music-badge--bpm"
                          title={`${asset.analysis.method} · 置信度 ${(asset.analysis.confidence * 100).toFixed(0)}%`}
                        >
                          BPM {asset.analysis.bpm.toFixed(1)}
                        </span>
                      ) : isMusic ? (
                        <span className="music-badge music-badge--muted">未分析</span>
                      ) : null}
                      {isMusic && asset.analysis?.measuredLufs != null ? (
                        <span className="music-badge music-badge--lufs">
                          {asset.analysis.measuredLufs.toFixed(1)} LUFS
                        </span>
                      ) : null}
                      {isMusic ? (
                        <>
                          <button
                            type="button"
                            className="music-mini-button"
                            disabled={analyzingId !== null}
                            onClick={() => void handleAnalyze(asset)}
                          >
                            {analyzingId === asset.id ? '分析中…' : asset.analysis?.bpm ? '重新分析' : '分析节拍'}
                          </button>
                          <button
                            type="button"
                            className="music-mini-button"
                            aria-expanded={expanded}
                            onClick={() => setExpandedId((current) => (current === asset.id ? null : asset.id))}
                          >
                            {expanded ? '收起' : '节拍网格'}
                          </button>
                        </>
                      ) : null}
                    </div>
                    <span className="music-badge">{isMusic ? '音乐' : '音效'}</span>
                  </div>
                  {isMusic && expanded ? (
                    <AssetBeatPanel
                      asset={asset}
                      projectMusicAssetId={projectMusicAssetId}
                      applying={applying}
                      onAnalyze={() => handleAnalyze(asset)}
                      onApplyMusic={onApplyMusic}
                    />
                  ) : null}
                </div>
              );
            })
          )}
        </div>

        <div className="music-divider" />

        <button
          type="button"
          className="music-subhead"
          aria-expanded={onlineOpen}
          onClick={() => setOnlineOpen((current) => !current)}
        >
          <Download size={13} aria-hidden="true" />
          <span>在线搜索与导入</span>
          <ChevronDown
            size={13}
            aria-hidden="true"
            className={`music-chevron ${onlineOpen ? 'is-open' : ''}`}
          />
        </button>

        {onlineOpen ? (
          <div className="music-stack">
            <div className="music-field-row">
              <input
                className="music-input"
                type="search"
                placeholder="例如：piano、city pop、雨声"
                aria-label="在线音频搜索关键词"
                value={query}
                onChange={(event) => setQuery(event.target.value)}
                onKeyDown={(event) => {
                  if (event.key === 'Enter') void runSearch(1);
                }}
              />
              <button
                type="button"
                className="music-button music-button--primary"
                disabled={searching}
                onClick={() => void runSearch(1)}
              >
                {searching ? <LoaderCircle size={13} className="music-spin" aria-hidden="true" /> : null}
                搜索
              </button>
            </div>

            <div className="music-field-row music-field-row--wrap">
              <label className="music-inline-label">
                类型
                <select
                  className="music-select"
                  value={onlineKind}
                  onChange={(event) => setOnlineKind(event.target.value as AudioAssetKind)}
                >
                  <option value="music">音乐</option>
                  <option value="effect">音效</option>
                </select>
              </label>
              <label className="music-inline-label">
                来源
                <select
                  className="music-select"
                  value={source}
                  onChange={(event) => setSource(event.target.value as OnlineAudioSource)}
                >
                  {SOURCE_OPTIONS.map((option) => (
                    <option key={option.value} value={option.value}>
                      {option.label}
                    </option>
                  ))}
                </select>
              </label>
              <label className="music-inline-label">
                许可
                <select
                  className="music-select"
                  value={license}
                  onChange={(event) => setLicense(event.target.value as OnlineAudioLicense)}
                >
                  {LICENSE_OPTIONS.map((option) => (
                    <option key={option.value} value={option.value}>
                      {option.label}
                    </option>
                  ))}
                </select>
              </label>
            </div>

            {onlineError ? <p className="music-error">{onlineError}</p> : null}

            {result ? (
              <>
                <div className="music-result-meta">
                  <span>
                    「{result.query || query}」共 {result.total} 条 · 第 {result.page}/{result.pageCount} 页
                  </span>
                  <div className="music-pager">
                    <button
                      type="button"
                      className="music-button music-button--ghost"
                      disabled={searching || result.page <= 1}
                      onClick={() => void runSearch(result.page - 1)}
                    >
                      上一页
                    </button>
                    <button
                      type="button"
                      className="music-button music-button--ghost"
                      disabled={searching || result.page >= result.pageCount}
                      onClick={() => void runSearch(result.page + 1)}
                    >
                      下一页
                    </button>
                  </div>
                </div>
                <div className="music-online-list">
                  {result.results.length === 0 ? (
                    <p className="music-empty">没有返回结果，换关键词或来源再试。</p>
                  ) : (
                    result.results.map((item) => (
                      <div className="music-online-row" key={item.id}>
                        <div className="music-online-text">
                          <strong>{item.title || '未命名作品'}</strong>
                          <span>
                            {item.creator || '未知作者'} · {item.source} · {formatSeconds(item.seconds)} ·{' '}
                            {item.license}
                          </span>
                        </div>
                        <div className="music-online-actions">
                          <button
                            type="button"
                            className="music-button music-button--ghost"
                            onClick={() => togglePlay(item.id, item.audioUrl)}
                          >
                            {playingId === item.id ? '暂停' : '试听'}
                          </button>
                          <a
                            className="music-icon-button"
                            href={item.landingUrl}
                            target="_blank"
                            rel="noreferrer"
                            title="打开来源页面"
                            aria-label="打开来源页面"
                          >
                            <ExternalLink size={13} aria-hidden="true" />
                          </a>
                          <button
                            type="button"
                            className="music-button music-button--primary"
                            disabled={importingOnlineId !== null}
                            onClick={() => void handleOnlineImport(item)}
                          >
                            {importingOnlineId === item.id ? (
                              <LoaderCircle size={13} className="music-spin" aria-hidden="true" />
                            ) : null}
                            导入
                          </button>
                        </div>
                      </div>
                    ))
                  )}
                </div>
                <p className="music-hint">
                  导入会把音频下载到本地音频库；结果有约 10 分钟服务端缓存，过期后重新搜索即可。使用时请遵守来源许可并保留署名。
                </p>
              </>
            ) : null}
          </div>
        ) : null}

        <div className="music-divider" />

        <div className="music-subhead">
          <Upload size={13} aria-hidden="true" />
          <span>导入本地音频</span>
        </div>
        <div className="music-stack">
          <div className="music-field-row music-field-row--wrap">
            <label className="music-inline-label">
              类型
              <select
                className="music-select"
                value={importKind}
                onChange={(event) => setImportKind(event.target.value as AudioAssetKind)}
              >
                <option value="music">音乐</option>
                <option value="effect">音效</option>
              </select>
            </label>
            <label className="music-inline-label music-inline-label--grow">
              风格
              <input
                className="music-input"
                type="text"
                placeholder="例如：轻快电子"
                value={importStyle}
                onChange={(event) => setImportStyle(event.target.value)}
              />
            </label>
          </div>
          <input
            ref={fileRef}
            className="music-hidden-input"
            type="file"
            accept=".wav,.mp3,.m4a,.flac,.ogg,audio/*"
            aria-label="选择本地音频文件"
            onChange={(event) => void handleFile(event.target.files?.[0])}
          />
          <button
            type="button"
            className="music-button music-button--ghost"
            disabled={importing}
            onClick={() => fileRef.current?.click()}
          >
            {importing ? <LoaderCircle size={13} className="music-spin" aria-hidden="true" /> : <Upload size={13} aria-hidden="true" />}
            选择音频文件导入
          </button>
          <p className="music-hint">支持 wav / mp3 / m4a / flac / ogg，单文件不超过 80 MB。</p>
          {importError ? <p className="music-error">{importError}</p> : null}
        </div>
      </div>

      <audio
        ref={audioRef}
        hidden
        preload="none"
        onEnded={() => setPlayingId(null)}
        onError={() => {
          setPlayingId(null);
          onToast('error', '音频无法播放：文件缺失或来源地址不可用');
        }}
      />
    </section>
  );
}
