import { useState } from 'react';
import { RefreshCw } from 'lucide-react';
import { Button } from '../../design';

interface CheckResult {
  message: string;
  releaseUrl?: string | null;
  baiduUrl?: string | null;
}

const apiBase = (import.meta.env.VITE_API_BASE ?? '').replace(/\/$/, '');

export function ReleaseUpdatePanel() {
  const [checking, setChecking] = useState(false);
  const [result, setResult] = useState<CheckResult | null>(null);

  async function check() {
    setChecking(true);
    setResult(null);
    try {
      const response = await fetch(apiBase + '/api/updates/check', {
        cache: 'no-store',
        headers: { Accept: 'application/json' },
      });
      if (!response.ok) throw new Error();
      setResult((await response.json()) as CheckResult);
    } catch {
      setResult({ message: '无法读取更新结果，请稍后重试。' });
    } finally {
      setChecking(false);
    }
  }

  return (
    <div className="settings-form">
      <div className="settings-inline">
        <Button type="button" size="sm" loading={checking} leading={<RefreshCw size={14} />} onClick={check}>
          {checking ? '正在检查…' : '检查更新'}
        </Button>
      </div>
      <span className="settings-hint" role="status" aria-live="polite">
        {checking ? '正在检查 GitHub 稳定版…' : result?.message ?? '仅手动检查，不会自动下载安装包。'}
      </span>
      {result && (result.releaseUrl || result.baiduUrl) && (
        <div className="settings-inline">
          {result.releaseUrl && <a href={result.releaseUrl} target="_blank" rel="noopener noreferrer">前往 GitHub 下载</a>}
          {result.baiduUrl && <a href={result.baiduUrl} target="_blank" rel="noopener noreferrer">百度网盘（手动备用）</a>}
        </div>
      )}
    </div>
  );
}
