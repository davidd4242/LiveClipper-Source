import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { createEngineAdapter, type FCBridge } from './adapter';
import { EngineHost } from './EngineHost';
import type { EngineAdapter } from './types';

export interface EngineStageProps {
  fps?: number;
  /** The host calls signalFirstSync after its first successful openProject. */
  onReady?: (adapter: EngineAdapter, signalFirstSync: () => void) => void;
  onFirstSync?: () => void;
  hidden?: boolean;
  suppressed?: boolean;
  mountSelector?: string;
}

const stageStyles = `
/* D89：安装版 WebView2 里引擎 canvas 的内容存在（CDP 截图看得到），屏幕却是黑的。
   曾试过 transform: translateZ(0); will-change: transform 想抬合成层，实测**无效**，
   而且很可能让问题更稳定出现，故已撤回。别再随手加这两行。 */
.lc-engine-stage { position: absolute; inset: 0; z-index: 10; overflow: hidden; pointer-events: none; }
.lc-engine-stage[data-engine-stage="ready"][data-engine-revealed="true"] { background: var(--lc-surface-sunken); }
.lc-engine-stage__host, .lc-engine-stage #fc-engine-root { position: relative; width: 100%; height: 100%; min-width: 0; min-height: 0; overflow: hidden; }
.lc-engine-stage #fc-engine-root > #app { display: none; }
.lc-engine-stage #fc-engine-root > .mon-panel { position: absolute; inset: 0; display: flex; flex-direction: column; padding: 0; }
.lc-engine-stage #fc-engine-root > .mon-panel > :not(.mon-stage) { display: none; }
.lc-engine-stage #fc-engine-root > .mon-panel > .mon-stage { flex: 1; min-height: 0; margin: 0; border: 0; }
.lc-engine-stage__status { position: absolute; top: 0; right: 0; padding: var(--lc-space-3); color: var(--lc-text-secondary); }
`;

function EngineViewport({ fps, onReady, onFirstSync, hidden, suppressed = false }: EngineStageProps) {
  const callbacks = useRef({ fps, onReady, onFirstSync });
  callbacks.current = { fps, onReady, onFirstSync };
  const active = useRef(true);
  const release = useRef<(() => void) | null>(null);
  const [status, setStatus] = useState('loading');
  const [revealed, setRevealed] = useState(false);
  const visible = status === 'ready' && revealed && !hidden && !suppressed;

  useEffect(() => {
    active.current = true;
    return () => {
      active.current = false;
      release.current?.();
      release.current = null;
    };
  }, []);

  const attach = useCallback(async (root: HTMLElement) => {
    if (!active.current || release.current) return;
    let currentRelease: (() => void) | null = null;
    try {
      const bridge = (window as Window & { FCBridge?: FCBridge }).FCBridge;
      if (!bridge) throw new Error('engine_bridge_missing');
      if (!root.querySelector('.mon-panel[data-mode="program"]')) throw new Error('engine_program_monitor_missing');
      const adapter = createEngineAdapter({ bridge, fps: () => callbacks.current.fps ?? 30, host: root });
      // 引擎自带整套编辑器 DOM，其中 timeline.js:218 建的 div 也带 .timeline-panel。
      // 宿主只借它的节目画面，其余 chrome 不可见、也不该参与选择器匹配，否则宿主与验收脚本的
      // document.querySelector('.timeline-panel') 会命中引擎那一份（同一文档里出现两个同名面板）。
      const stripEnginePanelNames = () => {
        for (const node of root.querySelectorAll('.timeline-panel')) node.classList.remove('timeline-panel');
      };
      stripEnginePanelNames();
      const panelNameObserver = new MutationObserver(stripEnginePanelNames);
      panelNameObserver.observe(root, { childList: true, subtree: true });
      // Classic scripts also register shortcuts; the host remains the only playback clock.
      const off = adapter.on('playstate', ({ playing }) => { if (playing) void adapter.pause(); });
      let disposed = false;
      release.current = () => {
        if (disposed) return;
        disposed = true;
        panelNameObserver.disconnect();
        try { off(); } finally { void adapter.dispose(); }
      };
      currentRelease = release.current;
      for (const result of [await adapter.ready(), await adapter.pause(), await adapter.requestRender()]) {
        if (!result.ok) throw new Error(result.error ?? 'engine_stage_not_ready');
      }
      if (!active.current || disposed || release.current !== currentRelease) return;
      let firstSyncComplete = false;
      const signalFirstSync = () => {
        if (!active.current || disposed || release.current !== currentRelease || firstSyncComplete) return;
        firstSyncComplete = true;
        setRevealed(true);
        callbacks.current.onFirstSync?.();
      };
      setStatus('ready');
      callbacks.current.onReady?.(adapter, signalFirstSync);
    } catch (reason) {
      currentRelease?.();
      if (release.current !== currentRelease) return;
      release.current = null;
      const message = reason instanceof Error ? reason.message : String(reason);
      if (active.current) {
        console.warn('[engine] 引擎画面加载失败，已回退宿主预览：', message);
        setStatus('error');
      }
    }
  }, []);

  return (
    <div
      className="lc-engine-stage"
      data-engine-stage={status}
      data-engine-revealed={revealed}
      style={{ display: hidden || suppressed || status === 'error' ? 'none' : undefined }}
      aria-hidden={!visible || undefined}
    >
      <style>{stageStyles}</style>
      <EngineHost
        className="lc-engine-stage__host"
        style={{ visibility: visible ? 'visible' : 'hidden' }}
        onReady={(root) => { void attach(root); }}
        onError={(reason) => { console.warn('[engine] 引擎宿主装载失败，已回退宿主预览：', reason.message); setStatus('error'); }}
      />
      {status === 'loading' ? (
        <div className="lc-engine-stage__status" role="status">正在加载引擎画面…</div>
      ) : null}
    </div>
  );
}

export function EngineStage({ mountSelector, ...props }: EngineStageProps) {
  const [mount, setMount] = useState<Element | null>(null);
  useLayoutEffect(() => {
    setMount(mountSelector ? document.querySelector(mountSelector) : null);
  }, [mountSelector, props.hidden]);

  if (!mountSelector) return <EngineViewport {...props} />;
  return mount ? createPortal(<EngineViewport {...props} />, mount) : null;
}
