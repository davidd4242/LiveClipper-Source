import { useEffect, useId } from 'react';
import type { ReactNode, SelectHTMLAttributes } from 'react';
import { cn } from '../cn';
import './field.css';
import './SelectField.css';

export interface SelectFieldProps extends Omit<SelectHTMLAttributes<HTMLSelectElement>, 'id'> {
  label?: ReactNode;
  hint?: ReactNode;
  error?: ReactNode;
  /** 固定 id（用于外部 label/联动）；默认使用 useId。 */
  fieldId?: string;
  /** `<option>` 列表。 */
  children: ReactNode;
}

export function SelectField({
  label,
  hint,
  error,
  fieldId,
  className,
  children,
  ...rest
}: SelectFieldProps) {
  const autoId = useId();
  const id = fieldId ?? autoId;
  const message = error ?? hint;
  const messageId = message ? `${id}-message` : undefined;
  // label 契约：必须有可见 label，或显式 aria-label；缺失时仅开发环境提示。
  const ariaLabel = rest['aria-label'];

  useEffect(() => {
    if (import.meta.env.DEV && !label && !ariaLabel) {
      console.warn(`[lc SelectField#${id}] 缺少可访问名称：请传 label，或传 aria-label。`);
    }
  }, [ariaLabel, id, label]);

  return (
    <div
      className={cn('lc-field', 'lc-select-field', Boolean(error) && 'lc-field--error', className)}
    >
      {label ? (
        <label className="lc-field__label" htmlFor={id}>
          {label}
        </label>
      ) : null}
      <div className="lc-field__control">
        <select
          {...rest}
          id={id}
          className="lc-select-field__select"
          aria-invalid={error ? true : undefined}
          aria-describedby={messageId}
        >
          {children}
        </select>
        <span className="lc-select-field__chevron" aria-hidden="true">
          ▾
        </span>
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
