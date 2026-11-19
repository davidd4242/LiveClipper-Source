import { useEffect, useState, type ReactNode } from 'react';

/** 只读字段行，沿用主窗口的 window-field 样式。 */
export function Field({ label, value }: { label: string; value: string }) {
  return (
    <div className="window-field">
      <span>{label}</span>
      <strong>{value}</strong>
    </div>
  );
}

/** 通用滑杆，值会四舍五入到两位小数显示。action 渲染在数值右侧（如关键帧按钮）。 */
export function Slider({
  label,
  value,
  min,
  max,
  step,
  suffix,
  onChange,
  disabled,
  action,
}: {
  label: string;
  value: number;
  min: number;
  max: number;
  step: number;
  suffix: string;
  onChange: (value: number) => void;
  disabled?: boolean;
  action?: ReactNode;
}) {
  return (
    <label className="window-slider">
      <span>{label}</span>
      <input
        type="range"
        min={min}
        max={max}
        step={step}
        value={value}
        disabled={disabled}
        onChange={(event) => onChange(Number(event.target.value))}
      />
      <b>
        {Math.round(value * 100) / 100}
        {suffix}
      </b>
      {action}
    </label>
  );
}

/** 面板内的说明 / 警告条。 */
export function Note({ children, tone = 'info' }: { children: ReactNode; tone?: 'info' | 'warning' }) {
  return <p className={`wsx-note${tone === 'warning' ? ' wsx-note--warning' : ''}`}>{children}</p>;
}

/**
 * 二级窗口里的可折叠分组。
 *
 * 默认展开；折叠态按「组标题」记忆到 localStorage（跨窗口/刷新保留），
 * 只影响展示，不改变任何参数状态。提示文案放标题右侧，折叠时仍可见。
 */
export function Section({ title, hint, children }: { title: string; hint?: string; children: ReactNode }) {
  const storageKey = `lc-studio-section:${title}`;
  const [open, setOpen] = useState(() => {
    try {
      return localStorage.getItem(storageKey) !== '1';
    } catch {
      return true;
    }
  });
  useEffect(() => {
    try {
      localStorage.setItem(storageKey, open ? '0' : '1');
    } catch {
      /* 隐私模式等场景不影响面板使用 */
    }
  }, [open, storageKey]);
  return (
    <details
      className="wsx-section"
      data-section={title}
      open={open}
      onToggle={(event) => setOpen(event.currentTarget.open)}
    >
      <summary className="wsx-section__head">
        <span className="wsx-section__title">{title}</span>
        {hint ? <span className="wsx-section__hint">{hint}</span> : null}
        <span className="wsx-section__chevron" aria-hidden="true">▾</span>
      </summary>
      <div className="wsx-section__body">{children}</div>
    </details>
  );
}
