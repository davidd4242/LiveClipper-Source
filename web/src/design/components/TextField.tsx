import { useEffect, useId } from 'react';
import type { InputHTMLAttributes, ReactNode } from 'react';
import { cn } from '../cn';
import './field.css';
import './TextField.css';

export interface TextFieldProps extends Omit<InputHTMLAttributes<HTMLInputElement>, 'id'> {
  label?: ReactNode;
  /** 提示文字；有 error 时被 error 替换。 */
  hint?: ReactNode;
  error?: ReactNode;
  /** 固定 id（用于外部 label/联动）；默认使用 useId。 */
  fieldId?: string;
  /** 输入框内前置图标节点。 */
  leading?: ReactNode;
}

export function TextField({
  label,
  hint,
  error,
  fieldId,
  leading,
  className,
  ...rest
}: TextFieldProps) {
  const autoId = useId();
  const id = fieldId ?? autoId;
  const message = error ?? hint;
  const messageId = message ? `${id}-message` : undefined;
  // label 契约：必须有可见 label，或显式 aria-label；缺失时仅开发环境提示。
  const ariaLabel = rest['aria-label'];

  useEffect(() => {
    if (import.meta.env.DEV && !label && !ariaLabel) {
      console.warn(`[lc TextField#${id}] 缺少可访问名称：请传 label，或传 aria-label。`);
    }
  }, [ariaLabel, id, label]);

  return (
    <div className={cn('lc-field', Boolean(error) && 'lc-field--error', className)}>
      {label ? (
        <label className="lc-field__label" htmlFor={id}>
          {label}
        </label>
      ) : null}
      <div className="lc-field__control">
        {leading ? (
          <span className="lc-field__leading" aria-hidden="true">
            {leading}
          </span>
        ) : null}
        <input
          {...rest}
          id={id}
          className={cn('lc-field__input', Boolean(leading) && 'lc-field__input--leading')}
          aria-invalid={error ? true : undefined}
          aria-describedby={messageId}
        />
      </div>
      {message ? (
        <div
          className={cn('lc-field__message', Boolean(error) && 'lc-field__message--error')}
          id={messageId}
        >
          {message}
        </div>
      ) : null}
    </div>
  );
}
