import { Component, type ErrorInfo, type ReactNode } from 'react';
import { Button, EmptyState } from '../design';

interface ErrorBoundaryProps {
  children: ReactNode;
  /** 每次捕获到真实渲染错误时回调（App 决定是否上报桌面壳首屏失败）；可省略。 */
  onError?: (error: Error) => void;
}

interface ErrorBoundaryState {
  hasError: boolean;
}

/**
 * 页面级错误边界：兜住懒加载 chunk 失败与页面渲染异常。
 *
 * - 错误页只展示面向用户的说明，不渲染堆栈（细节进 console.info 供排查）；
 * - 「重试加载」重置边界并整页 reload（重新拉取 chunk；网络恢复后即可进入）；
 * - 「回到工作台」直接导航回 `/`，用户不会被卡死在错误页；
 * - 由 App.tsx 以 `key={pathname}` 挂载，路由切换时自动重置。
 */
export class ErrorBoundary extends Component<ErrorBoundaryProps, ErrorBoundaryState> {
  state: ErrorBoundaryState = { hasError: false };

  static getDerivedStateFromError(): ErrorBoundaryState {
    return { hasError: true };
  }

  componentDidCatch(error: Error, info: ErrorInfo): void {
    // 面向开发者的细节只进控制台，不进入 UI。
    console.info('[LiveClipper] 页面渲染/加载失败，已由 ErrorBoundary 兜底', error, info.componentStack);
    this.props.onError?.(error);
  }

  private handleRetry = (): void => {
    this.setState({ hasError: false });
    window.location.reload();
  };

  private handleHome = (): void => {
    window.location.assign('/');
  };

  render(): ReactNode {
    if (!this.state.hasError) return this.props.children;

    return (
      <div className="lc-error-boundary" role="alert">
        <EmptyState
          title="这个页面没加载出来"
          description="页面资源可能没有加载成功，或这个页面出现了一点异常。可以重试一次，也可以先回到工作台继续操作。"
          action={
            <div className="lc-error-boundary__actions">
              <Button variant="solid" onClick={this.handleRetry}>
                重试加载
              </Button>
              <Button variant="outline" onClick={this.handleHome}>
                回到工作台
              </Button>
            </div>
          }
        />
        <p className="lc-error-boundary__hint">如果多次重试仍失败，请检查网络后刷新页面。</p>
      </div>
    );
  }
}
