import { MdOutlineDescription } from 'react-icons/md';
import { SiGithub } from 'react-icons/si';
import { GrUpdate } from 'react-icons/gr';
import DropdownSelect from '../DropdownSelect';
import { Settings as SettingsType } from '../../Models/types';
import { sendMessageToBackend } from '../../Utils/MessageUtils';
import Button from '../Button';

interface AdvancedSectionProps {
  settings: SettingsType;
  updateSettings: (updates: Partial<SettingsType>) => void;
  openReleaseNotesModal: (version: string | null) => void;
  checkForUpdates: () => void;
}

export default function AdvancedSection({
  settings,
  updateSettings,
  openReleaseNotesModal,
  checkForUpdates,
}: AdvancedSectionProps) {
  return (
    <>
      {/* Advanced Settings */}
      <div className="p-4 bg-base-300 rounded-lg shadow-md border border-custom">
        <h2 className="text-xl font-semibold mb-4">Advanced Settings</h2>
        <div className="bg-base-200 p-4 rounded-lg space-y-4 border border-custom">
          <div className="flex flex-col gap-3">
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
                    className="gap-2"
                    onClick={() => checkForUpdates()}
                    loading={settings.state.isCheckingForUpdates}
                  >
                    {!settings.state.isCheckingForUpdates && (
                      <GrUpdate size={16} className="shrink-0" />
                    )}
                    <span className="inline-block">Check for Updates</span>
                  </Button>
                </div>
              </div>
            </div>
            <div className="flex items-center">
              <Button
                variant="primary"
                size="sm"
                className="w-40"
                onClick={() => openReleaseNotesModal(null)}
              >
                <SiGithub size={16} aria-hidden="true" />
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
                    ...settings.state.availableOBSVersions
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
            <MdOutlineDescription className="w-4 h-4 shrink-0" aria-hidden="true" />
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
