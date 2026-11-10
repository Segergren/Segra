import { KeyboardEvent, useEffect, useState } from 'react';
import { Content, Settings as SettingsType } from '../../Models/types';
import { useModal } from '../../Context/ModalContext';
import ConfirmationModal from '../ConfirmationModal';
import { clampInt } from '../../Utils/NumberUtils';

const DAY_MS = 24 * 60 * 60 * 1000;

interface Rule {
  enabled: boolean;
  days: number;
  includeClips: boolean;
}

const formatSize = (kb: number) => {
  const gb = kb / (1024 * 1024);
  return gb >= 1 ? `${gb.toFixed(2)} GB` : `${Math.max(1, Math.round(kb / 1024))} MB`;
};

// Mirrors StorageService.DeleteExpiredContent
const findExpired = (content: Content[], rule: Rule) => {
  if (!rule.enabled) return [];
  const cutoff = Date.now() - rule.days * DAY_MS;
  return content.filter((c) => {
    if (c.isImported) return false;
    if (!rule.includeClips && c.type !== 'Session' && c.type !== 'Buffer') return false;
    const created = Date.parse(c.createdAt);
    return !isNaN(created) && created < cutoff;
  });
};

interface AutoDeleteProps {
  settings: SettingsType;
  content: Content[];
  updateSettings: (updates: Partial<SettingsType>) => void;
}

// Applies a change, asking first when it deletes videos right away
function useApplyAutoDelete({ settings, content, updateSettings }: AutoDeleteProps) {
  const { openModal, closeModal } = useModal();

  const current: Rule = {
    enabled: settings.autoDeleteOldContent,
    days: settings.autoDeleteAfterDays,
    includeClips: settings.autoDeleteClipsAndHighlights,
  };

  return (changes: Partial<Rule>, updates: Partial<SettingsType>, onCancel?: () => void) => {
    const next = { ...current, ...changes };
    const alreadyExpired = new Set(findExpired(content, current).map((c) => c.id));
    const newlyExpired = findExpired(content, next).filter((c) => !alreadyExpired.has(c.id));

    if (newlyExpired.length === 0) {
      updateSettings(updates);
      return;
    }

    const count = newlyExpired.length;
    const size = formatSize(newlyExpired.reduce((sum, c) => sum + (c.fileSizeKb || 0), 0));
    const onlyClips = newlyExpired.every((c) => c.type === 'Clip' || c.type === 'Highlight');
    const noun = onlyClips ? 'clips and highlights' : 'videos';
    openModal(
      <ConfirmationModal
        title="Delete Old Videos"
        description={`${count} ${noun} (${size}) ${count === 1 ? 'is' : 'are'} older than ${next.days} day${next.days === 1 ? '' : 's'} and will be deleted now.\n\nAre you sure you want to continue?`}
        confirmText={count === 1 ? 'Delete Video' : `Delete ${count} Videos`}
        cancelText="Cancel"
        onConfirm={() => {
          updateSettings(updates);
          closeModal();
        }}
        onCancel={() => {
          onCancel?.();
          closeModal();
        }}
      />,
    );
  };
}

export function AutoDeleteField(props: AutoDeleteProps) {
  const { settings } = props;
  const apply = useApplyAutoDelete(props);
  const [localDays, setLocalDays] = useState(String(settings.autoDeleteAfterDays));
  const enabled = settings.autoDeleteOldContent;

  useEffect(() => {
    setLocalDays(String(settings.autoDeleteAfterDays));
  }, [settings.autoDeleteAfterDays]);

  const commitDays = () => {
    const days = clampInt(localDays, 1, 36500, settings.autoDeleteAfterDays);
    setLocalDays(String(days));
    if (days === settings.autoDeleteAfterDays) return;
    apply({ days }, { autoDeleteAfterDays: days }, () =>
      setLocalDays(String(settings.autoDeleteAfterDays)),
    );
  };

  const blurOnEnter = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Enter') e.currentTarget.blur();
  };

  return (
    <div className="form-control">
      <label className="label block px-0">
        <span className="label-text text-base-content">Delete Videos After</span>
      </label>

      <div className="join w-full">
        <div className="relative flex-1">
          <input
            type="number"
            name="autoDeleteAfterDays"
            value={enabled ? localDays : ''}
            onChange={(e) => setLocalDays(e.target.value)}
            onBlur={commitDays}
            onKeyDown={blurOnEnter}
            disabled={!enabled}
            placeholder={enabled ? 'Set age in days' : 'Never'}
            min="1"
            className="input input-bordered join-item bg-base-200 w-full block pr-14 outline-none focus:border-base-400 disabled:bg-base-200 disabled:border-base-400 disabled:placeholder:opacity-100 disabled:placeholder:text-base-content/50"
          />
          {enabled && (
            <span className="absolute inset-y-0 right-3 flex items-center text-sm opacity-60 pointer-events-none">
              {Number(localDays) === 1 ? 'day' : 'days'}
            </span>
          )}
        </div>
        <label className="btn btn-secondary bg-base-200 hover:bg-base-300 border-base-400 hover:border-base-400 font-semibold join-item gap-2 relative">
          <input
            type="checkbox"
            name="autoDeleteNever"
            checked={!enabled}
            onChange={(e) =>
              apply({ enabled: !e.target.checked }, { autoDeleteOldContent: !e.target.checked })
            }
            className="checkbox checkbox-primary checkbox-sm"
          />
          Never
        </label>
      </div>
      <div className="mt-1 px-1 text-xs opacity-70 leading-snug">
        {!enabled
          ? 'Videos are kept until you delete them or the storage limit is reached.'
          : settings.autoDeleteClipsAndHighlights
            ? 'Videos older than this are deleted. Imported videos are kept.'
            : 'Sessions and replay buffers older than this are deleted.'}
      </div>
    </div>
  );
}

export function AutoDeleteClipsToggle(props: AutoDeleteProps) {
  const { settings } = props;
  const apply = useApplyAutoDelete(props);
  const enabled = settings.autoDeleteOldContent;

  return (
    <label
      className={`flex items-center gap-3 p-3 bg-base-200 rounded-lg border border-base-400 ${enabled ? 'cursor-pointer' : 'opacity-50 cursor-not-allowed'}`}
    >
      <input
        type="checkbox"
        name="autoDeleteClipsAndHighlights"
        checked={settings.autoDeleteClipsAndHighlights}
        disabled={!enabled}
        onChange={(e) =>
          apply(
            { includeClips: e.target.checked },
            { autoDeleteClipsAndHighlights: e.target.checked },
          )
        }
        className="checkbox checkbox-primary checkbox-sm"
      />
      <div>
        <div className="font-semibold">Delete Old Clips and Highlights</div>
        <div className="text-sm opacity-70 mt-0.5">
          Also deletes clips and highlights once they are older than the Delete Videos After age.
        </div>
      </div>
    </label>
  );
}
