import { useState, useEffect } from 'react';
import { motion, AnimatePresence } from 'framer-motion';
import {
  TriangleAlert,
  X,
  CircleAlert,
  Volume2,
  Gamepad2,
  AudioWaveform,
  Merge,
} from 'lucide-react';
import { DiscordIcon, TeamSpeakIcon } from '../icons/BrandIcons';
import Button from '../Button';
import RangeSlider from '../RangeSlider';
import {
  Settings as SettingsType,
  AudioDevice,
  AudioOutputMode,
  DeviceSetting,
} from '../../Models/types';
import { useAppState } from '../../Context/AppStateContext';
import {
  usePendingRecordingSettings,
  RECORDING_SETTING_GROUPS,
} from '../../Hooks/usePendingRecordingSettings';

interface AudioDevicesSectionProps {
  settings: SettingsType;
  updateSettings: (updates: Partial<SettingsType>) => void;
}

export default function AudioDevicesSection({
  settings,
  updateSettings,
}: AudioDevicesSectionProps) {
  const appState = useAppState();
  const hasPendingChanges = usePendingRecordingSettings(RECORDING_SETTING_GROUPS.audio);
  const [draggingVolume, setDraggingVolume] = useState<{
    deviceId: string | null;
    deviceType: 'input' | 'output' | null;
    volume: number | null;
  }>({ deviceId: null, deviceType: null, volume: null });

  // Helper function to check if the selected device is available
  const isDeviceAvailable = (deviceId: string, devices: AudioDevice[]) => {
    if (deviceId === 'default') return true;
    return devices.some((device) => device.id === deviceId);
  };

  // Multi-track audio: Track 1 is the Full Mix, the rest are isolated per source.
  // The ceiling comes from the loaded OBS build (6 in stock OBS, higher in patched bundles).
  // In GameOnly/GameAndDiscord modes, game recordings use Game Audio (+ one shared Voice Chat
  // track) instead of the output devices, which then only apply to manual recordings.
  const selectedInputIds = settings.inputDevices.map((d) => d.id);
  const implicitOutputCount =
    settings.audioOutputMode === 'GameAndDiscord'
      ? 2
      : settings.audioOutputMode === 'GameOnly'
        ? 1
        : 0;
  const selectedOutputIds = settings.outputDevices.map((d) => d.id);
  const combinedSelectedIds = [...selectedInputIds, ...selectedOutputIds];
  const totalSourceCount = combinedSelectedIds.length + implicitOutputCount;
  const maxIsolatedTracks = Math.max(1, appState.maxAudioTracks - 1);
  const hasOverTrackLimit =
    settings.enableSeparateAudioTracks && totalSourceCount > maxIsolatedTracks;
  const selectionSig = combinedSelectedIds.join(',');

  // Dismissible warning for track limit exceeded
  const [trackLimitWarnDismissed, setTrackLimitWarnDismissed] = useState<boolean>(false);

  useEffect(() => {
    const storedSig = localStorage.getItem('segra.trackLimitWarnDismissedSig');
    if (hasOverTrackLimit) {
      setTrackLimitWarnDismissed(storedSig === selectionSig);
    } else {
      setTrackLimitWarnDismissed(false);
    }
  }, [selectionSig, hasOverTrackLimit]);

  // Generic function to toggle device selection
  const toggleDevice = (deviceId: string, deviceType: 'input' | 'output') => {
    const isInput = deviceType === 'input';
    const selectedDevices = isInput ? settings.inputDevices : settings.outputDevices;
    const availableDevices = isInput ? appState.inputDevices : appState.outputDevices;

    const isSelected = selectedDevices.some((d) => d.id === deviceId);
    let updatedDevices;

    // New input devices start with noise suppression on
    const inputDefaults = isInput ? { noiseSuppression: true } : {};

    if (isSelected) {
      updatedDevices = selectedDevices.filter((d) => d.id !== deviceId);
    } else {
      if (deviceId === 'default') {
        updatedDevices = [
          ...selectedDevices,
          { id: 'default', name: 'Default Device', volume: 1.0, ...inputDefaults },
        ];
      } else {
        const deviceToAdd = availableDevices.find((d) => d.id === deviceId);
        if (deviceToAdd) {
          updatedDevices = [
            ...selectedDevices,
            { id: deviceId, name: deviceToAdd.name, volume: 1.0, ...inputDefaults },
          ];
        }
      }
    }

    if (isInput) {
      updateSettings({ inputDevices: updatedDevices });
    } else {
      updateSettings({ outputDevices: updatedDevices });
    }
  };

  // Generic function to handle device volume change
  const handleVolumeChange = (deviceId: string, volume: number, deviceType: 'input' | 'output') => {
    const isInput = deviceType === 'input';
    const selectedDevices = isInput ? settings.inputDevices : settings.outputDevices;

    const updatedDevices = selectedDevices.map((device) =>
      device.id === deviceId ? { ...device, volume: volume } : device,
    );

    if (isInput) {
      updateSettings({ inputDevices: updatedDevices });
    } else {
      updateSettings({ outputDevices: updatedDevices });
    }
  };

  const toggleInputOption = (deviceId: string, option: 'noiseSuppression' | 'forceMono') => {
    updateSettings({
      inputDevices: settings.inputDevices.map((device) =>
        // Off is left unset, matching the backend which omits false values
        device.id === deviceId
          ? { ...device, [option]: device[option] ? undefined : true }
          : device,
      ),
    });
  };

  const renderInputOptions = (device: DeviceSetting) =>
    [
      {
        option: 'noiseSuppression' as const,
        label: 'Noise Suppression',
        Icon: AudioWaveform,
      },
      // Rotated so two channels merge into one, left to right
      { option: 'forceMono' as const, label: 'Mono', Icon: Merge, iconClassName: 'rotate-90' },
    ].map(({ option, label, Icon, iconClassName }) => {
      const active = !!device[option];
      return (
        <div
          key={option}
          className="tooltip tooltip-left tooltip-primary inline-flex [&::before]:delay-200 [&::after]:delay-200"
          data-tip={label}
        >
          <button
            type="button"
            aria-label={label}
            aria-pressed={active}
            className={`group p-0.5 rounded border border-base-400 cursor-pointer transition-colors hover:bg-base-300 ${active ? 'text-primary' : 'text-base-content'}`}
            onClick={(e) => {
              e.preventDefault();
              toggleInputOption(device.id, option);
            }}
          >
            <Icon
              className={`h-3.5 w-3.5 transition-opacity ${active ? '' : 'opacity-40 group-hover:opacity-80'} ${iconClassName ?? ''}`}
            />
          </button>
        </div>
      );
    });

  // Render device list component
  const renderDeviceList = (deviceType: 'input' | 'output') => {
    const isInput = deviceType === 'input';
    const selectedDevices = isInput ? settings.inputDevices : settings.outputDevices;
    const availableDevices = isInput ? appState.inputDevices : appState.outputDevices;

    const defaultDevice: AudioDevice = { id: 'default', name: 'Default Device', isDefault: false };
    const allDevices = [defaultDevice, ...availableDevices];

    return (
      <>
        {/* List available devices as checkboxes */}
        {allDevices.map((device) => (
          <div key={device.id} className="form-control mb-1 last:mb-0">
            <label
              className={`flex items-center gap-2 p-1 rounded cursor-pointer hover:bg-base-200`}
            >
              <input
                type="checkbox"
                className="checkbox checkbox-sm checkbox-primary"
                checked={selectedDevices.some((d) => d.id === device.id)}
                onChange={() => toggleDevice(device.id, deviceType)}
              />
              <span className="label-text flex-1 mr-2 flex items-center">
                {device.name}
                {(() => {
                  const selectedIndex = combinedSelectedIds.indexOf(device.id);
                  const showLimitIcon =
                    settings.enableSeparateAudioTracks &&
                    selectedDevices.some((d) => d.id === device.id) &&
                    selectedIndex >= 0 &&
                    selectedIndex + implicitOutputCount >= maxIsolatedTracks;
                  return showLimitIcon ? (
                    <div
                      className="tooltip tooltip-bottom tooltip-warning ml-1 inline-flex"
                      data-tip="This source will be included in the Full Mix only"
                    >
                      <TriangleAlert className="h-4 w-4 text-warning" />
                    </div>
                  ) : null;
                })()}
              </span>
              {isInput &&
                (() => {
                  const selected = selectedDevices.find((d) => d.id === device.id);
                  return selected ? renderInputOptions(selected) : null;
                })()}
              {/* Volume slider for selected devices */}
              {selectedDevices.some((d) => d.id === device.id) &&
                (() => {
                  const isDragging =
                    draggingVolume.deviceId === device.id &&
                    draggingVolume.deviceType === deviceType;
                  return (
                    <div className="flex items-center gap-1 w-32">
                      <RangeSlider
                        min="0"
                        max="2"
                        step="0.02"
                        value={
                          isDragging
                            ? (draggingVolume.volume ?? 0)
                            : (selectedDevices.find((d) => d.id === device.id)?.volume ?? 1.0)
                        }
                        className="min-w-0 flex-1"
                        onChange={(e) => {
                          if (isDragging) {
                            setDraggingVolume({
                              ...draggingVolume,
                              volume: parseFloat(e.target.value),
                            });
                          }
                        }}
                        onMouseDown={(e) =>
                          setDraggingVolume({
                            deviceId: device.id,
                            deviceType,
                            volume: parseFloat(e.currentTarget.value),
                          })
                        }
                        onMouseUp={(e) => {
                          if (isDragging) {
                            handleVolumeChange(
                              device.id,
                              parseFloat(e.currentTarget.value),
                              deviceType,
                            );
                            setDraggingVolume({ deviceId: null, deviceType: null, volume: null });
                          }
                        }}
                      />
                      <span className="text-xs w-8 text-right">
                        {Math.round(
                          (isDragging
                            ? (draggingVolume.volume ?? 0)
                            : (selectedDevices.find((d) => d.id === device.id)?.volume ?? 1.0)) *
                            100,
                        )}
                        %
                      </span>
                    </div>
                  );
                })()}
            </label>
          </div>
        ))}

        {/* Show unavailable devices that are still selected */}
        {selectedDevices
          .filter(
            (deviceSetting) =>
              deviceSetting.id !== 'default' &&
              !isDeviceAvailable(deviceSetting.id, availableDevices) &&
              deviceSetting.id,
          )
          .map((deviceSetting) => (
            <div key={deviceSetting.id} className="form-control mb-1 last:mb-0">
              <label
                className={`flex items-center gap-2 p-1 rounded cursor-pointer hover:bg-base-200`}
              >
                <input
                  type="checkbox"
                  className="checkbox checkbox-sm checkbox-primary"
                  checked={true}
                  onChange={() => toggleDevice(deviceSetting.id, deviceType)}
                />
                <span className="label-text text-error flex items-center flex-1 mr-2 relative pl-6 leading-none">
                  <div
                    className="tooltip tooltip-right tooltip-error absolute left-0 inline-flex"
                    data-tip="This source is unavailable"
                  >
                    <CircleAlert size={18} />
                  </div>
                  {deviceSetting.name.replace(' (Default)', '')}
                </span>
                {isInput && renderInputOptions(deviceSetting)}
                {/* Volume slider for selected devices */}
                {(() => {
                  const isDragging =
                    draggingVolume.deviceId === deviceSetting.id &&
                    draggingVolume.deviceType === deviceType;
                  return (
                    <div className="flex items-center gap-1 w-32">
                      <RangeSlider
                        min="0"
                        max="2"
                        step="0.02"
                        value={isDragging ? (draggingVolume.volume ?? 0) : deviceSetting.volume}
                        className="min-w-0 flex-1"
                        onChange={(e) => {
                          if (isDragging) {
                            setDraggingVolume({
                              ...draggingVolume,
                              volume: parseFloat(e.target.value),
                            });
                          }
                        }}
                        onMouseDown={(e) =>
                          setDraggingVolume({
                            deviceId: deviceSetting.id,
                            deviceType,
                            volume: parseFloat(e.currentTarget.value),
                          })
                        }
                        onMouseUp={(e) => {
                          if (isDragging) {
                            handleVolumeChange(
                              deviceSetting.id,
                              parseFloat(e.currentTarget.value),
                              deviceType,
                            );
                            setDraggingVolume({ deviceId: null, deviceType: null, volume: null });
                          }
                        }}
                      />
                      <span className="text-xs w-8 text-right">
                        {Math.round(
                          (isDragging ? (draggingVolume.volume ?? 0) : deviceSetting.volume) * 100,
                        )}
                        %
                      </span>
                    </div>
                  );
                })()}
              </label>
            </div>
          ))}
      </>
    );
  };
  return (
    <div className="p-4 bg-base-300 rounded-lg shadow-md border border-base-400">
      <div className="flex items-center gap-2 mb-4">
        <h2 className="text-xl font-semibold">Audio</h2>
        {hasPendingChanges && (
          <span className="text-xs text-warning">(applies to next recording)</span>
        )}
      </div>

      <div className="grid grid-cols-2 gap-4">
        {/* Input Devices (Multiple Selection) */}
        <div className="form-control">
          <label className="label">
            <span className="label-text text-base-content">Input Devices</span>
          </label>
          <div className="bg-base-200 rounded-lg p-2 max-h-48 overflow-y-visible overflow-x-hidden border border-base-400 min-h-12.5">
            {renderDeviceList('input')}
          </div>
        </div>

        {/* Output Devices (Multiple Selection) */}
        <div className="form-control">
          <label className="label">
            <span className="label-text text-base-content">Output Devices</span>
          </label>
          <div className="bg-base-200 rounded-lg p-2 max-h-48 overflow-y-visible overflow-x-hidden border border-base-400 min-h-12.5">
            {renderDeviceList('output')}
          </div>
        </div>
      </div>

      <label className="label mt-4">
        <span className="label-text text-base-content">What to Record</span>
      </label>
      <div className="grid grid-cols-3 gap-4">
        {[
          {
            value: 'All' as AudioOutputMode,
            label: 'Everything',
            description:
              'All sound from the selected output devices, including music and voice chat.',
            icons: <Volume2 className="h-4 w-4" />,
          },
          {
            value: 'GameOnly' as AudioOutputMode,
            label: 'Game Only',
            description: "Only the game's own sound. Music and voice chat are left out.",
            icons: <Gamepad2 className="h-4 w-4" />,
          },
          {
            value: 'GameAndDiscord' as AudioOutputMode,
            label: 'Game and Voice Chat',
            description: 'The game plus Discord and TeamSpeak. Music and other apps are left out.',
            icons: (
              <span className="flex items-center gap-1.5">
                <Gamepad2 className="h-4 w-4" />
                <DiscordIcon className="h-4 w-4" />
                <TeamSpeakIcon className="h-4 w-4" />
              </span>
            ),
          },
        ].map((option) => (
          <label
            key={option.value}
            className={`relative bg-base-200 p-3 rounded-lg flex flex-col gap-1 transition-all border ${settings.audioOutputMode === option.value ? 'border-primary' : 'border-base-400'} cursor-pointer hover:bg-base-300`}
          >
            <input
              type="radio"
              name="audioOutputMode"
              className="sr-only"
              checked={settings.audioOutputMode === option.value}
              onChange={() => updateSettings({ audioOutputMode: option.value })}
            />
            <span className="flex items-center gap-1.5 font-semibold">
              {option.label}
              {option.icons}
            </span>
            <span className="text-sm opacity-70">{option.description}</span>
          </label>
        ))}
      </div>

      <label className="flex items-center gap-3 cursor-pointer p-3 bg-base-200 rounded-lg border border-base-400 mt-4">
        <input
          type="checkbox"
          name="enableSeparateAudioTracks"
          checked={settings.enableSeparateAudioTracks}
          onChange={(e) => updateSettings({ enableSeparateAudioTracks: e.target.checked })}
          className="checkbox checkbox-primary checkbox-sm"
        />
        <div>
          <div className="font-semibold">Separate Audio Tracks</div>
          <div className="text-sm opacity-70 mt-0.5">
            Saves each audio source as its own track, next to the full mix.
          </div>
        </div>
      </label>

      <AnimatePresence initial={false}>
        {hasOverTrackLimit && !trackLimitWarnDismissed && (
          <motion.div
            initial={{ opacity: 0, height: 0, overflow: 'hidden' }}
            animate={{
              opacity: 1,
              height: 'fit-content',
              transition: {
                duration: 0.3,
                height: { type: 'spring', stiffness: 300, damping: 30 },
              },
            }}
            exit={{ opacity: 0, height: 0, transition: { duration: 0.2 } }}
            className="mt-3 bg-warning/10 border border-warning rounded-lg px-3 text-warning text-sm flex items-center"
          >
            <div className="py-2 flex items-center w-full">
              <TriangleAlert className="h-5 w-5 mr-2 shrink-0" />
              <motion.span className="min-w-0 flex-1">
                You have selected more than {maxIsolatedTracks} audio sources. Only the first{' '}
                {maxIsolatedTracks} will be saved as separate audio tracks. Any additional sources
                will be recorded in the Full Mix only.
              </motion.span>
              <Button
                variant="ghost"
                size="xs"
                aria-label="Dismiss track limit warning"
                className="text-warning hover:bg-warning/20"
                onClick={() => {
                  setTrackLimitWarnDismissed(true);
                  localStorage.setItem('segra.trackLimitWarnDismissedSig', selectionSig);
                }}
              >
                <X className="h-4 w-4" />
              </Button>
            </div>
          </motion.div>
        )}
      </AnimatePresence>
    </div>
  );
}
