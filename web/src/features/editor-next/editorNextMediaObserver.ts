import type { FullEngineBridge } from '../../engine/fullEngineSession';

export type EditorNextMediaIssue = { kind: 'playback' | 'seek' | 'decode'; message: string; sourceIds?: string[] };
export type EditorNextMediaRuntime = {
  Media?: { list: () => { id?: string; el?: HTMLMediaElement | null; failed?: string | null; audioFailed?: string | null }[] };
  FXA?: { previewStatus: () => string };
  FCBridge?: Pick<FullEngineBridge, 'retryMedia'>;
};
const playbackMessage = (cause: unknown) => {
  const name = (cause as { name?: string } | null)?.name;
  return name === 'NotAllowedError'
    ? '浏览器阻止了播放（NotAllowedError）。请再次点击编辑器的播放按钮，允许音频启动。'
    : `播放未成功。请再次点击播放；仍失败时请重新载入素材。${cause instanceof Error ? ` ${cause.message}` : ''}`;
};

// Observe only this engine's media instances. Global/native prototypes and engine source stay unchanged.
export function observeEditorNextMedia(options: {
  runtime: EditorNextMediaRuntime; onIssue: (issue: EditorNextMediaIssue) => void;
  intervalMs?: number; seekTimeoutMs?: number; onAudioRecovered?: (issue: EditorNextMediaIssue) => void;
}) {
  let disposed = false;
  let lastAudioStatus = '';
  let audioIssue: EditorNextMediaIssue | undefined;
  const owned = new Map<HTMLMediaElement, () => void>();
  const failures = new Set<string>();
  const report = (kind: EditorNextMediaIssue['kind'], message: string, sourceId?: string) => {
    if (!disposed) options.onIssue({ kind, message, ...(sourceId ? { sourceIds: [sourceId] } : {}) });
  };
  function observe(element: HTMLMediaElement, sourceId?: string) {
    const report = (kind: EditorNextMediaIssue['kind'], message: string) => {
      if (!disposed) options.onIssue({ kind, message, ...(sourceId ? { sourceIds: [sourceId] } : {}) });
    };
    const cleanups: (() => void)[] = [];
    let seekTimer: ReturnType<typeof setTimeout> | undefined;
    const clearSeek = () => { clearTimeout(seekTimer); seekTimer = undefined; };
    const onError = () => report('decode', '媒体解码或定位失败。请检查素材是否可播放，再重新载入。');
    element.addEventListener('error', onError);
    element.addEventListener('seeked', clearSeek);
    cleanups.push(() => { clearSeek(); element.removeEventListener('error', onError); element.removeEventListener('seeked', clearSeek); });
    const ownPlay = Object.getOwnPropertyDescriptor(element, 'play');
    const originalPlay = element.play;
    const play = function (this: HTMLMediaElement): Promise<void> {
      try {
        const result = originalPlay.call(this);
        // Attach rejection observation even when the caller deliberately ignores play().
        void result.catch(cause => report('playback', playbackMessage(cause)));
        return result;
      } catch (cause) { report('playback', playbackMessage(cause)); throw cause; }
    };
    try {
      Object.defineProperty(element, 'play', { configurable: true, writable: true, value: play });
      cleanups.push(() => {
        if (element.play !== play) return;
        if (ownPlay) Object.defineProperty(element, 'play', ownPlay); else Reflect.deleteProperty(element, 'play');
      });
    } catch { report('playback', '浏览器未允许监测播放拒绝；若无声音，请再次点击播放。'); }
    const ownTime = Object.getOwnPropertyDescriptor(element, 'currentTime');
    let owner: object | null = element;
    let time: PropertyDescriptor | undefined;
    while (owner && !time) { time = Object.getOwnPropertyDescriptor(owner, 'currentTime'); owner = Object.getPrototypeOf(owner); }
    if (time?.get && time.set) {
      const descriptor = time;
      const set = function (this: HTMLMediaElement, value: number) {
        clearSeek();
        try { descriptor.set!.call(this, value); }
        catch (cause) { report('seek', '浏览器拒绝了媒体定位。请重试拖动播放头，或重新载入素材。'); throw cause; }
        seekTimer = setTimeout(() => {
          if (this.seeking || this.readyState < 2 || Math.abs(this.currentTime - value) > .25)
            report('seek', '媒体定位尚未完成，预览可能停留在旧画面。请重试拖动播放头，或重新载入素材。');
        }, options.seekTimeoutMs ?? 6000);
      };
      try {
        Object.defineProperty(element, 'currentTime', { configurable: true, get: descriptor.get, set });
        cleanups.push(() => {
          if (Object.getOwnPropertyDescriptor(element, 'currentTime')?.set !== set) return;
          if (ownTime) Object.defineProperty(element, 'currentTime', ownTime); else Reflect.deleteProperty(element, 'currentTime');
        });
      } catch { report('seek', '浏览器未允许监测定位失败；若预览不更新，请重新载入素材。'); }
    }
    return () => { for (const cleanup of cleanups.reverse()) cleanup(); };
  }
  const scan = () => {
    if (disposed) return;
    const assets = options.runtime.Media?.list() ?? [];
    const live = new Set(assets.flatMap(asset => asset.el ? [asset.el] : []));
    for (const [element, cleanup] of owned) if (!live.has(element)) { cleanup(); owned.delete(element); }
    for (const element of live) if (!owned.has(element)) owned.set(element, observe(element, assets.find(asset => asset.el === element)?.id));
    for (const asset of assets) for (const failure of [asset.failed, asset.audioFailed]) {
      const key = `${asset.id ?? ''}:${failure}`;
      if (failure && !failures.has(key)) { failures.add(key); report('decode', `媒体载入提示：${failure}`, asset.id); }
    }
    // The PCM pipeline catches resume() rejection itself; read its public status instead of patching AudioContext.
    const status = options.runtime.FXA?.previewStatus() ?? '';
    if (status !== lastAudioStatus && /音频未启动|音频输出不可用|Web Audio 不可用/.test(status)) {
      audioIssue = { kind: 'playback', message: `${status}。浏览器可能阻止了音频播放，请再次点击编辑器的播放按钮。` };
      options.onIssue(audioIssue);
    } else if (status === '引擎 PCM 预览' && audioIssue) {
      options.onAudioRecovered?.(audioIssue);
      audioIssue = undefined;
    }
    lastAudioStatus = status;
  };
  scan();
  const timer = setInterval(scan, options.intervalMs ?? 250);
  return () => { disposed = true; clearInterval(timer); for (const cleanup of owned.values()) cleanup(); owned.clear(); };
}
