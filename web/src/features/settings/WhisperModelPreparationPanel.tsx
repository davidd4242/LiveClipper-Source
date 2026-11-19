import { useCallback, useEffect, useRef, useState } from 'react';
import { Download, RefreshCw, XCircle } from 'lucide-react';
import { Button } from '../../design';
import {
  cancelAsrModelPrepare,
  getAsrModel,
  prepareAsrModel,
  type AsrModelResponse,
  type AsrPrepareSource,
} from '../../api/asrModels';
import './whisperModelPreparation.css';

const POLL_MS = 1500;
const HEX40 = /^[0-9a-f]{40}$/;
const SMALL_MODEL = 'small';

const SOURCE_OPTIONS: { value: AsrPrepareSource; label: string }[] = [
  { value: 'auto', label: '自动（官方失败时换镜像）' },
  { value: 'official', label: '官方 huggingface.co' },
  { value: 'mirror', label: '镜像 hf-mirror.com' },
];

const SOURCE_LABEL: Record<string, string> = {
  auto: '自动',
  official: '官方 huggingface.co',
  mirror: '镜像 hf-mirror.com',
};

const STATUS_TEXT: Record<string, string> = {
  idle: '未在准备（空闲，并不代表模型缺失）',
  preparing: '正在准备',
  completed: '已加载完成',
  failed: '准备失败',
  cancelled: '已取消',
};

const PHASE_TEXT: Record<string, string> = {
  starting: '启动中',
  cache: '检查缓存',
  downloading: '下载中',
  'switching-source': '切换来源',
  loading: '加载中',
  ready: '就绪',
};

function errorText(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function isAbort(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'AbortError';
}

/**
 * 转写模型（ASR / faster-whisper）准备面板。只展示服务器已保存的型号；
 * 挂载时只 GET，从不自动 POST；仅在任务 preparing 时串行轮询。
 */
export function WhisperModelPreparationPanel() {
  const [data, setData] = useState<AsrModelResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [pollPaused, setPollPaused] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [cancelRequested, setCancelRequested] = useState(false);
  const [source, setSource] = useState<AsrPrepareSource>('auto');
  const [revision, setRevision] = useState('');

  const controllerRef = useRef<AbortController | null>(null);
  const timerRef = useRef<number | null>(null);
  const seqRef = useRef(0);
  const mountedRef = useRef(true);

  const clearTimer = useCallback(() => {
    if (timerRef.current !== null) {
      window.clearTimeout(timerRef.current);
      timerRef.current = null;
    }
  }, []);

  const load = useCallback(async () => {
    clearTimer();
    controllerRef.current?.abort();
    const controller = new AbortController();
    controllerRef.current = controller;
    const seq = ++seqRef.current;
    setLoading(true);
    try {
      const next = await getAsrModel(controller.signal);
      if (seq !== seqRef.current) return;
      setData(next);
      setError(null);
      setPollPaused(false);
      if (next.state.status !== 'preparing') setCancelRequested(false);
    } catch (err) {
      if (isAbort(err) || seq !== seqRef.current) return;
      setError(errorText(err));
      setPollPaused(true);
    } finally {
      if (seq === seqRef.current) setLoading(false);
    }
  }, [clearTimer]);

  useEffect(() => {
    mountedRef.current = true;
    void load();
    return () => {
      mountedRef.current = false;
      seqRef.current += 1;
      clearTimer();
      controllerRef.current?.abort();
    };
  }, [load, clearTimer]);

  const status = data?.state.status ?? 'idle';
  const preparing = status === 'preparing';

  // 串行轮询：每次响应落地后才安排下一次；出错即停，等手动刷新。
  useEffect(() => {
    if (!preparing || pollPaused || loading) return;
    timerRef.current = window.setTimeout(() => {
      timerRef.current = null;
      void load();
    }, POLL_MS);
    return clearTimer;
  }, [preparing, pollPaused, loading, data, load, clearTimer]);

  const model = data?.model ?? '';
  const isSmall = model === SMALL_MODEL || model === 'Systran/faster-whisper-small';
  const trimmedRevision = revision.trim().toLowerCase();
  const revisionValid = trimmedRevision === '' || HEX40.test(trimmedRevision);
  const revisionMissing = !isSmall && trimmedRevision === '';
  const canPrepare = !!data && !preparing && !submitting && revisionValid && !revisionMissing;

  const onPrepare = async () => {
    if (!canPrepare) return;
    setSubmitting(true);
    setError(null);
    setCancelRequested(false);
    const controller = new AbortController();
    controllerRef.current?.abort();
    controllerRef.current = controller;
    const seq = ++seqRef.current;
    try {
      const accepted = await prepareAsrModel(
        trimmedRevision ? { source, revision: trimmedRevision } : { source },
        controller.signal,
      );
      if (seq !== seqRef.current) return;
      if (accepted.state) {
        setData((prev) => (prev ? { ...prev, state: accepted.state! } : prev));
      }
      setPollPaused(false);
      setSubmitting(false);
      void load();
    } catch (err) {
      if (isAbort(err) || seq !== seqRef.current) return;
      setError(errorText(err));
      setSubmitting(false);
      // 409 说明后端已有任务：刷新以恢复真实状态
      if (err instanceof Error && (err as { status?: number }).status === 409) void load();
    }
  };

  const onCancel = async () => {
    if (!preparing || cancelRequested) return;
    setError(null);
    try {
      await cancelAsrModelPrepare();
      if (!mountedRef.current) return;
      setCancelRequested(true);
      setPollPaused(false);
      void load();
    } catch (err) {
      if (!mountedRef.current) return;
      setError(errorText(err));
    }
  };

  const state = data?.state;
  const statusText = STATUS_TEXT[status] ?? `未知状态：${status}`;
  const phaseText = state?.phase ? (PHASE_TEXT[state.phase] ?? state.phase) : null;
  const prepareLabel =
    status === 'failed' ? '失败后重试' : status === 'cancelled' ? '重新准备' : '准备模型';

  return (
    <section className="asr-prep" aria-labelledby="asr-prep-title">
      <h3 id="asr-prep-title" className="asr-prep__title">
        转写模型（ASR）准备
      </h3>
      <p className="asr-prep__note">
        只用于本地语音转写（faster-whisper），与在线 AI / Ollama 的模型无关；这里不会下载或选择 AI
        模型，也不会修改任何设置。离开设置页不会取消后台任务，回来后会重新读取当前状态。
      </p>

      <dl className="asr-prep__kv">
        <div>
          <dt>服务器已保存的转写型号</dt>
          <dd>{data ? model || '—' : loading ? '读取中…' : '—'}</dd>
        </div>
        <div>
          <dt>当前状态</dt>
          <dd aria-live="polite" role="status">
            {data ? statusText : loading ? '读取中…' : '未能读取'}
            {phaseText ? `（阶段：${phaseText}）` : ''}
            {cancelRequested && preparing ? ' · 已请求取消，等待后端真实结果' : ''}
          </dd>
        </div>
        {state?.source ? (
          <div>
            <dt>实际来源</dt>
            <dd>{SOURCE_LABEL[state.source] ?? state.source}</dd>
          </div>
        ) : null}
        {state?.revision ? (
          <div>
            <dt>固定 revision</dt>
            <dd className="asr-prep__mono">{state.revision}</dd>
          </div>
        ) : null}
      </dl>
      {state?.detail ? <p className="asr-prep__detail">{state.detail}</p> : null}
      {status === 'completed' ? (
        <p className="asr-prep__note">
          模型已加载完成；这不代表文件哈希已逐一校验。
        </p>
      ) : null}
      {state?.modelPath ? (
        <details className="asr-prep__details">
          <summary>模型路径</summary>
          <p className="asr-prep__mono">{state.modelPath}</p>
        </details>
      ) : null}
      {preparing ? (
        <p className="asr-prep__note">后端不提供字节进度，仅显示阶段说明。</p>
      ) : null}

      <p className="asr-prep__note">
        提示：这里显示的是服务器已保存的型号。若刚在上方更换了转写模型，请先保存，再点「刷新」。
      </p>

      <div className="asr-prep__form">
        <label className="asr-prep__field">
          <span>下载来源</span>
          <select
            value={source}
            disabled={preparing || submitting}
            onChange={(event) => setSource(event.target.value as AsrPrepareSource)}
          >
            {SOURCE_OPTIONS.map((item) => (
              <option key={item.value} value={item.value}>
                {item.label}
              </option>
            ))}
          </select>
        </label>
        <p className="asr-prep__note">
          已缓存的文件命中后无需重新下载。取消后重新准备可能从头下载，未完成的临时文件可能保留。
        </p>

        <details className="asr-prep__details" open={!isSmall && !!data}>
          <summary>高级：指定 revision</summary>
          <label className="asr-prep__field">
            <span>
              {isSmall
                ? '留空则使用 small 已核对的 commit 536b0662…bce120'
                : `型号 ${model || '—'} 没有内置版本，需填写 40 位十六进制 commit`}
            </span>
            <input
              type="text"
              value={revision}
              spellCheck={false}
              autoComplete="off"
              placeholder="40 位小写十六进制 commit"
              disabled={preparing || submitting}
              aria-invalid={!revisionValid}
              onChange={(event) => setRevision(event.target.value)}
            />
          </label>
          {!revisionValid ? (
            <p className="asr-prep__error">revision 必须是 40 位十六进制字符。</p>
          ) : null}
          {revisionMissing && data ? (
            <p className="asr-prep__note">填写 revision 后才能准备该型号。</p>
          ) : null}
        </details>
      </div>

      <div className="asr-prep__actions">
        <Button
          size="sm"
          variant="solid"
          leading={<Download size={14} />}
          disabled={!canPrepare}
          onClick={() => void onPrepare()}
        >
          {submitting ? '提交中…' : prepareLabel}
        </Button>
        <Button
          size="sm"
          leading={<XCircle size={14} />}
          disabled={!preparing || cancelRequested}
          onClick={() => void onCancel()}
        >
          {cancelRequested ? '已请求取消' : '取消准备'}
        </Button>
        <Button
          size="sm"
          variant="ghost"
          leading={<RefreshCw size={14} />}
          disabled={loading || submitting}
          onClick={() => void load()}
        >
          刷新
        </Button>
      </div>

      <p className="asr-prep__error" role="alert">
        {error ? `${error}${pollPaused && preparing ? '（已暂停自动刷新，请手动刷新）' : ''}` : ''}
      </p>
    </section>
  );
}
