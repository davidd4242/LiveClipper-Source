import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  CheckCircle2,
  Cpu,
  KeyRound,
  ListFilter,
  LoaderCircle,
  RefreshCw,
  Save,
  Server,
} from 'lucide-react';
import {
  Badge,
  Button,
  Card,
  EmptyState,
  IconButton,
  Panel,
  SegmentedTabs,
  SelectField,
  StatusDot,
  TextField,
  Toast,
  type SegmentedTabItem,
} from '../../design';
import {
  COMPATIBLE_PRESETS,
  SettingsPatchError,
  WHISPER_MODELS,
  getAiSettings,
  getSettingsEnv,
  getSettingsOverview,
  patchSettings,
  saveAiSettings,
  testAiConnection,
  type AiProvider,
  type AiSettings,
  type AiSettingsPatch,
  type AiTestResult,
  type SettingsEnvValues,
  type SettingsOverview,
} from '../../api/settings';
import { WhisperModelPreparationPanel } from './WhisperModelPreparationPanel';
import { MusicGenerationSettingsPanel } from './MusicGenerationSettingsPanel';
import { OllamaManagementPanel } from './OllamaManagementPanel';
import { OllamaModelDownloadPanel } from './OllamaModelDownloadPanel';
import { ReleaseUpdatePanel } from './ReleaseUpdatePanel';
import './settings.css';

interface Banner {
  tone: 'ok' | 'error' | 'info';
  text: string;
}

const PROVIDER_TABS: readonly SegmentedTabItem<AiProvider>[] = [
  { value: 'ollama', label: '本地 Ollama' },
  { value: 'compatible', label: '在线兼容 API' },
];

/** A9 P0-4：测试连接错误分类的展示文案。 */
const TEST_CODE_LABEL: Record<string, string> = {
  ok: '连接成功',
  model_not_found: '连接成功，但模型不存在',
  network: '网络不可达',
  timeout: '连接超时',
  invalid: '配置无效',
  unknown: '连接失败',
};

function errorMessage(cause: unknown): string {
  return cause instanceof Error ? cause.message : String(cause);
}

interface CliPayload {
  ok?: boolean;
  code?: string;
  message?: string;
  project?: string;
  output?: string;
  bytes?: number;
  stdout?: string;
  stderr?: string;
}

/** 读取 CLI 接口响应：先取文本再解析；非 JSON（如 403/502 的纯文本或空正文）转成可读错误。 */
async function readCliPayload(response: Response): Promise<CliPayload> {
  const raw = await response.text();
  try {
    return JSON.parse(raw) as CliPayload;
  } catch {
    const body = raw.trim().slice(0, 200);
    throw new Error(body ? `HTTP ${response.status}: ${body}` : `HTTP ${response.status}（无响应正文）`);
  }
}

/** P2a / P2b 共用的失败文案优先级：stderr 末 600 字 → message → code → HTTP 状态。 */
function cliFailureText(payload: CliPayload, status: number): string {
  const detail = payload.stderr?.trim();
  if (detail) return detail.slice(-600);
  return payload.message?.trim() || payload.code || `HTTP ${status}`;
}

function formatClock(iso: string | null): string {
  if (!iso) return '—';
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? '—' : date.toLocaleTimeString('zh-CN', { hour12: false });
}

function isCompatiblePresetEndpoint(endpoint: string): boolean {
  return COMPATIBLE_PRESETS.some((preset) => preset.endpoint === endpoint);
}

/**
 * 设置页（/settings）：AppShell 内第三块换壳页面。
 *
 * - AI 连接（本地 Ollama / 在线 OpenAI 兼容）沿用 task-10 的既有后端契约：
 *   GET /api/settings/ai、POST /api/settings/ai、POST /api/settings/ai/test。
 * - 本地环境（存储目录 / FFmpeg / Python / 转写模型）与自动切片在「自动切片与本地环境」卡片内
 *   通过 PATCH /api/settings 编辑；导出目录 localOutputRoot 只读展示。
 * - 密钥只以 hasApiKey 暴露，不回显明文；保存时留空表示保持已有密文。
 */
export function SettingsPage() {
  const [overview, setOverview] = useState<SettingsOverview | null>(null);
  const [ai, setAi] = useState<AiSettings | null>(null);
  const [appVersion, setAppVersion] = useState<string | null>(null);
  const [provider, setProvider] = useState<AiProvider>('ollama');
  const [endpoint, setEndpoint] = useState('');
  const [model, setModel] = useState('');
  const [apiKey, setApiKey] = useState('');
  const [clearApiKey, setClearApiKey] = useState(false);
  const [models, setModels] = useState<string[]>([]);
  const [testResult, setTestResult] = useState<AiTestResult | null>(null);
  const [testing, setTesting] = useState(false);
  const [saving, setSaving] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const [loading, setLoading] = useState(true);
  const [banner, setBanner] = useState<Banner | null>(null);
  const [lastUpdated, setLastUpdated] = useState<string | null>(null);
  const [envDraft, setEnvDraft] = useState<SettingsEnvValues | null>(null);
  const [envInitial, setEnvInitial] = useState<SettingsEnvValues | null>(null);
  const [envSaving, setEnvSaving] = useState(false);
  const [envErrors, setEnvErrors] = useState<Record<string, string>>({});
  const [envRestart, setEnvRestart] = useState(false);
  const [cliProject, setCliProject] = useState('');
  const [cliOutput, setCliOutput] = useState('');
  const [cliStartText, setCliStartText] = useState('0');
  const [cliEndText, setCliEndText] = useState('3');
  const [cliTouched, setCliTouched] = useState(false);
  const [cutsTouched, setCutsTouched] = useState(false);
  const [cliRunning, setCliRunning] = useState(false);
  const [cliResult, setCliResult] = useState<{ ok: boolean; message?: string; output?: string; bytes?: number; cancelled?: boolean } | null>(null);
  const cliAbortRef = useRef<AbortController | null>(null);
  const cliCancelledRef = useRef(false);
  const [cutsMedia, setCutsMedia] = useState('');
  const [cutsProject, setCutsProject] = useState('');
  const [cutsOutput, setCutsOutput] = useState('');
  const [cutsRunning, setCutsRunning] = useState(false);
  const [cutsResult, setCutsResult] = useState<{ ok: boolean; message?: string; project?: string; output?: string; cancelled?: boolean } | null>(null);
  const cutsAbortRef = useRef<AbortController | null>(null);
  const cutsCancelledRef = useRef(false);
  const mountedRef = useRef(true);
  const [localModels, setLocalModels] = useState<string[]>([]);
  const [localStatus, setLocalStatus] = useState<'idle' | 'loading' | 'ok' | 'error'>('idle');
  const [localError, setLocalError] = useState<string | null>(null);
  const localSeqRef = useRef(0);
  const providerSeqRef = useRef(0);
  const identifySeqRef = useRef(0);
  const [identify, setIdentify] = useState<{
    status: 'idle' | 'loading' | 'ok' | 'empty' | 'error';
    text?: string;
  }>({ status: 'idle' });
  const modelRef = useRef('');
  const endpointRef = useRef('');
  modelRef.current = model;
  endpointRef.current = endpoint;
  const lastActionRef = useRef<'load' | 'test' | 'save'>('load');
  const [toast, setToast] = useState<{ id: number; tone: 'success' | 'error'; text: string } | null>(null);
  const toastSeq = useRef(0);
  const notify = useCallback((tone: 'success' | 'error', text: string) => {
    toastSeq.current += 1;
    setToast({ id: toastSeq.current, tone, text });
  }, []);

  const cliMissing = [
    !cliProject.trim() ? '.fcproj 路径' : null,
    !cliOutput.trim() ? '输出文件' : null,
  ].filter((item): item is string => item !== null);
  // type=number 填入非法字符（abc / . / 1.2.3）时 value 为空串，空串视作 NaN；负数同样拦截。
  const parseSeconds = (text: string) => (text.trim() === '' ? Number.NaN : Number(text));
  const cliStart = parseSeconds(cliStartText);
  const cliEnd = parseSeconds(cliEndText);
  const cliRangeError =
    !Number.isFinite(cliStart) || !Number.isFinite(cliEnd) || cliStart < 0 || cliEnd < 0 || !(cliEnd > cliStart)
      ? '起点、终点须为不小于 0 的数字，且终点必须大于起点'
      : undefined;
  const cliBlocked = cliMissing.length > 0 || cliRangeError !== undefined;
  const cutsMissing = [
    !cutsMedia.trim() ? '素材路径' : null,
    !cutsProject.trim() ? '输出 .fcproj' : null,
  ].filter((item): item is string => item !== null);
  const cutsBlocked = cutsMissing.length > 0;

  function cancelCliExport() {
    cliCancelledRef.current = true;
    cliAbortRef.current?.abort();
  }

  async function runCliExport() {
    setCliTouched(true);
    if (cliBlocked) return;
    const apiBase = (import.meta.env.VITE_API_BASE ?? '').replace(/\/$/, '');
    setCliRunning(true);
    setCliResult(null);
    cliCancelledRef.current = false;
    const controller = new AbortController();
    cliAbortRef.current = controller;
    const abortTimer = window.setTimeout(() => controller.abort(), 1800_000 + 30_000);
    try {
      const response = await fetch(`${apiBase}/api/filmcraft/cli/export`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
        },
        body: JSON.stringify({
          projectPath: cliProject.trim(),
          outputPath: cliOutput.trim(),
          start: cliStart,
          end: cliEnd,
          format: 'h264',
          timeoutSec: 1800,
        }),
        signal: controller.signal,
      });
      const payload = await readCliPayload(response);
      if (!response.ok || !payload.ok) {
        const text = cliFailureText(payload, response.status);
        setCliResult({ ok: false, message: text });
        notify('error', text);
        return;
      }
      setCliResult({ ok: true, output: payload.output, bytes: payload.bytes });
      notify('success', 'FilmCraft CLI 已导出');
    } catch (cause) {
      if (cliCancelledRef.current) {
        setCliResult({ ok: false, cancelled: true, message: '已取消' });
        notify('error', '已取消');
      } else {
        const text = errorMessage(cause);
        setCliResult({ ok: false, message: text });
        notify('error', text);
      }
    } finally {
      window.clearTimeout(abortTimer);
      if (cliAbortRef.current === controller) cliAbortRef.current = null;
      setCliRunning(false);
    }
  }

  function cancelCutsBuild() {
    cutsCancelledRef.current = true;
    cutsAbortRef.current?.abort();
  }

  async function runCutsBuild() {
    setCutsTouched(true);
    if (cutsBlocked) return;
    const apiBase = (import.meta.env.VITE_API_BASE ?? '').replace(/\/$/, '');
    const mediaPaths = cutsMedia
      .split(/\r?\n/)
      .map((line) => line.trim())
      .filter(Boolean);
    setCutsRunning(true);
    setCutsResult(null);
    cutsCancelledRef.current = false;
    const controller = new AbortController();
    cutsAbortRef.current = controller;
    const abortTimer = window.setTimeout(() => controller.abort(), 1800_000 + 30_000);
    try {
      const response = await fetch(`${apiBase}/api/filmcraft/cli/cuts`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
        },
        body: JSON.stringify({
          mediaPaths,
          projectPath: cutsProject.trim(),
          outputPath: cutsOutput.trim() || null,
          timeoutSec: 1800,
        }),
        signal: controller.signal,
      });
      const payload = await readCliPayload(response);
      if (!response.ok || !payload.ok) {
        const text = cliFailureText(payload, response.status);
        setCutsResult({ ok: false, message: text });
        notify('error', text);
        return;
      }
      setCutsResult({ ok: true, project: payload.project, output: payload.output });
      notify('success', 'FilmCraft 工程已生成');
    } catch (cause) {
      if (cutsCancelledRef.current) {
        setCutsResult({ ok: false, cancelled: true, message: '已取消' });
        notify('error', '已取消');
      } else {
        const text = errorMessage(cause);
        setCutsResult({ ok: false, message: text });
        notify('error', text);
      }
    } finally {
      window.clearTimeout(abortTimer);
      if (cutsAbortRef.current === controller) cutsAbortRef.current = null;
      setCutsRunning(false);
    }
  }

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
    };
  }, []);

  useEffect(() => {
    let cancelled = false;
    const apiBase = (import.meta.env.VITE_API_BASE ?? '').replace(/\/$/, '');
    void fetch(`${apiBase}/api/version`, { headers: { Accept: 'application/json' } })
      .then((response) => (response.ok ? (response.json() as Promise<{ appVersion?: string }>) : null))
      .then((payload) => {
        if (!cancelled && payload?.appVersion) setAppVersion(payload.appVersion);
      })
      .catch(() => {
        // 版本号只是「关于」里的展示项，读取失败不影响设置功能。
      });
    return () => {
      cancelled = true;
    };
  }, []);

  const applySnapshot = useCallback((snapshot: AiSettings) => {
    setAi(snapshot);
    setProvider(snapshot.provider === 'compatible' ? 'compatible' : 'ollama');
    setEndpoint(snapshot.endpoint ?? '');
    setModel(snapshot.model ?? '');
  }, []);

  const load = useCallback(
    async (mode: 'initial' | 'refresh' = 'initial') => {
      lastActionRef.current = 'load';
      if (mode === 'refresh') setRefreshing(true);
      try {
        const [nextOverview, nextAi, nextEnv] = await Promise.all([
          getSettingsOverview(),
          getAiSettings(),
          getSettingsEnv(),
        ]);
        if (!mountedRef.current) return;
        setOverview(nextOverview);
        applySnapshot(nextAi);
        setEnvInitial(nextEnv);
        setEnvDraft(nextEnv);
        setEnvErrors({});
        setEnvRestart(false);
        setLastUpdated(new Date().toISOString());
        setBanner((current) => (current?.tone === 'error' ? null : current));
      } catch (cause) {
        if (!mountedRef.current) return;
        setBanner({ tone: 'error', text: errorMessage(cause) });
      } finally {
        if (mountedRef.current) {
          setLoading(false);
          setRefreshing(false);
        }
      }
    },
    [applySnapshot],
  );

  useEffect(() => {
    void load('initial');
  }, [load]);

  /** 本地模式：用既有测试接口读取一次真实已安装列表；seq 抑制过期响应。 */
  const localEndpointDefault = ai?.ollamaEndpoint ?? '';
  const loadLocalModels = useCallback(async () => {
    const seq = ++localSeqRef.current;
    setLocalStatus('loading');
    setLocalError(null);
    try {
      const result = await testAiConnection({
        provider: 'ollama',
        endpoint: (endpointRef.current || localEndpointDefault).trim(),
        model: modelRef.current.trim(),
      });
      if (!mountedRef.current || localSeqRef.current !== seq) return;
      if (result.ok) {
        setLocalModels(result.models);
        setLocalStatus('ok');
      } else {
        setLocalModels([]);
        setLocalStatus('error');
        setLocalError(result.error ?? TEST_CODE_LABEL[result.code ?? 'unknown'] ?? '连接失败');
      }
    } catch (cause) {
      if (!mountedRef.current || localSeqRef.current !== seq) return;
      setLocalModels([]);
      setLocalStatus('error');
      setLocalError(errorMessage(cause));
    }
  }, [localEndpointDefault]);

  // 进入/初次为本地模式且设置已读到时，读取一次已安装模型；idle 门闩防止循环请求。
  const aiLoaded = ai !== null;
  useEffect(() => {
    if (provider === 'ollama' && aiLoaded && localStatus === 'idle') void loadLocalModels();
  }, [provider, aiLoaded, localStatus, loadLocalModels]);

  /** 在线「识别模型」：仅列表查询（后端只调 ModelsAsync，不发推理）；seq + 端点快照抑制过期响应。 */
  function invalidateIdentify() {
    identifySeqRef.current += 1;
    setIdentify({ status: 'idle' });
    setModels([]);
  }

  async function handleIdentify() {
    if (identify.status === 'loading' || testing || saving) return;
    const snapshotEndpoint = endpoint.trim();
    if (!snapshotEndpoint) {
      setIdentify({ status: 'error', text: '请先填写协议地址（Endpoint），再识别模型。' });
      return;
    }
    const seq = ++identifySeqRef.current;
    setIdentify({ status: 'loading' });
    setModels([]);
    const patch: AiSettingsPatch = { provider: 'compatible', endpoint: snapshotEndpoint, model: model.trim() };
    if (apiKey.trim()) patch.apiKey = apiKey.trim();
    try {
      const result = await testAiConnection(patch);
      if (!mountedRef.current || identifySeqRef.current !== seq || endpointRef.current.trim() !== snapshotEndpoint) return;
      if (result.ok) {
        // model_not_found 仍视为列表可用：只列出服务端模型，不替换当前 model。
        setModels(result.models);
        setIdentify(
          result.models.length === 0
            ? { status: 'empty', text: '服务端返回了空的模型列表；仍可手填模型名。' }
            : { status: 'ok', text: `识别到 ${result.models.length} 个模型（${result.elapsedMs} ms）；请选择或手填，选择后需点「保存」。` },
        );
      } else {
        const label = TEST_CODE_LABEL[result.code ?? 'unknown'] ?? '识别失败';
        setIdentify({ status: 'error', text: `${label}：${result.error ?? '服务端未返回模型列表（可能不支持列表接口或密钥无效）'}` });
      }
    } catch (cause) {
      if (!mountedRef.current || identifySeqRef.current !== seq) return;
      setIdentify({ status: 'error', text: errorMessage(cause) });
    }
  }

  const onLocalConnectionChanged = useCallback(() => {
    localSeqRef.current += 1;
    setLocalModels([]);
    setLocalStatus('idle');
    setLocalError(null);
    if (provider === 'ollama') {
      providerSeqRef.current += 1;
      setTestResult(null);
      setModels([]);
    }
  }, [provider]);

  function switchProvider(next: AiProvider) {
    if (next === provider) return;
    const localEndpoint = ai?.ollamaEndpoint ?? '';
    providerSeqRef.current += 1;
    localSeqRef.current += 1; // 作废进行中的本地读取
    setLocalStatus('idle');
    setLocalModels([]);
    setLocalError(null);
    setProvider(next);
    setTestResult(null);
    invalidateIdentify();
    setClearApiKey(false);
    if (next === 'compatible') {
      if (!endpoint.trim() || endpoint.trim() === localEndpoint) {
        const saved = ai?.endpoint ?? '';
        setEndpoint(saved && saved !== localEndpoint ? saved : COMPATIBLE_PRESETS[0].endpoint);
      }
    } else {
      if (!endpoint.trim() || isCompatiblePresetEndpoint(endpoint.trim()) || endpoint.trim() === localEndpoint) {
        setEndpoint(localEndpoint);
      }
    }
  }

  function buildPatch(): AiSettingsPatch {
    const patch: AiSettingsPatch = {
      provider,
      endpoint: endpoint.trim(),
      model: model.trim(),
    };
    if (provider === 'compatible') {
      if (apiKey.trim()) patch.apiKey = apiKey.trim();
      if (clearApiKey) patch.clearApiKey = true;
    }
    return patch;
  }

  function validateLocally(): string | null {
    if (!model.trim()) return '请选择或填写模型';
    if (provider === 'compatible' && !endpoint.trim()) return '在线兼容 API 需要填写 Endpoint';
    return null;
  }

  async function handleTest(mode: 'test' | 'list') {
    if (testing || saving) return;
    lastActionRef.current = 'test';
    const invalid = validateLocally();
    if (invalid) {
      setBanner({ tone: 'error', text: invalid });
      return;
    }
    setTesting(true);
    setTestResult(null);
    const startedProvider = provider;
    const startedSeq = providerSeqRef.current;
    const startedEndpoint = endpoint.trim();
    try {
      const result = await testAiConnection(buildPatch());
      if (!mountedRef.current) return;
      // 期间切换过服务方：丢弃旧响应，避免污染另一侧的模型列表/结果。
      if (providerSeqRef.current !== startedSeq || endpointRef.current.trim() !== startedEndpoint) return;
      if (startedProvider === 'ollama' && result.ok) setLocalModels(result.models);
      setTestResult(result);
      const codeLabel = TEST_CODE_LABEL[result.code ?? (result.ok ? 'ok' : 'unknown')] ?? '未分类';
      if (result.ok && result.code !== 'model_not_found') {
        setModels(result.models);
        notify(
          'success',
          mode === 'list'
            ? `模型列表已拉取：${result.models.length} 个可用模型（${result.elapsedMs} ms）`
            : `${codeLabel}：${result.models.length} 个可用模型，耗时 ${result.elapsedMs} ms`,
        );
      } else if (result.ok) {
        setModels(result.models);
        notify('error', `${codeLabel}：当前模型「${model.trim()}」不在服务端可用列表中`);
      } else {
        notify('error', `${codeLabel}：${result.error ?? '连接失败'}`);
      }
    } catch (cause) {
      if (mountedRef.current) notify('error', errorMessage(cause));
    } finally {
      if (mountedRef.current) setTesting(false);
    }
  }

  async function handleSave() {
    if (saving || testing) return;
    lastActionRef.current = 'save';
    const invalid = validateLocally();
    if (invalid) {
      setBanner({ tone: 'error', text: invalid });
      return;
    }
    setSaving(true);
    try {
      const response = await saveAiSettings(buildPatch());
      if (!mountedRef.current) return;
      setAi(response.ai);
      setProvider(response.ai.provider === 'compatible' ? 'compatible' : 'ollama');
      setEndpoint(response.ai.endpoint ?? '');
      setModel(response.ai.model ?? '');
      setApiKey('');
      setClearApiKey(false);
      setOverview((current) =>
        current
          ? { ...current, aiProvider: response.ai.provider, aiEndpoint: response.ai.endpoint, aiModel: response.ai.model }
          : current,
      );
      setBanner(null);
      notify('success', 'AI 设置已保存并立即生效（自动分析会在下次运行时使用新连接）。');
    } catch (cause) {
      if (mountedRef.current) notify('error', errorMessage(cause));
    } finally {
      if (mountedRef.current) setSaving(false);
    }
  }

  function handleRetry() {
    const action = lastActionRef.current;
    if (action === 'save') void handleSave();
    else if (action === 'test') void handleTest('test');
    else void load('refresh');
  }

  /* ── 环境 / 自动切片：dirty 集合 + PATCH 局部保存 ─────────────── */

  const envDirtyKeys = useMemo<Array<keyof SettingsEnvValues>>(() => {
    if (!envDraft || !envInitial) return [];
    const keys: Array<keyof SettingsEnvValues> = [
      'autoHighlight',
      'autoHighlightCooldownSeconds',
      'highlightPreRollSeconds',
      'highlightPostRollSeconds',
      'storageRoot',
      'renderWorkDirectory',
      'exportOutputDirectory',
      'whisperModel',
      'ffmpegPath',
      'pythonPath',
    ];
    return keys.filter((key) => envDraft[key] !== envInitial[key]);
  }, [envDraft, envInitial]);

  const envDirty = envDirtyKeys.length > 0;

  /** 路径类字段改动后不能为空（后端 PATCH 同样会拒绝）；只校验改过的项。 */
  const envClientErrors = useMemo<Record<string, string>>(() => {
    const errors: Record<string, string> = {};
    if (!envDraft) return errors;
    for (const key of ['storageRoot', 'ffmpegPath', 'pythonPath'] as const) {
      if (envDirtyKeys.includes(key) && envDraft[key].trim() === '') errors[key] = '路径不能为空';
    }
    return errors;
  }, [envDraft, envDirtyKeys]);
  const envInvalid = Object.keys(envClientErrors).length > 0;

  function updateEnv<K extends keyof SettingsEnvValues>(key: K, value: SettingsEnvValues[K]) {
    setEnvDraft((current) => (current ? { ...current, [key]: value } : current));
    setEnvErrors((current) => {
      if (!current[key]) return current;
      const next = { ...current };
      delete next[key];
      return next;
    });
  }

  async function handleSaveEnv() {
    if (!envDraft || !envInitial || envSaving || envDirtyKeys.length === 0 || envInvalid) return;
    const trimmed = (key: keyof SettingsEnvValues) => {
      const value = envDraft[key];
      return typeof value === 'string' ? value.trim() : value;
    };
    const partial = Object.fromEntries(envDirtyKeys.map((key) => [key, trimmed(key)])) as Partial<SettingsEnvValues>;
    const normalized = { ...envDraft, ...partial } as SettingsEnvValues;
    setEnvSaving(true);
    setEnvErrors({});
    try {
      const result = await patchSettings(partial);
      if (!mountedRef.current) return;
      setEnvInitial(normalized);
      setEnvDraft(normalized);
      setOverview((current) =>
        current
          ? {
              ...current,
              storageRoot: normalized.storageRoot,
              renderWorkDirectory: normalized.renderWorkDirectory,
              exportOutputDirectory: normalized.exportOutputDirectory,
              whisperModel: normalized.whisperModel,
              ffmpegPath: normalized.ffmpegPath,
              pythonPath: normalized.pythonPath,
            }
          : current,
      );
      setEnvRestart(result.restartRequired);
      setBanner(
        result.restartRequired
          ? {
              tone: 'info',
              text: `已保存 ${envDirtyKeys.length} 项；存储目录、FFmpeg / Python 路径、转写模型的变更需重启 LiveClipper 后生效。`,
            }
          : {
              tone: 'ok',
              text: `已保存 ${envDirtyKeys.length} 项，未改动的项保持原样。`,
            },
      );
      notify('success', '已保存');
    } catch (cause) {
      if (!mountedRef.current) return;
      if (cause instanceof SettingsPatchError) {
        const next: Record<string, string> = {};
        for (const field of cause.fields) next[field] = '该值不合法，请检查范围或类型';
        setEnvErrors(next);
        setBanner({ tone: 'error', text: cause.message });
      } else {
        setBanner({ tone: 'error', text: errorMessage(cause) });
      }
    } finally {
      if (mountedRef.current) setEnvSaving(false);
    }
  }

  const endpointHint =
    provider === 'ollama'
      ? `后端读取 LIVECLIPPER_OLLAMA_URL（可覆盖），未设置时使用内置默认本机地址。${
          ai?.ollamaEndpoint ? `接口报告的当前地址：${ai.ollamaEndpoint}。` : ''
        }`
      : '在线兼容 API 必须使用 HTTPS；本机回环地址允许 http。若服务需要 /v1 前缀请一并填写。';
  const localMissing = localStatus === 'ok' && model.trim() !== '' && !localModels.includes(model.trim());
  const providerLabel = provider === 'ollama' ? '本地 Ollama' : '在线兼容 API';

  return (
    <div className="settings-workspace">
      {banner ? (
        <div className={`settings-banner settings-banner--${banner.tone}`} role="status" aria-live="polite">
          <StatusDot tone={banner.tone === 'ok' ? 'green' : banner.tone === 'error' ? 'red' : 'blue'} size="sm" />
          <span className="settings-banner__text">{banner.text}</span>
          {banner.tone === 'error' ? (
            <Button variant="link" size="sm" onClick={handleRetry}>
              重试
            </Button>
          ) : null}
        </div>
      ) : null}

      <Toast
        open={toast !== null}
        id={toast?.id}
        tone={toast?.tone ?? 'neutral'}
        message={toast?.text ?? ''}
        onClose={() => setToast(null)}
        action={
          toast?.tone === 'error' ? (
            <Button variant="link" size="sm" onClick={handleRetry}>
              重试
            </Button>
          ) : undefined
        }
      />

      <div className="settings-grid">
        <Card
          title="AI 连接"
          eyebrow="自动分析、自动切片标题与摘要都使用这里的连接"
          meta={`最近读取 ${formatClock(lastUpdated)}`}
          actions={
            <div className="settings-card-actions">
              <Badge tone={provider === 'ollama' ? 'green' : 'blue'} dot>
                {providerLabel}
              </Badge>
              <IconButton label="重新读取设置" onClick={() => void load('refresh')} disabled={refreshing}>
                <RefreshCw size={15} className={refreshing ? 'settings-spin' : undefined} />
              </IconButton>
            </div>
          }
          footer={
            <div className="settings-actions">
              <Button
                size="sm"
                leading={<CheckCircle2 size={14} />}
                loading={testing}
                disabled={saving}
                onClick={() => void handleTest('test')}
              >
                测试连接
              </Button>
              {provider === 'compatible' ? (
                <Button
                  size="sm"
                  leading={<ListFilter size={14} />}
                  loading={identify.status === 'loading'}
                  disabled={testing || saving}
                  onClick={() => void handleIdentify()}
                >
                  识别模型
                </Button>
              ) : null}
              <span className="settings-actions__spacer" />
              <Button
                variant="solid"
                size="sm"
                leading={<Save size={14} />}
                loading={saving}
                disabled={testing}
                onClick={() => void handleSave()}
              >
                保存
              </Button>
            </div>
          }
        >
          <SegmentedTabs
            ariaLabel="AI 服务提供方"
            items={PROVIDER_TABS}
            value={provider}
            onChange={(next) => switchProvider(next)}
          />

          <details className="settings-note">
            <summary>说明与注意事项</summary>
            <div className="settings-note__body">
              <span>
                本卡片只维护 AI 连接；存储目录、FFmpeg / Python 路径、转写模型与自动切片请在下方「自动切片与本地环境」中修改。
              </span>
              <span>
                「测试连接」与在线「识别模型」共用同一查询接口（只列模型，不做推理），最多等待 8 秒；失败会区分超时、网络不可达、模型不存在与配置无效。「识别模型」仅在你点击时才请求，不会自动保存或替换当前模型。
              </span>
              <span>密钥经 Windows DPAPI 加密保存，接口只返回 hasApiKey；留空表示不修改已有密钥。</span>
            </div>
          </details>

          {loading && !ai ? (
            <EmptyState
              compact
              icon={<LoaderCircle size={22} className="settings-spin" />}
              title="正在读取设置…"
              description="正在从本机读取 AI 连接配置"
            />
          ) : (
            <div className="settings-form">
              {provider === 'compatible' ? (
                <div className="settings-field">
                  <span className="settings-field__label">服务预设</span>
                  <div className="settings-chips" role="group" aria-label="服务预设">
                    {COMPATIBLE_PRESETS.map((preset) => (
                      <Button
                        key={preset.id}
                        size="sm"
                        variant={endpoint.trim() === preset.endpoint ? 'solid' : 'outline'}
                        onClick={() => {
                          invalidateIdentify();
                          setEndpoint(preset.endpoint);
                          setModel(preset.model);
                        }}
                      >
                        {preset.name}
                      </Button>
                    ))}
                    <Button
                      size="sm"
                      variant={isCompatiblePresetEndpoint(endpoint.trim()) ? 'outline' : 'solid'}
                      onClick={() => {
                        const current = endpoint.trim();
                        const matched = COMPATIBLE_PRESETS.find((preset) => preset.endpoint === current);
                        if (!matched) return; // 已是自定义：保留用户手填的地址和模型
                        invalidateIdentify();
                        setEndpoint('');
                        if (model.trim() === matched.model) setModel('');
                      }}
                    >
                      第三方 / 自定义 API
                    </Button>
                  </div>
                </div>
              ) : null}

              {provider === 'ollama' ? (
                <>
                  <div className="settings-field">
                    <span className="settings-field__label">连接</span>
                    <span className="settings-hint">
                      <Server size={13} /> 自动连接本机 Ollama，无需填写地址。
                    </span>
                    <details className="settings-note">
                      <summary>高级</summary>
                      <div className="settings-note__body">
                        <span>{endpointHint}</span>
                      </div>
                    </details>
                  </div>

                  <SelectField
                    label="Model（本机已安装）"
                    hint={
                      localStatus === 'loading'
                        ? '正在读取本机已安装模型…'
                        : localStatus === 'ok' && localModels.length === 0
                          ? '本机 Ollama 已连接，但没有已安装模型。可使用下方可选下载入口，完成后再点刷新。'
                          : localStatus === 'error'
                            ? `读取失败：${localError ?? '未知错误'}。请确认 Ollama 已启动（运行 ollama serve 或打开 Ollama 应用），然后点刷新。`
                            : localMissing
                              ? `当前配置的「${model.trim()}」未安装，已保留原值，不会自动替换；请从列表中选择，或下载该模型后刷新。`
                              : '只列出本机真实已安装的模型；选择后需点「保存」才生效。'
                    }
                    value={model}
                    disabled={localStatus === 'loading'}
                    onChange={(event) => setModel(event.target.value)}
                  >
                    {!model.trim() ? <option value="">请选择模型</option> : null}
                    {model.trim() && !localModels.includes(model.trim()) ? (
                      <option value={model}>
                        {localStatus === 'ok' ? `${model}（未安装）` : `${model}（当前配置，未能确认）`}
                      </option>
                    ) : null}
                    {localModels.map((item) => (
                      <option key={item} value={item}>
                        {item}
                      </option>
                    ))}
                  </SelectField>
                  <div className="settings-inline">
                    <Button
                      size="sm"
                      leading={<RefreshCw size={14} className={localStatus === 'loading' ? 'settings-spin' : undefined} />}
                      disabled={localStatus === 'loading' || testing || saving}
                      onClick={() => void loadLocalModels()}
                    >
                      刷新已安装模型
                    </Button>
                  </div>
                  <OllamaModelDownloadPanel installedModels={localStatus === 'ok' ? localModels : null} />
                </>
              ) : (
                <>
              <TextField
                label="Endpoint"
                hint={endpointHint}
                leading={<Server size={14} />}
                value={endpoint}
                spellCheck={false}
                autoComplete="off"
                placeholder="https://api.deepseek.com/v1"
                onChange={(event) => {
                  invalidateIdentify();
                  setEndpoint(event.target.value);
                }}
              />

              <TextField
                label="Model"
                hint="可直接输入或点选预设/列表。本地 Ollama 用本机已安装模型；在线兼容 API 是远程推理，不会下载模型。切换服务后请确认该服务支持所选型号。"
                leading={<Cpu size={14} />}
                value={model}
                spellCheck={false}
                autoComplete="off"
                list="settings-model-options"
                placeholder="deepseek-chat"
                onChange={(event) => setModel(event.target.value)}
              />
              <datalist id="settings-model-options">
                {models.map((item) => (
                  <option key={item} value={item} />
                ))}
              </datalist>
              <div className="settings-chips" role="group" aria-label="模型预设">
                {models.slice(0, 12).map((item) => (
                  <Button
                    key={item}
                    size="sm"
                    variant={model.trim() === item ? 'solid' : 'outline'}
                    onClick={() => setModel(item)}
                  >
                    {item}
                  </Button>
                ))}
                {models.length === 0 && identify.status === 'idle' ? (
                  <span className="settings-hint">填好协议地址后点「识别模型」，可直接选择服务端模型；也可手填。</span>
                ) : null}
              </div>
              {identify.text ? (
                <span
                  className="settings-hint"
                  role="status"
                  aria-live="polite"
                  style={identify.status === 'error' ? { color: 'var(--color-danger, #c0392b)' } : undefined}
                >
                  {identify.text}
                </span>
              ) : null}
                </>
              )}

              {provider === 'compatible' ? (
                <>
                  <TextField
                    label="API Key"
                    hint={
                      ai?.hasApiKey
                        ? '已加密保存；留空表示不修改。密钥不会回显，也不会下发给界面。'
                        : '密钥经 Windows DPAPI 加密写入本机 settings.json；接口只返回 hasApiKey。'
                    }
                    error={clearApiKey ? '保存后将清除已保存的密钥。' : undefined}
                    leading={<KeyRound size={14} />}
                    type="password"
                    value={apiKey}
                    autoComplete="off"
                    spellCheck={false}
                    placeholder={ai?.hasApiKey ? '已加密保存；留空表示不修改' : '粘贴 API Key（例如 sk-…）'}
                    onChange={(event) => {
                      setApiKey(event.target.value);
                      if (event.target.value) setClearApiKey(false);
                    }}
                  />
                  <div className="settings-inline">
                    <span className={`settings-key-state ${ai?.hasApiKey ? 'is-saved' : ''}`}>
                      <StatusDot tone={ai?.hasApiKey ? 'green' : 'idle'} size="sm" />
                      {ai?.hasApiKey ? '已有加密密钥（DPAPI）' : '尚未保存密钥'}
                    </span>
                    {ai?.hasApiKey ? (
                      <Button
                        variant="ghost"
                        size="sm"
                        disabled={clearApiKey}
                        onClick={() => {
                          setApiKey('');
                          setClearApiKey(true);
                        }}
                      >
                        清除密钥
                      </Button>
                    ) : null}
                  </div>
                </>
              ) : null}

              {testResult ? (
                <div
                  className={`settings-result ${
                    testResult.ok && testResult.code !== 'model_not_found'
                      ? 'settings-result--ok'
                      : 'settings-result--bad'
                  }`}
                  role="status"
                >
                  <StatusDot
                    tone={testResult.ok && testResult.code !== 'model_not_found' ? 'green' : 'red'}
                    size="sm"
                  />
                  <div className="settings-result__text">
                    <strong>
                      {TEST_CODE_LABEL[testResult.code ?? (testResult.ok ? 'ok' : 'unknown')] ?? '未分类'}
                      {' · '}
                      {testResult.models.length} 个模型 · {testResult.elapsedMs} ms
                    </strong>
                    <span>
                      {testResult.code === 'model_not_found'
                        ? `当前模型「${model || '—'}」不在可用列表中，可先下载模型或从列表里改选。`
                        : testResult.ok
                          ? testResult.models.some(
                              (item) => item.trim().toLowerCase() === model.trim().toLowerCase(),
                            )
                            ? '当前模型在可用列表中。'
                            : '当前模型不在可用列表中，可先下载模型或从列表里改选。'
                          : testResult.code === 'timeout'
                            ? '已中止请求（8 秒上限）：检查 Endpoint 是否可达；保存不受影响。'
                            : testResult.code === 'network'
                              ? '无法建立连接：检查 Endpoint 地址、端口、证书与代理。'
                              : testResult.code === 'invalid'
                                ? '配置无效：检查 Endpoint、模型名与密钥格式。'
                                : testResult.error ?? '未知错误'}
                    </span>
                  </div>
                </div>
              ) : null}

              {provider === 'compatible' && models.length > 0 ? (
                <div className="settings-models">
                  <div className="settings-models__head">
                    <ListFilter size={14} />
                    <strong>可用模型</strong>
                    <span>{models.length} 个 · 点击填入 Model</span>
                  </div>
                  <div className="settings-chips settings-chips--scroll">
                    {models.slice(0, 80).map((item) => (
                      <Button
                        key={item}
                        size="sm"
                        variant={model.trim() === item ? 'solid' : 'outline'}
                        onClick={() => setModel(item)}
                      >
                        {item}
                      </Button>
                    ))}
                  </div>
                  {models.length > 80 ? (
                    <span className="settings-hint">仅显示前 80 个，共 {models.length} 个。</span>
                  ) : null}
                </div>
              ) : null}
            </div>
          )}
        </Card>

        <Card
          title="自动切片与本地环境"
          eyebrow="点击「保存改动」后才生效，只保存你改过的项"
          actions={
            <Badge tone={envDirty ? 'blue' : 'neutral'} dot={envDirty}>
              {envDirty ? `待保存 ${envDirtyKeys.length} 项` : '无改动'}
            </Badge>
          }
          footer={
            <div className="settings-actions">
              <Button
                size="sm"
                variant="ghost"
                disabled={!envDirty || envSaving}
                onClick={() => {
                  setEnvDraft(envInitial);
                  setEnvErrors({});
                }}
              >
                撤销改动
              </Button>
              <span className="settings-actions__spacer" />
              <Button
                variant="solid"
                size="sm"
                leading={<Save size={14} />}
                loading={envSaving}
                disabled={!envDirty || envSaving || envInvalid}
                onClick={() => void handleSaveEnv()}
              >
                保存改动{envDirty ? `（${envDirtyKeys.length}）` : ''}
              </Button>
            </div>
          }
        >
          {loading && !envDraft ? (
            <EmptyState
              compact
              icon={<LoaderCircle size={22} className="settings-spin" />}
              title="正在读取设置…"
              description="正在从本机读取当前配置"
            />
          ) : (
            <div className="settings-form">
              <SelectField
                fieldId="settings-env-autohighlight"
                label="自动切片"
                hint="按弹幕热度自动标记高光；关闭后只保留手动保存。"
                value={envDraft?.autoHighlight ? 'on' : 'off'}
                onChange={(event) => updateEnv('autoHighlight', event.target.value === 'on')}
              >
                <option value="on">开启</option>
                <option value="off">关闭</option>
              </SelectField>

              <TextField
                fieldId="settings-env-cooldown"
                label="自动切片冷却（秒）"
                hint="热度触发后的最短间隔：10–900 秒，修改后立即生效。"
                type="number"
                min={10}
                max={900}
                step={5}
                value={envDraft?.autoHighlightCooldownSeconds ?? 60}
                error={envErrors.autoHighlightCooldownSeconds}
                onChange={(event) => updateEnv('autoHighlightCooldownSeconds', Number(event.target.value))}
              />

              <div className="settings-env-row">
                <TextField
                  fieldId="settings-env-preroll"
                  label="前录（秒）"
                  hint="自动切片的触发前保留时长：0–1800。"
                  type="number"
                  min={0}
                  max={1800}
                  step={1}
                  value={envDraft?.highlightPreRollSeconds ?? 200}
                  error={envErrors.highlightPreRollSeconds}
                  onChange={(event) => updateEnv('highlightPreRollSeconds', Number(event.target.value))}
                />
                <TextField
                  fieldId="settings-env-postroll"
                  label="后录（秒）"
                  hint="自动切片的触发后保留时长：0–1800。"
                  type="number"
                  min={0}
                  max={1800}
                  step={1}
                  value={envDraft?.highlightPostRollSeconds ?? 200}
                  error={envErrors.highlightPostRollSeconds}
                  onChange={(event) => updateEnv('highlightPostRollSeconds', Number(event.target.value))}
                />
              </div>

              <TextField
                fieldId="settings-env-storage"
                label={
                  <span className="settings-label-badge">
                    存储目录
                    <Badge tone={envRestart ? 'green' : 'neutral'}>保存后需重启</Badge>
                  </span>
                }
                hint="素材、转写与工程数据的根目录；保存后需重启 LiveClipper 生效。"
                value={envDraft?.storageRoot ?? ''}
                error={envClientErrors.storageRoot ?? envErrors.storageRoot}
                spellCheck={false}
                onChange={(event) => updateEnv('storageRoot', event.target.value)}
              />

              <TextField
                fieldId="settings-env-export-output"
                label="导出输出目录"
                hint="最终成片和工程包的生成目录；留空保持原有输出规则。保存后无需重启，仅对后续导出生效，不迁移已有文件。工程包下载时仍可选择保存位置。"
                placeholder="例如 F:\LiveClipper-Exports（留空使用原规则）"
                value={envDraft?.exportOutputDirectory ?? ''}
                error={envErrors.exportOutputDirectory}
                spellCheck={false}
                onChange={(event) => updateEnv('exportOutputDirectory', event.target.value)}
              />

              <TextField
                fieldId="settings-env-render-work"
                label="导出临时目录"
                hint="仅存放导出中间文件，不搬移数据库或原素材。留空使用存储目录下的 render-work；建议选择空间充足的磁盘，保存后用于后续导出。"
                placeholder="例如 F:\LiveClipper-RenderWork（留空使用默认）"
                value={envDraft?.renderWorkDirectory ?? ''}
                error={envErrors.renderWorkDirectory}
                spellCheck={false}
                onChange={(event) => updateEnv('renderWorkDirectory', event.target.value)}
              />

              <TextField
                fieldId="settings-env-ffmpeg"
                label={
                  <span className="settings-label-badge">
                    FFmpeg 路径
                    <Badge tone={envRestart ? 'green' : 'neutral'}>保存后需重启</Badge>
                  </span>
                }
                hint="ffmpeg 可执行文件的完整路径，或 PATH 中的命令名（如 ffmpeg）；保存后需重启 LiveClipper 生效。"
                value={envDraft?.ffmpegPath ?? ''}
                error={envClientErrors.ffmpegPath ?? envErrors.ffmpegPath}
                spellCheck={false}
                onChange={(event) => updateEnv('ffmpegPath', event.target.value)}
              />

              <TextField
                fieldId="settings-env-python"
                label={
                  <span className="settings-label-badge">
                    Python 路径
                    <Badge tone={envRestart ? 'green' : 'neutral'}>保存后需重启</Badge>
                  </span>
                }
                hint="转写、人声分离与导出脚本使用的 Python 解释器；保存后需重启 LiveClipper 生效。"
                value={envDraft?.pythonPath ?? ''}
                error={envClientErrors.pythonPath ?? envErrors.pythonPath}
                spellCheck={false}
                onChange={(event) => updateEnv('pythonPath', event.target.value)}
              />

              <SelectField
                fieldId="settings-env-whisper"
                label={
                  <span className="settings-label-badge">
                    转写模型
                    <Badge tone={envRestart ? 'green' : 'neutral'}>保存后需重启</Badge>
                  </span>
                }
                hint="faster-whisper 模型；保存后需重启生效。"
                value={envDraft?.whisperModel ?? 'large-v3-turbo'}
                error={envErrors.whisperModel}
                onChange={(event) => updateEnv('whisperModel', event.target.value)}
              >
                {WHISPER_MODELS.map((item) => (
                  <option key={item} value={item}>
                    {item}
                  </option>
                ))}
              </SelectField>

              <div className="settings-field">
                <span className="settings-field__label">本地导出目录（当前值，只读）</span>
                <span className="settings-hint settings-hint--mono">{overview?.localOutputRoot || '未读取到'}</span>
                <span className="settings-hint">
                  这里仅显示已保存的导出目录配置；目前没有在此处修改它的入口，请不要把它当作存储目录的跟随值。
                </span>
              </div>

              <p className="settings-hint settings-hint--mono">
                配置文件：%LOCALAPPDATA%\LiveClipper\settings.json（或 LIVECLIPPER_CONFIG_DIR）
              </p>
            </div>
          )}
        </Card>

        <div className="settings-side">

          <Panel title="FilmCraft CLI" eyebrow="P2a · 已有 .fcproj 直出">
            <div className="settings-form">
              <TextField
                fieldId="settings-cli-project"
                onBlur={() => setCliTouched(true)}
                label=".fcproj 路径"
                hint="在真实 FilmCraft 里保存的工程文件，schema 13。"
                value={cliProject}
                placeholder="F:\projects\我的工程.fcproj"
                spellCheck={false}
                onChange={(event) => {
                  setCliProject(event.target.value);
                  setCliResult(null);
                }}
              />
              <TextField
                fieldId="settings-cli-output"
                onBlur={() => setCliTouched(true)}
                label="输出文件"
                hint="必须已存在的目录；扩展名 .mp4 / .mov / .mxf。"
                value={cliOutput}
                placeholder="F:\exports\filmcraft.mp4"
                spellCheck={false}
                onChange={(event) => {
                  setCliOutput(event.target.value);
                  setCliResult(null);
                }}
              />
              <div className="settings-env-row">
                <TextField
                  fieldId="settings-cli-start"
                  label="起点（秒）"
                  type="number"
                  min={0}
                  step={0.1}
                  value={cliStartText}
                  onChange={(event) => {
                    setCliStartText(event.target.value);
                    setCliResult(null);
                  }}
                />
                <TextField
                  fieldId="settings-cli-end"
                  label="终点（秒）"
                  error={cliRangeError}
                  type="number"
                  min={0}
                  step={0.1}
                  value={cliEndText}
                  onChange={(event) => {
                    setCliEndText(event.target.value);
                    setCliResult(null);
                  }}
                />
              </div>
              <div className="settings-side-actions">
                {/* 置灰按钮收不到 click：用外层 span 捕获「尝试提交」 */}
                <span onPointerDownCapture={() => setCliTouched(true)} onClickCapture={() => setCliTouched(true)}>
                  <Button
                    size="sm"
                    variant="solid"
                    leading={cliRunning ? <LoaderCircle size={14} className="spin" /> : <Cpu size={14} />}
                    disabled={cliRunning || cliBlocked}
                    onClick={() => void runCliExport()}
                  >
                    {cliRunning ? 'CLI 导出中…' : '用 FilmCraft CLI 导出'}
                  </Button>
                </span>
                <Button size="sm" disabled={!cliRunning} onClick={cancelCliExport}>
                  取消
                </Button>
              </div>
              {cliTouched && cliMissing.length > 0 ? (
                <p className="settings-field-error" role="status">请先填写：{cliMissing.join('、')}</p>
              ) : null}
              {cliResult ? (
                <p className={`settings-hint${cliResult.ok ? '' : ' settings-hint--error'}`}>
                  {cliResult.ok
                    ? `已导出 ${cliResult.output ?? ''} · ${Math.max(1, Math.round((cliResult.bytes ?? 0) / 1024))} KB`
                    : cliResult.cancelled
                      ? '已取消'
                      : `失败：${cliResult.message ?? '未知错误'}`}
                </p>
              ) : (
                <p className="settings-hint">CLI 只在导出期间作为子进程存在；失败不会改动工程或源文件。</p>
              )}
            </div>
          </Panel>

          <Panel title="FilmCraft 批量工程" eyebrow="P2b · 片段 → .fcproj">
            <div className="settings-form">
              <label className="settings-field" htmlFor="settings-cuts-media">
                <span className="settings-field__label">素材路径（每行一个）</span>
                <textarea
                  id="settings-cuts-media"
                  className="settings-cuts-media"
                  rows={4}
                  spellCheck={false}
                  placeholder={'F:\\media\\clip-01.mp4\nF:\\media\\clip-02.mp4'}
                  value={cutsMedia}
                  onBlur={() => setCutsTouched(true)}
                  onChange={(event) => {
                    setCutsMedia(event.target.value);
                    setCutsResult(null);
                  }}
                />
                <span className="settings-hint">按顺序拼成一条 cuts-only 序列；目标尺寸取第一条素材。</span>
              </label>
              <TextField
                fieldId="settings-cuts-project"
                onBlur={() => setCutsTouched(true)}
                label="输出 .fcproj"
                hint="必须已存在的目录；扩展名 .fcproj。"
                value={cutsProject}
                placeholder="F:\exports\cuts.fcproj"
                spellCheck={false}
                onChange={(event) => {
                  setCutsProject(event.target.value);
                  setCutsResult(null);
                }}
              />
              <TextField
                fieldId="settings-cuts-output"
                label="同时导出 .mp4（可选）"
                hint="留空只生成工程；填写时必须已存在的目录。"
                value={cutsOutput}
                placeholder="F:\exports\cuts.mp4"
                spellCheck={false}
                onChange={(event) => {
                  setCutsOutput(event.target.value);
                  setCutsResult(null);
                }}
              />
              <div className="settings-side-actions">
                <span onPointerDownCapture={() => setCutsTouched(true)} onClickCapture={() => setCutsTouched(true)}>
                  <Button
                    size="sm"
                    variant="solid"
                    leading={cutsRunning ? <LoaderCircle size={14} className="spin" /> : <Cpu size={14} />}
                    disabled={cutsRunning || cutsBlocked}
                    onClick={() => void runCutsBuild()}
                  >
                    {cutsRunning ? '生成中…' : '生成 FilmCraft 工程'}
                  </Button>
                </span>
                <Button size="sm" disabled={!cutsRunning} onClick={cancelCutsBuild}>
                  取消
                </Button>
              </div>
              {cutsTouched && cutsMissing.length > 0 ? (
                <p className="settings-field-error" role="status">请先填写：{cutsMissing.join('、')}</p>
              ) : null}
              {cutsResult ? (
                <p className={`settings-hint${cutsResult.ok ? '' : ' settings-hint--error'}`}>
                  {cutsResult.ok
                    ? `已生成 ${cutsResult.project ?? ''}${cutsResult.output ? ` · 已导出 ${cutsResult.output}` : ''}`
                    : cutsResult.cancelled
                      ? '已取消'
                      : `失败：${cutsResult.message ?? '未知错误'}`}
                </p>
              ) : (
                <p className="settings-hint">调用真实 FilmCraft CLI；工程生成后才进入导出。</p>
              )}
            </div>
          </Panel>

          <Panel title="关于" eyebrow="版本与配置">
            <dl className="settings-kv">
              <div>
                <dt>应用版本</dt>
                <dd>{appVersion ? `v${appVersion}` : '—'}</dd>
              </div>
              <div>
                <dt>配置文件版本</dt>
                <dd>v{overview?.settingsVersion ?? '—'}</dd>
              </div>
              <div>
                <dt>AI 服务提供方</dt>
                <dd>{overview?.aiProvider || '—'}</dd>
              </div>
              <div>
                <dt>界面外壳</dt>
                <dd>AppShell · 浅/深主题跟随</dd>
              </div>
            </dl>
            <ReleaseUpdatePanel />
          </Panel>
        </div>

        <OllamaManagementPanel onConnectionChanged={onLocalConnectionChanged} />

        <div className="settings-asr-span">
          <WhisperModelPreparationPanel />
        </div>

        <div className="settings-music-gen-span">
          <MusicGenerationSettingsPanel />
        </div>
      </div>
    </div>
  );
}
