import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import './styles/tokens.css';
import './styles/app.css';
import './styles/live.css';
import { App, isAppShellRoute } from './App';
import { initTheme } from './design';

// 外壳页面（当前是工作台 `/`）跟随浅/深主题；旧页面（编辑器/直播/设置…）
// 继续使用深色原生控件。设计层主题会把 color-scheme 跟随 data-theme，
// 因此这里用内联值覆盖回深色，避免迁移期间旧页面的下拉框/滚动条翻白。
initTheme();
if (!isAppShellRoute(window.location.pathname, window.location.search)) {
  document.documentElement.style.colorScheme = 'dark';
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
