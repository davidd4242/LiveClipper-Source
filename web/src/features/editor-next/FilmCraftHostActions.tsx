export type FilmCraftHostAction =
  | 'transcript'
  | 'analysis'
  | 'events'
  | 'rough-cut'
  | 'music'
  | 'export'
  | 'save'
  | 'notices';

export interface FilmCraftHostActionsProps {
  onAction: (action: FilmCraftHostAction) => void;
  disabled?: boolean;
  exportUnavailable?: string;
  active?: FilmCraftHostAction | null;
  saveLabel?: string;
}

const HOST_ACTIONS: ReadonlyArray<{
  action: FilmCraftHostAction;
  label: string;
}> = [
  { action: 'export', label: '导出' },
  { action: 'save', label: '保存' },
  { action: 'notices', label: '提示' },
];

export function FilmCraftHostActions(props: FilmCraftHostActionsProps) {
  return (
    <div className="fc-host-actions" role="group" aria-label="剪辑操作">
      {HOST_ACTIONS.map(({ action, label }) => {
        const isActive = props.active === action;
        const title =
          action === 'save' && props.saveLabel ? props.saveLabel : undefined;
        return (
          <button
            key={action}
            type="button"
            className={isActive ? 'fc-host-action is-active' : 'fc-host-action'}
            aria-pressed={isActive}
            aria-label={action === 'save' ? props.saveLabel || label : label}
            disabled={props.disabled === true || (action === 'export' && !!props.exportUnavailable)}
            title={action === 'export' ? props.exportUnavailable : title}
            onClick={() => props.onAction(action)}
          >
            {action === 'save' ? props.saveLabel || label : label}
          </button>
        );
      })}
    </div>
  );
}
