import { useEffect, useRef, useState } from 'react';
import { AnimatePresence, motion } from 'framer-motion';
import { FileText, RefreshCw, Plane } from 'lucide-react';
import { GithubIcon } from '../icons/BrandIcons';
import DropdownSelect from '../DropdownSelect';
import { Settings as SettingsType } from '../../Models/types';
import { sendMessageToBackend } from '../../Utils/MessageUtils';
import Button from '../Button';
import { useAppState } from '../../Context/AppStateContext';

interface AdvancedSectionProps {
  settings: SettingsType;
  updateSettings: (updates: Partial<SettingsType>) => void;
  openReleaseNotesModal: (version: string | null) => void;
  checkForUpdates: () => void;
  // False on Linux (Flatpak) — show update guidance instead of the in-app updater controls.
  canSelfUpdate: boolean;
}

export default function AdvancedSection({
  settings,
  updateSettings,
  openReleaseNotesModal,
  checkForUpdates,
  canSelfUpdate,
}: AdvancedSectionProps) {
  const appState = useAppState();
  const rowRef = useRef<HTMLDivElement>(null);
  const contentRef = useRef<HTMLSpanElement>(null);
  const [flyDistance, setFlyDistance] = useState(240);

  // Fly the plane from its spot to the right edge of the Airplane Mode row.
  useEffect(() => {
    const row = rowRef.current;
    if (!row) return;
    const measure = () => {
      const content = contentRef.current;
      if (!content) return;
      const distance = row.getBoundingClientRect().right - content.getBoundingClientRect().right;
      if (distance > 0) setFlyDistance(distance);
    };
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(row);
    return () => observer.disconnect();
  }, []);

  return (
    <>
      <div className="bg-base-300 p-4 rounded-lg space-y-4 border border-custom">
        <div className="flex flex-col gap-3">
          {canSelfUpdate ? (
            <div className="flex items-center justify-between">
              <div className="flex flex-col">
                <div className="mb-1">
                  <span className="text-base-content">Update Channel</span>
                </div>
                <div className="flex items-center gap-3">
                  <div className="w-40">
                    <DropdownSelect
                      size="sm"
                      items={[
                        { value: 'stable', label: 'Stable' },
                        { value: 'beta', label: 'Beta' },
                      ]}
                      value={settings.receiveBetaUpdates ? 'beta' : 'stable'}
                      onChange={(val) => updateSettings({ receiveBetaUpdates: val === 'beta' })}
                    />
                  </div>
                  <Button
                    variant="primary"
                    size="sm"
                    className="gap-2 bg-base-200 hover:bg-base-300"
                    onClick={() => checkForUpdates()}
                    loading={appState.isCheckingForUpdates}
                  >
                    {!appState.isCheckingForUpdates && <RefreshCw size={16} className="shrink-0" />}
                    <span className="inline-block">Check for Updates</span>
                  </Button>
                </div>
              </div>
            </div>
          ) : (
            <div className="flex flex-col">
              <div className="mb-1">
                <span className="text-base-content">Updates</span>
              </div>
              <p className="text-sm text-gray-400 max-w-md">
                Segra updates automatically through Flatpak. To update now, run{' '}
                <code className="px-1 py-0.5 rounded bg-base-200 text-gray-300">
                  flatpak update tv.segra.Segra
                </code>{' '}
                or use your software center.
              </p>
            </div>
          )}
          <div className="flex items-center">
            <Button
              variant="primary"
              size="sm"
              className="w-40 bg-base-200 hover:bg-base-300"
              onClick={() => openReleaseNotesModal(null)}
            >
              <GithubIcon size={16} aria-hidden="true" />
              <span className="inline-block">View Release Notes</span>
            </Button>
          </div>
        </div>

        {/* OBS Version Selection */}
        <div className="flex items-center justify-between">
          <div className="flex flex-col">
            <div className="mb-1">
              <span className="text-base-content">OBS Version</span>
            </div>
            <div className="w-40">
              <DropdownSelect
                size="sm"
                items={[
                  { value: '', label: 'Automatic' },
                  ...[...appState.availableOBSVersions]
                    .sort((a, b) => {
                      return b.version.localeCompare(a.version, undefined, { numeric: true });
                    })
                    .map((v) => ({
                      value: v.version,
                      label: `${v.version}${v.isBeta ? ' (Beta)' : ''}`,
                    })),
                ]}
                value={settings.selectedOBSVersion || ''}
                onChange={(val) => updateSettings({ selectedOBSVersion: val || null })}
              />
            </div>
          </div>
        </div>

        {/* Airplane Mode */}
        <div ref={rowRef} className="pt-4 border-t border-custom">
          <label className="flex items-center gap-2">
            <input
              type="checkbox"
              name="airplaneMode"
              checked={settings.airplaneMode}
              onChange={(e) => updateSettings({ airplaneMode: e.target.checked })}
              className="toggle toggle-primary toggle-sm"
            />
            <span ref={contentRef} className="inline-flex items-center gap-1.5 cursor-pointer">
              Airplane Mode
              <AnimatePresence initial={false}>
                {settings.airplaneMode && (
                  <motion.span
                    className="inline-flex"
                    initial={{ opacity: 0, x: -8, rotate: 45 }}
                    animate={{ opacity: 1, x: 0, rotate: 45 }}
                    exit={{
                      opacity: 0,
                      x: flyDistance,
                      rotate: 45,
                      transition: {
                        x: { duration: 1.9, ease: [0.2, 0, 0.8, 0] },
                        opacity: { duration: 0.9, ease: 'easeIn', delay: 1.0 },
                      },
                    }}
                    transition={{ duration: 0.35, ease: 'easeOut' }}
                  >
                    <Plane size={16} />
                  </motion.span>
                )}
              </AnimatePresence>
            </span>
          </label>
        </div>
      </div>

      {/* Version */}
      <div className="text-center mt-4 text-sm text-gray-500">
        <div className="flex flex-col items-center gap-2">
          <Button
            variant="primary"
            size="sm"
            onClick={() => sendMessageToBackend('OpenLogsLocation')}
          >
            <FileText className="w-4 h-4 shrink-0" aria-hidden="true" />
            <span className="leading-none">View Logs</span>
          </Button>
          <div>
            Segra{' '}
            {__APP_VERSION__ === 'Developer Preview' ? __APP_VERSION__ : 'v' + __APP_VERSION__}
          </div>
        </div>
      </div>
    </>
  );
}
