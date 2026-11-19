import { useEffect, useRef, useState } from 'react';
import '../../design/components/IconRail.css';
import { PAGE_ORDER, PAGES } from '../../app/AppShell';
import type { MouseEvent } from 'react';
import { editorNextClipId } from './editorNextDocument';
import { navigateApp, registerAppLeaveGuard } from '../../app/appNavigation';

export interface FilmCraftAppNavProps {
  beforeNavigate: () => Promise<string | null>;
}

export function FilmCraftAppNav({ beforeNavigate }: FilmCraftAppNavProps) {
  const pending = useRef(false);
  const mounted = useRef(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');

  useEffect(() => {
    mounted.current = true;
    return () => { mounted.current = false; };
  }, []);

  useEffect(() => registerAppLeaveGuard(async () => {
    if (pending.current) return '正在确认保存，请稍候。';
    pending.current = true;
    setBusy(true);
    setError('');
    try {
      const reason = await beforeNavigate();
      if (!mounted.current) return '当前编辑器已离开。';
      if (reason) setError(reason);
      return reason;
    } catch (cause) {
      const reason = `离开前保存失败：${cause instanceof Error ? cause.message : String(cause)}。当前工程已保留，请重试保存。`;
      if (mounted.current) setError(reason);
      return reason;
    } finally {
      pending.current = false;
      if (mounted.current) setBusy(false);
    }
  }), [beforeNavigate]);

  const navigate = (event: MouseEvent<HTMLAnchorElement>, current: boolean) => {
    if (event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey ||
      event.currentTarget.hasAttribute('download') || (event.currentTarget.target && event.currentTarget.target !== '_self')) return;
    event.preventDefault();
    if (!current) void navigateApp(event.currentTarget.href);
  };

  return (
    <aside className="fc-app-nav" onKeyDown={event => {
      event.stopPropagation();
      if (event.key === 'Escape' && !pending.current) {
        setError('');
        if (event.target instanceof HTMLElement) event.target.blur();
      }
    }}>
      <nav className="lc-icon-rail" aria-label="LiveClipper 应用导航" aria-busy={busy}>
        <div className="lc-icon-rail__brand" aria-hidden="true"><i className="lc-icon-rail__brand-dot" /></div>
        {PAGE_ORDER.map(id => {
          const current = id === 'editor';
          const label = id === 'music' ? '音乐工作室' : PAGES[id].label;
          // 往返素材页时带上当前工程，素材页据此回选而不是默认首个素材。
          const openClip = editorNextClipId(window.location.search);
          const href = current ? `${window.location.pathname}${window.location.search}${window.location.hash}`
            : id === 'studio' && openClip ? `${PAGES[id].href}?clip=${encodeURIComponent(openClip)}` : PAGES[id].href;
          return <a key={id} href={href} className={`lc-icon-rail__item${current ? ' is-active' : ''}`}
            aria-label={label} title={`${label}${current ? ' · 当前' : ' · 离开前会先保存工程'}`} data-tooltip={label}
            aria-current={current ? 'page' : undefined} aria-disabled={busy && !current ? true : undefined}
            onClick={event => navigate(event, current)}>
            <span className="lc-icon-rail__glyph" aria-hidden="true">{PAGES[id].icon}</span>
          </a>;
        })}
        <span className="lc-icon-rail__spacer" aria-hidden="true" />
      </nav>
      <p className="fc-app-nav__hint" role="status" data-busy={busy}>{busy ? '正在确认保存…' : '离开剪辑前会先保存工程'}</p>
      {error && <p className="fc-app-nav__error" role="alert">{error}</p>}
    </aside>
  );
}

export default FilmCraftAppNav;
