import { useEffect, useRef, useState } from 'react';
import { editorNextTechnicalText } from './editorNextNotices';

export function TechnicalDetails({ messages }: { messages: readonly string[] }) {
  const raw = editorNextTechnicalText(messages);
  const field = useRef<HTMLTextAreaElement>(null);
  const request = useRef(0);
  const [feedback, setFeedback] = useState('');
  useEffect(() => { setFeedback(''); return () => { request.current++; }; }, [raw]);
  if (!raw) return null;
  const copy = async () => {
    const current = ++request.current;
    try {
      if (!navigator.clipboard?.writeText) throw new Error('clipboard unavailable');
      await navigator.clipboard.writeText(raw);
      if (request.current === current) setFeedback('已复制技术详情。');
    } catch {
      if (request.current !== current) return;
      field.current?.focus(); field.current?.select();
      setFeedback('自动复制未成功，已选中原始详情，请按 Ctrl+C 或 Cmd+C 复制。');
    }
  };
  return <details className="editor-next-technical">
    <summary>技术详情</summary>
    <label>原始诊断信息<textarea ref={field} readOnly value={raw} rows={5} spellCheck={false}
      aria-label="原始诊断信息，可选择并复制" /></label>
    <button type="button" onClick={() => { void copy(); }}>复制技术详情</button>
    <p role="status" aria-live="polite">{feedback}</p>
  </details>;
}
