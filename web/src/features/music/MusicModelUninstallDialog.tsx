import { useCallback, useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import {
  MusicModelRemovalError,
  previewMusicModelUninstall,
  uninstallMusicModel,
  type MusicModelRemovalPreview,
  type MusicStatus,
} from '../../api/music';
import { errorMessage, formatBytes } from './utils';
import './MusicModelUninstallDialog.css';

/*
 * 设置页与音乐页共用的「卸载本地音乐模型」流程（专用组件，不是通用确认框架）。
 * 挂载即发一次只读 preview；只有用户点「确认卸载」才 POST，且带的是当前 preview 的指纹。
 * 不会修改默认模型 / 允许下载等 preferences，不会自动重试。
 */

type Phase =
  | { kind: 'previewing' }
  | { kind: 'preview'; preview: MusicModelRemovalPreview }
  | { kind: 'previewFailed'; message: string }
  | { kind: 'removing'; preview: MusicModelRemovalPreview }
  | { kind: 'done'; tone: 'ok' | 'info' | 'error'; title: string; lines: string[] };

export interface MusicModelUninstallDialogProps {
  model: { id: string; name: string };
  /** 重新读取真实 status 并更新父层；失败返回 null。结果用于复查，不会被当作成功依据之外的事实。 */
  onRefreshStatus: () => Promise<MusicStatus | null>;
  /** message=字符串：卸载未完成，目录可能残留（父层据此不提供一键下载覆盖）；null=已确认不再残留。 */
  onResidualChange?: (modelId: string, message: string | null) => void;
  onClose: () => void;
}

function bytesText(bytes: number): string {
  return bytes > 0 ? `${formatBytes(bytes)}（${bytes.toLocaleString()} 字节）` : '0 字节';
}

export function MusicModelUninstallDialog({
  model,
  onRefreshStatus,
  onResidualChange,
  onClose,
}: MusicModelUninstallDialogProps) {
  const [phase, setPhase] = useState<Phase>({ kind: 'previewing' });
  const dialogRef = useRef<HTMLDialogElement>(null);
  const aliveRef = useRef(true);
  const seqRef = useRef(0);
  const removingRef = useRef(false);
  // 父层传内联函数：放进 ref，避免回调变化触发重新预览。
  const residualRef = useRef(onResidualChange);
  residualRef.current = onResidualChange;
  const refreshRef = useRef(onRefreshStatus);
  refreshRef.current = onRefreshStatus;

  useEffect(() => {
    const dialog = dialogRef.current;
    if (dialog && !dialog.open) dialog.showModal();
  }, []);

  const startPreview = useCallback(async () => {
    const seq = ++seqRef.current;
    setPhase({ kind: 'previewing' });
    try {
      const preview = await previewMusicModelUninstall(model.id);
      if (!aliveRef.current || seq !== seqRef.current) return;
      // 目录读取为不存在且未安装：此前残留的「待处理」标记可以清除。
      if (preview.notInstalled && !preview.installed) residualRef.current?.(model.id, null);
      setPhase({ kind: 'preview', preview });
    } catch (error) {
      if (!aliveRef.current || seq !== seqRef.current) return;
      setPhase({ kind: 'previewFailed', message: errorMessage(error) });
    }
  }, [model.id]);

  useEffect(() => {
    aliveRef.current = true;
    void startPreview();
    return () => {
      aliveRef.current = false;
      seqRef.current += 1;
    };
  }, [startPreview]);

  const confirmRemove = async (preview: MusicModelRemovalPreview) => {
    if (removingRef.current) return;
    if (!preview.canRemove || !preview.identityFingerprint) return;
    removingRef.current = true;
    setPhase({ kind: 'removing', preview });

    let result: Awaited<ReturnType<typeof uninstallMusicModel>> | null = null;
    let failure: unknown = null;
    try {
      result = await uninstallMusicModel(model.id, preview.identityFingerprint);
    } catch (error) {
      failure = error;
    }
    // 无论结果如何都重新读取真实状态；按钮/动画只跟随这次读取。
    const fresh = await refreshRef.current().catch(() => null);
    const freshModel = fresh?.models.find((item) => item.id === model.id) ?? null;
    const freshLine = !fresh
      ? '重新读取模型状态失败，当前显示的状态可能不是最新，请手动刷新。'
      : !freshModel
        ? '重新读取的状态里找不到该模型。'
        : freshModel.installed
          ? '重新读取的真实状态：仍显示「已安装」。'
          : '重新读取的真实状态：显示「未安装」。';

    removingRef.current = false;
    if (!aliveRef.current) return;

    if (failure !== null) {
      if (failure instanceof MusicModelRemovalError) {
        const lines = [failure.message];
        if (failure.removalStatus === 'partial' || failure.removalStatus === 'unknown') {
          lines.push(
            `后端结果：${failure.removalStatus === 'partial' ? '部分删除（有内容残留）' : '无法确认删除结果'}；` +
              `目录${failure.directoryExists === null ? '状态未读到' : failure.directoryExists ? '仍存在' : '读取为不存在'}，` +
              `安装标记${failure.installed === null ? '未读到' : failure.installed ? '仍为已安装' : '为未安装'}。`,
          );
          lines.push('不会自动重试，也不会改动模型选择或设置；请检查后再重新预览。');
          onResidualChange?.(model.id, failure.message);
        } else if (failure.status === 409) {
          lines.push('后端拒绝了这次卸载（忙碌 / 目录已改变 / 不可卸载），没有删除任何内容。请重新预览。');
        } else if (failure.status === 400) {
          lines.push('请求被拒绝，没有删除任何内容。');
        }
        lines.push(freshLine);
        setPhase({ kind: 'done', tone: 'error', title: '卸载未成功', lines });
      } else {
        // 没拿到后端响应：删除可能已发生也可能没有，只能以重读状态为准。
        setPhase({
          kind: 'done',
          tone: 'error',
          title: '卸载结果未确认',
          lines: [errorMessage(failure), '没有收到后端的确认，不能断定已删除或未删除。', freshLine],
        });
      }
      return;
    }

    if (result?.status === 'removed') {
      const consistent = result.directoryExists === false && result.installed === false;
      if (!consistent) {
        setPhase({
          kind: 'done',
          tone: 'error',
          title: '后端返回已移除，但状态字段不一致',
          lines: [
            `目录${result.directoryExists ? '仍存在' : '不存在'}，安装标记${result.installed ? '仍为已安装' : '为未安装'}。`,
            freshLine,
          ],
        });
        return;
      }
      if (!fresh || !freshModel || freshModel.installed) {
        setPhase({
          kind: 'done',
          tone: 'error',
          title: '后端返回已移除，但复查未能确认',
          lines: [freshLine],
        });
        return;
      }
      onResidualChange?.(model.id, null);
      setPhase({
        kind: 'done',
        tone: 'ok',
        title: `已卸载 ${model.name}`,
        lines: [
          `后端确认目录已不存在、模型为未安装，并已重新读取状态核对（移除约 ${bytesText(result.removedBytes)}，${result.removedFiles} 个文件）。`,
          '当前选中的模型和设置没有被改动。',
        ],
      });
      return;
    }

    if (result?.status === 'notInstalled') {
      onResidualChange?.(model.id, null);
      setPhase({
        kind: 'done',
        tone: 'info',
        title: '该模型原本就不在',
        lines: [
          '后端确认模型目录不存在，本次没有删除任何内容。',
          ...(result.installed ? ['但安装标记仍报告已安装，状态不一致，需要排查。'] : []),
          freshLine,
        ],
      });
      return;
    }

    setPhase({
      kind: 'done',
      tone: 'error',
      title: '后端返回了无法识别的结果',
      lines: [`status=${String(result?.status)}，不当作成功。`, freshLine],
    });
  };

  const removing = phase.kind === 'removing';
  const close = () => {
    if (!removing) onClose();
  };

  const body = (() => {
    switch (phase.kind) {
      case 'previewing':
        return <p className="mmu__note" role="status">正在读取卸载预览（只读，不会删除任何内容）…</p>;
      case 'previewFailed':
        return (
          <>
            <p className="mmu__error" role="alert">未能读取卸载预览：{phase.message}</p>
            <div className="mmu__actions">
              <button type="button" className="mmu__btn" onClick={() => void startPreview()}>重新预览</button>
              <button type="button" className="mmu__btn mmu__btn--ghost" onClick={close}>关闭</button>
            </div>
          </>
        );
      case 'preview':
      case 'removing': {
        const { preview } = phase;
        const ready = preview.canRemove && preview.identityFingerprint !== '';
        return (
          <>
            {ready ? (
              <>
                <dl className="mmu__facts">
                  <dt>模型</dt>
                  <dd>{model.name}<code>{model.id}</code></dd>
                  <dt>估计可清理</dt>
                  <dd>
                    {bytesText(preview.actualBytes)}，{preview.fileCount} 个文件
                    <span className="mmu__muted">这是现在读到的目录大小；实际释放的磁盘空间以删除后为准。</span>
                  </dd>
                  <dt>删除范围</dt>
                  <dd>
                    仅此模型在应用管理的模型目录里的内容。不包含已生成的作品 / 音频库、Python 虚拟环境，也不动共享模型或其他模型。
                  </dd>
                </dl>
                <p className="mmu__note">
                  卸载后该模型需要重新下载才能使用。不会改动默认模型、「允许下载」或其它设置。
                </p>
              </>
            ) : (
              <>
                <p className="mmu__error" role="alert">
                  {preview.notInstalled ? '该模型目前不存在，没有可卸载的内容。' : '现在不能卸载这个模型。'}
                </p>
                <p className="mmu__note">原因：{preview.reason || '后端未给出原因'}</p>
                {preview.downloadBusy || preview.localModelBusy ? (
                  <p className="mmu__muted">
                    {preview.downloadBusy ? '下载任务占用中。' : ''}
                    {preview.localModelBusy ? '本地生成任务占用中。' : ''}
                    等后台任务结束后可重新预览。
                  </p>
                ) : null}
              </>
            )}
            <div className="mmu__actions">
              {ready ? (
                <button
                  type="button"
                  className="mmu__btn mmu__btn--danger"
                  disabled={removing}
                  onClick={() => void confirmRemove(preview)}
                >
                  {removing ? '卸载中…' : '确认卸载'}
                </button>
              ) : (
                <button type="button" className="mmu__btn" onClick={() => void startPreview()}>重新预览</button>
              )}
              <button type="button" className="mmu__btn mmu__btn--ghost" disabled={removing} onClick={close}>
                {ready ? '取消' : '关闭'}
              </button>
            </div>
            {removing ? (
              <p className="mmu__muted" role="status">正在删除，请不要关闭此窗口；结果以后端返回和重新读取的状态为准。</p>
            ) : null}
          </>
        );
      }
      case 'done':
        return (
          <>
            <p className={phase.tone === 'error' ? 'mmu__error' : 'mmu__ok'} role={phase.tone === 'error' ? 'alert' : 'status'}>
              <strong>{phase.title}</strong>
            </p>
            {phase.lines.map((line) => (
              <p key={line} className="mmu__note">{line}</p>
            ))}
            <div className="mmu__actions">
              {phase.tone === 'error' ? (
                <button type="button" className="mmu__btn" onClick={() => void startPreview()}>重新预览</button>
              ) : null}
              <button type="button" className="mmu__btn mmu__btn--ghost" onClick={close}>关闭</button>
            </div>
          </>
        );
    }
  })();

  return createPortal(
    <dialog
      ref={dialogRef}
      className="mmu"
      aria-label={`卸载 ${model.name}`}
      onClose={close}
      onCancel={(event) => {
        if (removing) event.preventDefault();
        else onClose();
      }}
    >
      <h4 className="mmu__title">卸载 {model.name}</h4>
      {body}
    </dialog>,
    document.body,
  );
}
