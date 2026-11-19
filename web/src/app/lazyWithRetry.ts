import { lazy, type ComponentType, type LazyExoticComponent } from 'react';

type LazyImporter<T extends ComponentType<unknown>> = () => Promise<{ default: T }>;

const lazyCache = new WeakMap<LazyImporter<ComponentType<unknown>>, LazyExoticComponent<ComponentType<unknown>>>();

const RETRY_DELAY_MS = 300;

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => {
    setTimeout(resolve, ms);
  });
}

/**
 * 带重试的 `React.lazy`。
 *
 * - `import()` 失败（chunk 404、网络抖动、发版后旧 chunk 被清）时重试 `retries` 次（默认 1 次）；
 * - 重试仍失败则抛出原始错误，交给最近的 ErrorBoundary 展示错误页；
 * - 同一个 importer 只会创建一个 lazy 组件并缓存，页面切换/边界重挂载时不会重复请求已加载的 chunk。
 *
 * 注意：import 需在模块顶层定义（同一个函数引用），缓存才能命中。
 */
export function lazyWithRetry<T extends ComponentType<unknown>>(
  importer: LazyImporter<T>,
  retries = 1,
): LazyExoticComponent<T> {
  const cached = lazyCache.get(importer as LazyImporter<ComponentType<unknown>>);
  if (cached) return cached as LazyExoticComponent<T>;

  const component = lazy(async () => {
    let lastError: unknown;
    for (let attempt = 0; attempt <= retries; attempt += 1) {
      try {
        return await importer();
      } catch (error) {
        lastError = error;
        if (attempt < retries) await sleep(RETRY_DELAY_MS);
      }
    }
    throw lastError instanceof Error ? lastError : new Error(String(lastError));
  });

  lazyCache.set(importer as LazyImporter<ComponentType<unknown>>, component as LazyExoticComponent<ComponentType<unknown>>);
  return component;
}
