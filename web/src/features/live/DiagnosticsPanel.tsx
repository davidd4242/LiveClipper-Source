import {
  Activity,
  AlertTriangle,
  CheckCircle2,
  HardDrive,
  LoaderCircle,
  RefreshCw,
  ShieldAlert,
} from 'lucide-react';
import { Button, EmptyState } from '../../design';
import type { LiveDiagnosticsReport } from './liveApi';
import {
  formatBytes,
  formatClock,
  formatNumber,
  formatTime,
  recoveryDescription,
  recoveryTone,
} from './format';

interface DiagnosticsPanelProps {
  report: LiveDiagnosticsReport | null;
  loading: boolean;
  error: string | null;
  onRefresh: () => void;
}

function CountCell({ label, value, tone }: { label: string; value: number | string; tone?: 'warn' | 'bad' }) {
  return (
    <div className={`lw-count${tone ? ` is-${tone}` : ''}`}>
      <span>{label}</span>
      <strong>{typeof value === 'number' ? formatNumber(value) : value}</strong>
    </div>
  );
}

export function DiagnosticsPanel({ report, loading, error, onRefresh }: DiagnosticsPanelProps) {
  const store = report?.store ?? null;
  const registry = report?.registry ?? null;
  const storage = report?.storage ?? null;
  const warnings = report?.warnings ?? [];
  const issues = store?.issues ?? [];
  const corruptCount = store?.corruptFiles ?? 0;
  const registryError = registry?.lastError ?? null;

  return (
    <section className="lw-panel" aria-label="诊断面板">
      <header className="lw-panel__head">
        <div>
          <h2>
            <ShieldAlert size={13} /> 诊断
          </h2>
          <span>
            {report
              ? `检查于 ${formatTime(report.checkedAt)} · 根目录 ${report.root}`
              : '等待诊断数据'}
          </span>
        </div>
        <button type="button" className="live-btn" onClick={onRefresh} disabled={loading}>
          {loading ? <LoaderCircle size={11} className="live-spin" /> : <RefreshCw size={10} />}
          刷新
        </button>
      </header>

      <div className="lw-panel__body" role="region" aria-label="诊断详情" tabIndex={0}>
        {error ? (
          <EmptyState
            compact
            icon={<AlertTriangle size={22} aria-hidden="true" />}
            title="诊断数据读取失败"
            description={error}
            action={
              <Button variant="outline" size="sm" onClick={onRefresh}>
                重试
              </Button>
            }
          />
        ) : null}

        {!report && loading ? (
          <EmptyState compact icon={<LoaderCircle size={22} className="live-spin" aria-hidden="true" />} title="正在采集诊断数据…" />
        ) : null}

        {report ? (
          <>
            <div className="lw-counts">
              <CountCell label="会话文件" value={store?.sessionFiles ?? 0} />
              <CountCell label="打开会话" value={store?.openSessions ?? 0} />
              <CountCell
                label="坏文件"
                value={corruptCount}
                tone={corruptCount > 0 ? 'bad' : undefined}
              />
              <CountCell label="隔离文件" value={store?.quarantinedFiles ?? 0} />
              <CountCell label="恢复房间" value={report.recovery.length} />
              <CountCell
                label="孤儿目录"
                value={report.orphanMediaDirectories.length}
                tone={report.orphanMediaDirectories.length > 0 ? 'warn' : undefined}
              />
            </div>

            <div className="lw-section">
              <h3>告警</h3>
              {warnings.length === 0 && !registryError && corruptCount === 0 && issues.length === 0 ? (
                <p className="lw-ok">
                  <CheckCircle2 size={11} /> 无告警
                </p>
              ) : (
                <ul className="lw-warn-list">
                  {warnings.map((warning) => (
                    <li key={warning}>
                      <AlertTriangle size={10} /> {warning}
                    </li>
                  ))}
                  {registryError ? (
                    <li>
                      <AlertTriangle size={10} /> 登记簿读取错误：{registryError}
                      {registry?.lastErrorAt ? `（${formatClock(registry.lastErrorAt)}）` : ''}
                    </li>
                  ) : null}
                  {corruptCount > 0
                    ? Array.from({ length: corruptCount }, (_, index) => (
                        <li key={`corrupt-${index}`}>
                          <AlertTriangle size={10} /> 会话文件损坏，已隔离为
                          *.corrupt-&lt;时间戳&gt;
                        </li>
                      ))
                    : null}
                  {issues.map((issue) => (
                    <li key={`${issue.kind}-${issue.path}-${issue.line ?? 0}`}>
                      <AlertTriangle size={10} />
                      {issue.kind} · {issue.path}
                      {issue.line != null ? `:${issue.line}` : ''}
                      {issue.sizeBytes != null ? ` · ${formatBytes(issue.sizeBytes)}` : ''} · {issue.message}
                    </li>
                  ))}
                </ul>
              )}
            </div>

            <div className="lw-section">
              <h3>会话存储</h3>
              <div className="lw-kv">
                <span>存储根（相对）</span>
                <strong className="lw-mono">{store?.root ?? '.'}</strong>
              </div>
              <div className="lw-kv">
                <span>隔离区</span>
                <strong className="lw-mono">
                  {store && store.quarantineFiles.length > 0
                    ? `${store.quarantineFiles.length} 个文件`
                    : '空'}
                </strong>
              </div>
              {store && store.quarantineFiles.length > 0 ? (
                <ul className="lw-path-list">
                  {store.quarantineFiles.map((file) => (
                    <li key={file} className="lw-mono" title={file}>
                      {file}
                    </li>
                  ))}
                </ul>
              ) : null}
              <div className="lw-kv">
                <span>登记簿</span>
                <strong className="lw-mono">{registry?.path ?? 'rooms.json'}</strong>
              </div>
              <div className="lw-kv">
                <span>登记簿状态</span>
                <strong>{registry?.loaded ? '已加载' : '未加载'}</strong>
              </div>
              {registry && registry.corruptBackups.length > 0 ? (
                <ul className="lw-path-list">
                  {registry.corruptBackups.map((backup) => (
                    <li key={backup} className="lw-mono">
                      {backup}
                    </li>
                  ))}
                </ul>
              ) : null}
            </div>

            <div className="lw-section">
              <h3>
                <HardDrive size={11} /> 独立会话库存储
              </h3>
              {storage ? (
                <>
                  <p className="lw-room__hint">此统计仅覆盖 live-core 工作区，不含主录制目录中的整场录播或高光缓存。</p>
                  <div className="lw-bar">
                    <span
                      style={{
                        width: `${Math.min(
                          100,
                          storage.maxTotalBytes > 0
                            ? (storage.totalBytes / storage.maxTotalBytes) * 100
                            : 0,
                        )}%`,
                      }}
                    />
                  </div>
                  <div className="lw-kv">
                    <span>独立会话库占用</span>
                    <strong>
                      {formatBytes(storage.totalBytes)} / {formatBytes(storage.maxTotalBytes)}
                    </strong>
                  </div>
                  <div className="lw-kv">
                    <span>工作区盘剩余（非录播盘）</span>
                    <strong>{formatBytes(storage.freeBytes)}</strong>
                  </div>
                  <div className="lw-kv">
                    <span>会话目录</span>
                    <strong>
                      {formatNumber(storage.sessionDirectories)}（过期 {formatNumber(storage.expiredSessions)}）
                    </strong>
                  </div>
                  <div className="lw-kv">
                    <span>受保护会话</span>
                    <strong>{formatNumber(storage.protectedSessions)}</strong>
                  </div>
                  {storage.quotaExceeded ? (
                    <p className="lw-room-error">
                      <AlertTriangle size={10} /> 已超过缓冲配额，将按时间清理旧会话
                    </p>
                  ) : null}
                </>
              ) : (
                <p className="lw-muted">诊断报告未包含存储信息。</p>
              )}
            </div>

            <div className="lw-section">
              <h3>
                <Activity size={11} /> 恢复状态
              </h3>
              {report.recovery.length === 0 ? (
                <p className="lw-muted">暂无待恢复房间。</p>
              ) : (
                <ul className="lw-recovery-list">
                  {report.recovery.map((state) => (
                    <li key={state.roomId} className={`lw-recovery lw-recovery--${recoveryTone(state)}`}>
                      <span className="lw-mono">房间 {state.roomId}</span>
                      <span>{recoveryDescription(state)}</span>
                      {state.lastError ? (
                        <span className="lw-recovery__error" title={state.lastError}>
                          {state.lastError}
                        </span>
                      ) : null}
                      {state.nextAttemptAt ? (
                        <span className="lw-mono">下次 {formatTime(state.nextAttemptAt)}</span>
                      ) : null}
                    </li>
                  ))}
                </ul>
              )}
            </div>

            <div className="lw-section">
              <h3>孤儿媒体目录</h3>
              {report.orphanMediaDirectories.length === 0 ? (
                <p className="lw-muted">没有孤儿媒体目录。</p>
              ) : (
                <ul className="lw-path-list">
                  {report.orphanMediaDirectories.map((directory) => (
                    <li key={directory.relativePath}>
                      <span className="lw-mono" title={directory.relativePath}>
                        {directory.relativePath}
                      </span>
                      <span>
                        {formatNumber(directory.files)} 个文件 · {formatBytes(directory.bytes)}
                      </span>
                    </li>
                  ))}
                </ul>
              )}
            </div>

            <div className="lw-section">
              <h3>弹幕队列丢弃（运行时快照）</h3>
              {report.danmakuDropped.length === 0 ? (
                <p className="lw-muted">当前没有采集中的房间快照。</p>
              ) : (
                <ul className="lw-path-list">
                  {report.danmakuDropped.map((entry) => (
                    <li key={entry.roomId}>
                      <span className="lw-mono">房间 {entry.roomId}</span>
                      <span>
                        丢弃 {formatNumber(entry.queueDropped)} / 容量 {formatNumber(entry.queueCapacity)}
                      </span>
                    </li>
                  ))}
                </ul>
              )}
            </div>
          </>
        ) : null}
      </div>
    </section>
  );
}
