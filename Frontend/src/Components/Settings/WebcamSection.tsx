import { useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import DropdownSelect from '../DropdownSelect';
import { Settings as SettingsType } from '../../Models/types';
import { useAppState } from '../../Context/AppStateContext';
import { sendMessageToBackend } from '../../Utils/MessageUtils';
import {
  usePendingRecordingSettings,
  RECORDING_SETTING_GROUPS,
} from '../../Hooks/usePendingRecordingSettings';

interface WebcamSectionProps {
  settings: SettingsType;
  updateSettings: (updates: Partial<SettingsType>) => void;
}

export default function WebcamSection({ settings, updateSettings }: WebcamSectionProps) {
  const { t } = useTranslation('recording');
  const appState = useAppState();
  const hasPendingChanges = usePendingRecordingSettings(RECORDING_SETTING_GROUPS.webcam);
  const webcams = appState.webcams;

  useEffect(() => {
    sendMessageToBackend('RefreshWebcams');
  }, []);

  if (appState.platform === 'linux') return null;

  const selectedMissing =
    settings.webcamDeviceId != null && !webcams.some((w) => w.deviceId === settings.webcamDeviceId);

  const setEnabled = (enabled: boolean) => {
    const deviceId = settings.webcamDeviceId ?? webcams[0]?.deviceId ?? null;
    updateSettings({ webcamEnabled: enabled, webcamDeviceId: deviceId });
  };

  return (
    <div className="p-4 bg-base-300 rounded-lg shadow-md border border-base-400">
      <div className="flex items-center gap-2 mb-4">
        <h2 className="text-xl font-semibold">{t('webcam.title')}</h2>
        {hasPendingChanges && <span className="text-xs text-warning">{t('pendingChanges')}</span>}
      </div>

      <label className="flex items-center gap-3 cursor-pointer p-3 bg-base-200 rounded-lg border border-base-400">
        <input
          type="checkbox"
          className="checkbox checkbox-primary checkbox-sm"
          checked={settings.webcamEnabled}
          onChange={(e) => setEnabled(e.target.checked)}
        />
        <div>
          <div className="flex items-center gap-2 font-semibold">
            {t('webcam.enable.label')}
            <span className="badge badge-primary badge-xs text-base-300 font-bold">
              {t('webcam.enable.badge')}
            </span>
          </div>
          <div className="text-sm opacity-70 mt-0.5">{t('webcam.enable.description')}</div>
        </div>
      </label>

      {settings.webcamEnabled && (
        <div className="mt-4 form-control max-w-sm">
          <label className="label">
            <span className="label-text text-base-content">{t('webcam.camera.label')}</span>
          </label>
          <DropdownSelect
            items={[
              ...webcams.map((w) => ({ value: w.deviceId, label: w.name })),
              ...(selectedMissing
                ? [{ value: settings.webcamDeviceId!, label: t('webcam.camera.disconnected') }]
                : []),
            ]}
            value={settings.webcamDeviceId ?? undefined}
            onChange={(value) => updateSettings({ webcamDeviceId: value })}
            placeholder={webcams.length === 0 ? t('webcam.camera.none') : t('webcam.camera.select')}
            disabled={webcams.length === 0 && !selectedMissing}
          />
        </div>
      )}
    </div>
  );
}
