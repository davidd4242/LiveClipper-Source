import { packageEditor, importEditorPackage } from '../../api/client';
import type { EditorPackageResult, CopyEditorResult, PackageUploadProgress } from '../../api/client';
import type { CopySaveSession, CopyStamp } from './copyBridge';
export interface PackageState {
  open: boolean; mode: 'save' | 'import'; busy: boolean; name: string; error: string | null;
  download: EditorPackageResult | null; imported: CopyEditorResult | null; importProgress: PackageUploadProgress | null;
}
export const initialPackageState = (): PackageState => ({ open: false, mode: 'import', busy: false, name: '', error: null, download: null, imported: null, importProgress: null });
export function safePackageDownload(value: string, origin: string) {
  const url = new URL(value, origin);
  if (url.origin !== new URL(origin).origin || !/^https?:$/.test(url.protocol)) throw new Error('工程包下载地址无效。');
  return url.href;
}
export function createPackageController(options: {
  clipId: string | null; epoch: string; name: string; getSaveSession(): CopySaveSession | null; origin: string;
  api?: { save: typeof packageEditor; import: typeof importEditorPackage }; onState?(state: PackageState): void;
}) {
  let state = initialPackageState(), stamp: CopyStamp | null = null, disposed = false;
  let importAbort: AbortController | null = null;
  const api = options.api ?? { save: packageEditor, import: importEditorPackage };
  const publish = (patch: Partial<PackageState>) => { if (!disposed) { state = { ...state, ...patch }; options.onState?.({ ...state }); } };
  const failure = (error: unknown) => {
    const value = error as { status?: number; message?: string };
    publish({ busy: false, error: state.importProgress?.phase === 'importing' && !value.status
      ? `导入结果未确认，请先到素材页核对，勿重复导入。${value.message || String(error)}`
      : (error as { name?: string }).name === 'AbortError' ? '已取消上传工程包。' : value.status === 409 ? '工程修订或纪元已变化，请重新载入后再打包。'
      : value.message === 'stale_engine_revision' ? '编辑内容已变化，请重新选择保存工程包。' : value.message || String(error) });
    return false;
  };
  return {
    getState: () => ({ ...state }),
    openSave(expected: CopyStamp) { if (!disposed && !state.busy) { stamp = { ...expected }; publish({ ...initialPackageState(), open: true, mode: 'save', name: options.name }); } },
    openImport() { if (!disposed && !state.busy) publish({ ...initialPackageState(), open: true }); },
    setName(name: string) { if (!state.busy) publish({ name }); },
    close() { if (!state.busy) publish({ open: false }); },
    async save() {
      if (disposed || state.busy || !stamp) return false;
      publish({ busy: true, error: null, download: null });
      try {
        const session = options.getSaveSession();
        if (!session || !options.clipId) throw new Error('请先打开工程。');
        const saved = session.readState();
        if (saved.saving || saved.conflict || saved.revision === null || !options.epoch) throw new Error('工程正在保存、存在冲突或缺少修订，请稍后重试。');
        if (!state.name.trim()) throw new Error('请输入工程包名称。');
        const projection = session.projectCurrentSnapshot(stamp);
        if (!projection.ok) throw new Error('当前编辑内容无法完整映射，未生成工程包。');
        const result = await api.save(options.clipId, state.name.trim(), projection.project, saved.revision, options.epoch);
        const downloadUrl = safePackageDownload(result.downloadUrl, options.origin);
        publish({ busy: false, download: { ...result, downloadUrl } }); return true;
      } catch (error) { return failure(error); }
    },
    async import(file: File) {
      if (disposed || state.busy) return false;
      if (file.size >= 16 * 1024 ** 3) return failure(new Error('工程包须小于 16 GiB。'));
      if (/\.rar$/i.test(file.name)) return failure(new Error('暂不支持 RAR，请选择 .lcproj 或含工程清单的 ZIP。'));
      if (!/\.(lcproj|zip)$/i.test(file.name)) return failure(new Error('请选择 .lcproj 或 ZIP 工程包。'));
      importAbort = new AbortController();
      publish({ busy: true, error: null, imported: null, importProgress: { uploaded: 0, total: file.size, phase: 'uploading' } });
      try {
        const imported = await api.import(file, { signal: importAbort.signal, onProgress: importProgress => publish({ importProgress }) });
        publish({ busy: false, imported }); return true;
      } catch (error) { return failure(error); }
      finally { importAbort = null; }
    },
    cancelImport() { if (state.importProgress?.phase === 'uploading') importAbort?.abort(); },
    dispose() { disposed = true; if (state.importProgress?.phase === 'uploading') importAbort?.abort(); },
  };
}
