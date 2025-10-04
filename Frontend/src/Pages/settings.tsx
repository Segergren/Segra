import { useSettings, useSettingsUpdater } from '../Context/SettingsContext';
import { useUpdate } from '../Context/UpdateContext';
import { useAuth } from '../Hooks/useAuth';
import AccountSection from '../Components/Settings/AccountSection';
import SegraAISection from '../Components/Settings/SegraAISection';
import CaptureModeSection from '../Components/Settings/CaptureModeSection';
import VideoSettingsSection from '../Components/Settings/VideoSettingsSection';
import StorageSettingsSection from '../Components/Settings/StorageSettingsSection';
import ClipSettingsSection from '../Components/Settings/ClipSettingsSection';
import AudioDevicesSection from '../Components/Settings/AudioDevicesSection';
import KeybindingsSection from '../Components/Settings/KeybindingsSection';
import GameDetectionSection from '../Components/Settings/GameDetectionSection';
import UISettingsSection from '../Components/Settings/UISettingsSection';

export default function Settings() {
  const { session } = useAuth();
  const { openReleaseNotesModal, checkForUpdates } = useUpdate();
  const settings = useSettings();
  const updateSettings = useSettingsUpdater();

  return (
    <div className="p-5 space-y-6 bg-base-200 dark:bg-base-300">
      <h1 className="text-3xl font-bold">Settings</h1>

      <AccountSection />

      <SegraAISection session={session} settings={settings} updateSettings={updateSettings} />

      <CaptureModeSection settings={settings} updateSettings={updateSettings} />

      <VideoSettingsSection settings={settings} updateSettings={updateSettings} />

      <StorageSettingsSection settings={settings} updateSettings={updateSettings} />

      <ClipSettingsSection settings={settings} updateSettings={updateSettings} />

      <AudioDevicesSection settings={settings} updateSettings={updateSettings} />

      <KeybindingsSection settings={settings} updateSettings={updateSettings} />

      <GameDetectionSection />

      <UISettingsSection
        settings={settings}
        updateSettings={updateSettings}
        openReleaseNotesModal={openReleaseNotesModal}
        checkForUpdates={checkForUpdates}
      />
    </div>
  );
}