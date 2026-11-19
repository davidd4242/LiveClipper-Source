import {autoClipAvailability} from './autoClipAvailability';
import { useState } from 'react';
import { LoaderCircle, RefreshCw, Scissors, ToggleLeft, ToggleRight } from 'lucide-react';
import type { AutoClipItem, AutoClipPayload, LegacyRoomStatus } from './liveApi';

interface Props {
  payload: AutoClipPayload | null;
  rooms: LegacyRoomStatus[] | null;
  loading: boolean;
  error: string | null;
  busy: boolean;
  onToggle: (enabled: boolean) => void;
  onRefresh: () => void;
}

const STATE_LABEL: Record<string, string> = {
  Collecting: '收集中',
  Materializing: '生成中',
  Ready: '已保存',
  Failed: '失败',
};

const STATE_TONE: Record<string, string> = {
  Collecting: 'is-wait',
  Materializing: 'is-busy',
  Ready: 'is-ok',
  Failed: 'is-bad',
};

function seconds(value: number): string {
  const total = Math.max(0, Math.round(value));
  const minutes = Math.floor(total / 60);
  const rest = total % 60;
  return minutes > 0 ? `${minutes}m${String(rest).padStart(2, '0')}s` : `${rest}s`;
}

function rate(value: number): string {
  return `${value.toFixed(3)}/s`;
}

export function AutoClipPanel({ payload, rooms, loading, error, busy, onToggle, onRefresh }: Props) {
  const [open, setOpen] = useState<string | null>(null);
  const policy = payload?.policy ?? null;
  const heat = payload?.heat ?? null;
  const clips = payload?.clips ?? [];
  const enabled = payload?.enabled ?? false;
  const availability=autoClipAvailability(payload?.enabled??null,rooms);
  const current = heat?.messageRate ?? 0;
  const trigger = heat?.triggerLine ?? 0;
  const release = heat?.releaseLine ?? 0;
  const peak = Math.max(trigger * 1.25, current, 0.01);
  const percent = (value: number) => `${Math.min(100, (value / peak) * 100).toFixed(1)}%`;

  return (
    <section className="lw-panel lw-auto-clip" aria-label="自动切片">
      <header className="lw-panel__head">
        <div>
          <h2>
            <Scissors size={12} /> 自动切片
          </h2>
          <span>
            弹幕热度
            {policy
              ? ` · 前 ${Math.round(policy.baselineSeconds / 60)} 分钟基准 · ${Math.round(policy.triggerRatio * 100)}% 触发`
              : ''}
          </span>
        </div>
        <div className="lw-auto-clip__head">
          <span className={`lw-auto-clip__tag ${availability.ready ? 'is-ok' : 'is-wait'}`}>
            {availability.label}
          </span>
          <button
            type="button"
            className="live-btn live-btn--ghost"
            title={enabled ? '停用自动切片' : '启用自动切片'}
            disabled={busy || loading || !payload}
            onClick={() => onToggle(!enabled)}
          >
            {busy ? (
              <LoaderCircle size={11} className="live-spin" />
            ) : enabled ? (
              <ToggleRight size={13} />
            ) : (
              <ToggleLeft size={13} />
            )}
          </button>
          <button
            type="button"
            className="live-btn live-btn--ghost"
            title="刷新自动切片状态"
            disabled={loading}
            onClick={onRefresh}
          >
            <RefreshCw size={11} className={loading ? 'live-spin' : undefined} />
          </button>
        </div>
      </header>

      <div
        className="lw-panel__body lw-auto-clip__body"
        role="region"
        aria-label="自动切片与弹幕热度"
        tabIndex={0}
      >
        {error ? <p className="lw-auto-clip__note is-bad">{error}</p> : null}

        {heat ? (
          <div className="lw-auto-clip__heat">
            <div className="lw-auto-clip__rates">
              <span className={heat.active ? 'is-hot' : heat.inBaseline ? 'is-wait' : undefined}>
                当前 {rate(current)}
              </span>
              <span>基准 {rate(heat.baseline)}</span>
              <span>触发 {rate(trigger)}</span>
              <span>释放 {rate(release)}</span>
            </div>
            <div className="lw-auto-clip__meter" role="img" aria-label="弹幕热度与阈值">
              <span className="lw-auto-clip__fill" style={{ width: percent(current) }} />
              <i className="lw-auto-clip__tick is-release" style={{ left: percent(release) }} />
              <i className="lw-auto-clip__tick is-trigger" style={{ left: percent(trigger) }} />
            </div>
            <p className="lw-auto-clip__reason">{heat.reason || '等待弹幕热度数据'}</p>
          </div>
        ) : loading ? (
          <p className="lw-auto-clip__note">
            <LoaderCircle size={11} className="live-spin" /> 读取热度状态…
          </p>
        ) : null}

        {policy ? (
          <div className="lw-auto-clip__policy">
            <span>基线 {seconds(policy.baselineSeconds)}</span>
            <span>触发 {Math.round(policy.triggerRatio * 100)}%</span>
            <span>释放 {Math.round(policy.releaseRatio * 100)}%</span>
            <span>
              前 {seconds(policy.preRollSeconds)} + 后 {seconds(policy.postRollSeconds)}
            </span>
            <span>比较 {seconds(policy.comparisonSeconds)}</span>
            <span>释放确认 {seconds(policy.releaseSeconds)}</span>
            <span>动态重评：低于基准超过10分钟 / 20分钟第3个独立自动高光</span>
          </div>
        ) : null}

        <div className="lw-auto-clip__clips">
          <div className="lw-auto-clip__clips-head">
            <span>最近自动切片</span>
            <span className="lw-mono">{clips.length} 条</span>
          </div>
          {clips.length === 0 ? (
            <p className="lw-auto-clip__note">{availability.ready?'还没有自动切片。具备临时缓存后，热度达到触发条件才会收集高光。':availability.label+'；当前不会自动产生高光。'}</p>
          ) : (
            <ul>
              {clips.map((clip: AutoClipItem) => (
                <li key={clip.id} className={open === clip.id ? 'is-open' : undefined}>
                  <button
                    type="button"
                    className="lw-auto-clip__row"
                    onClick={() => setOpen((value) => (value === clip.id ? null : clip.id))}
                  >
                    <span className={`lw-auto-clip__tag ${STATE_TONE[clip.state ?? ''] ?? 'is-wait'}`}>
                      {STATE_LABEL[clip.state ?? ''] ?? clip.state ?? '未知'}
                    </span>
                    {clip.partial ? <span className="lw-mono">部分</span> : null}
                    <span className="lw-auto-clip__row-time">
                      {clip.triggerTime != null ? `触发 ${seconds(clip.triggerTime)}` : '—'}
                    </span>
                    <span className="lw-mono lw-auto-clip__row-range">
                      {clip.start != null && clip.end != null ? `${seconds(clip.start)} → ${seconds(clip.end)}` : ''}
                    </span>
                  </button>
                  {open === clip.id ? (
                    <div className="lw-auto-clip__row-detail">
                      <span className="lw-mono">{clip.path}</span>
                      {clip.state === 'Ready' ? (
                        <a className="live-btn live-btn--ghost" href={`/api/clips/${clip.id}/video`} download>
                          下载 MP4
                        </a>
                      ) : null}
                    </div>
                  ) : null}
                </li>
              ))}
            </ul>
          )}
        </div>
      </div>
    </section>
  );
}
