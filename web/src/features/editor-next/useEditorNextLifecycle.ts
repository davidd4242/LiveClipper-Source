import { useEffect, useRef, useState } from 'react';
import { createEditorNextExportLifecycle, EDITOR_NEXT_DRAFT_NOTICE } from './editorNextLifecycle';
import { observeEditorNextMedia } from './editorNextMediaObserver';
import type { EditorNextMediaIssue, EditorNextMediaRuntime } from './editorNextMediaObserver';

export function useEditorNextLifecycle(options: {
  identity: object | null; ready: boolean; exportOpen: boolean; openExport: () => Promise<boolean>;
  mediaWarnings?: string[]; onMediaRecovered?: (sourceIds: string[]) => void;
}) {
  const current = useRef(options);
  current.current = options;
  const controller = useRef<ReturnType<typeof createEditorNextExportLifecycle> | null>(null);
  const [mediaIssue, setMediaIssue] = useState<EditorNextMediaIssue | null>(null);
  const [mediaRetrying, setMediaRetrying] = useState(false);
  const mediaOwner = useRef<{ active: boolean; busy: boolean } | null>(null);
  useEffect(() => {
    setMediaIssue(null);
    setMediaRetrying(false);
    if (!options.identity || !options.ready) return;
    const owner = { active: true, busy: false };
    mediaOwner.current = owner;
    const lifecycle = createEditorNextExportLifecycle({ document, open: () => current.current.openExport() });
    controller.current = lifecycle;
    const shortcut = (event: KeyboardEvent) => lifecycle.shortcut(event);
    const focus = (event: FocusEvent) => lifecycle.rememberFocus(event.target);
    window.addEventListener('keydown', shortcut, true);
    window.addEventListener('focusin', focus);
    const stopMedia = observeEditorNextMedia({ runtime: window as unknown as EditorNextMediaRuntime, onIssue: setMediaIssue,
      onAudioRecovered: issue => setMediaIssue(current => current === issue ? null : current) });
    return () => {
      window.removeEventListener('keydown', shortcut, true);
      window.removeEventListener('focusin', focus);
      owner.active = false;
      if (mediaOwner.current === owner) mediaOwner.current = null;
      stopMedia(); lifecycle.dispose();
      if (controller.current === lifecycle) controller.current = null;
    };
  }, [options.identity, options.ready]);
  useEffect(() => { controller.current?.syncOpen(options.exportOpen); }, [options.exportOpen]);
  useEffect(() => {
    if (!options.identity || !options.ready) return;
    const sourceIds = [...new Set((options.mediaWarnings ?? []).flatMap(code => {
      const match = /^media_unbound:(.*):[^:]+$/.exec(code);
      return match?.[1] ? [match[1]] : [];
    }))];
    if (sourceIds.length) setMediaIssue({ kind: 'decode', message: '媒体未能载入或已离线。请恢复网络后重试加载媒体。', sourceIds });
  }, [options.identity, options.ready, options.mediaWarnings]);
  const retryMedia = async () => {
    const owner = mediaOwner.current;
    if (!owner?.active || owner.busy) return false;
    owner.busy = true;
    setMediaRetrying(true);
    try {
      const bridge = (window as unknown as EditorNextMediaRuntime).FCBridge;
      if (!bridge?.retryMedia) throw new Error('当前引擎未提供媒体重试入口。');
      const result = await bridge.retryMedia(mediaIssue?.sourceIds);
      if (!owner.active || mediaOwner.current !== owner) return false;
      if (!result.ok) throw new Error(result.error || '媒体仍未能载入。');
      const missing = result.missingSources ?? [];
      const recovered = (result.boundSources ?? []).filter(id => !missing.includes(id));
      if (recovered.length) current.current.onMediaRecovered?.(recovered);
      setMediaIssue(missing.length ? { kind: 'decode', message: '部分媒体仍未能载入。请检查网络后再次重试加载媒体。', sourceIds: missing } : null);
      return missing.length === 0;
    } catch (cause) {
      if (owner.active && mediaOwner.current === owner) setMediaIssue({ kind: 'decode', sourceIds: mediaIssue?.sourceIds,
        message: `媒体重试未成功，请再次重试加载媒体。${cause instanceof Error ? ` ${cause.message}` : ''}` });
      return false;
    } finally {
      owner.busy = false;
      if (owner.active && mediaOwner.current === owner) setMediaRetrying(false);
    }
  };
  return {
    draftNotice: EDITOR_NEXT_DRAFT_NOTICE,
    mediaIssue, mediaRetrying, retryMedia,
    dismissMediaIssue: () => setMediaIssue(null),
    openExport: (trigger?: HTMLElement | null) => controller.current?.open(trigger) ?? Promise.resolve(false),
  };
}
