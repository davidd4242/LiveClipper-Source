import { useEffect, useState } from 'react';

/**
 * 为「挂载后才播动画」的抽屉/面板生成过渡状态。
 *
 * 问题：条件渲染会在移除 is-open 的同一帧卸载节点，CSS 过渡来不及播放。
 * 这里的约定是——
 * - 打开：先 mounted=true，等两帧（首帧提交收起态布局）再 shown=true，
 *   这样 width / transform 过渡有初始值；
 * - 收起：先 shown=false 触发过渡，durationMs 后再 mounted=false。
 *
 * 返回的 `mounted` 决定是否渲染，`shown` 传给组件的 open / is-open 状态。
 * 被包住的组件必须让「收起态」仍可渲染（宽度 0、移出视口皆可），
 * 不能自己 `if (!open) return null`，否则收起动画同样会消失。
 */
export function useSlideMount(open: boolean, durationMs = 240) {
  const [mounted, setMounted] = useState(open);
  const [shown, setShown] = useState(open);

  useEffect(() => {
    if (open) {
      setMounted(true);
      // 双 rAF：等收起态那次提交完成后再切 is-open，过渡才有初始值。
      let inner = 0;
      const outer = window.requestAnimationFrame(() => {
        inner = window.requestAnimationFrame(() => setShown(true));
      });
      return () => {
        window.cancelAnimationFrame(outer);
        if (inner) window.cancelAnimationFrame(inner);
      };
    }

    setShown(false);
    const timer = window.setTimeout(() => setMounted(false), durationMs);
    return () => window.clearTimeout(timer);
  }, [open, durationMs]);

  return { mounted, shown };
}
