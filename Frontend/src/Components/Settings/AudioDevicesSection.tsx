import { useState, useEffect } from 'react';
import { motion, AnimatePresence } from 'framer-motion';
import { MdWarning, MdClose, MdError, MdVolumeUp } from 'react-icons/md';
import { LuGamepad2 } from 'react-icons/lu';
import { FaDiscord } from 'react-icons/fa';
import Button from '../Button';
import { Settings as SettingsType, AudioDevice, AudioOutputMode } from '../../Models/types';

interface AudioDevicesSectionProps {
  settings: SettingsType;
  updateSettings: (updates: Partial<SettingsType>) => void;
}

export default function AudioDevicesSection({
  settings,
  updateSettings,
}: AudioDevicesSectionProps) {
  const [draggingVolume, setDraggingVolume] = useState<{
    deviceId: string | null;
    volume: number | null;
  }>({ deviceId: null, volume: null });

  // Helper function to check if the selected device is available
  const isDeviceAvailable = (deviceId: string, devices: AudioDevice[]) => {
    return devices.some((device) => device.id === deviceId);
  };

  // Multi-track audio: first 5 selected sources get isolated tracks (Track 1 is Full Mix)
  const selectedInputIds = settings.inputDevices.map((d) => d.id);
  const selectedOutputIds = settings.outputDevices.map((d) => d.id);
  const combinedSelectedIds = [...selectedInputIds, ...selectedOutputIds];
  const maxIsolatedTracks = 5;
  const hasOverTrackLimit =
    settings.enableSeparateAudioTracks && combinedSelectedIds.length > maxIsolatedTracks;
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
    const availableDevices = isInput ? settings.state.inputDevices : settings.state.outputDevices;

    const isSelected = selectedDevices.some((d) => d.id === deviceId);
    let updatedDevices;

    if (isSelected) {
      updatedDevices = selectedDevices.filter((d) => d.id !== deviceId);
    } else {
      const deviceToAdd = availableDevices.find((d) => d.id === deviceId);
      if (deviceToAdd) {
        updatedDevices = [
          ...selectedDevices,
          { id: deviceId, name: deviceToAdd.name, volume: 1.0 },
        ];
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

  // Render device list component
  const renderDeviceList = (deviceType: 'input' | 'output') => {
    const isInput = deviceType === 'input';
    const selectedDevices = isInput ? settings.inputDevices : settings.outputDevices;
    const availableDevices = isInput ? settings.state.inputDevices : settings.state.outputDevices;

    return (
      <>
        {/* List available devices as checkboxes */}
        {availableDevices.map((device) => (
          <div key={device.id} className="form-control mb-1 last:mb-0">
            <label className="cursor-pointer flex items-center gap-2 p-1 hover:bg-base-200 rounded">
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
                    selectedIndex >= maxIsolatedTracks;
                  return showLimitIcon ? (
                    <div
                      className="tooltip tooltip-bottom tooltip-warning ml-1 inline-flex"
                      data-tip="This source will be included in the Full Mix only"
                    >
                      <MdWarning className="h-4 w-4 text-warning" />
                    </div>
                  ) : null;
                })()}
              </span>
              {/* Volume slider for selected devices */}
              {selectedDevices.some((d) => d.id === device.id) && (
                <div className="flex items-center gap-1 w-32">
                  <input
                    type="range"
                    min="0"
                    max="2"
                    step="0.02"
                    value={
                      draggingVolume.deviceId === device.id
                        ? (draggingVolume.volume ?? 0)
                        : (selectedDevices.find((d) => d.id === device.id)?.volume ?? 1.0)
                    }
                    className="range range-xs range-primary [--range-fill:0]"
                    onChange={(e) => {
                      if (draggingVolume.deviceId === device.id) {
                        setDraggingVolume({
                          ...draggingVolume,
                          volume: parseFloat(e.target.value),
                        });
                      }
                    }}
                    onMouseDown={(e) =>
                      setDraggingVolume({
                        deviceId: device.id,
                        volume: parseFloat(e.currentTarget.value),
                      })
                    }
                    onMouseUp={(e) => {
                      if (draggingVolume.deviceId === device.id) {
                        handleVolumeChange(
                          device.id,
                          parseFloat(e.currentTarget.value),
                          deviceType,
                        );
                        setDraggingVolume({ deviceId: null, volume: null });
                      }
                    }}
                  />
                  <span className="text-xs w-8 text-right">
                    {Math.round(
                      (draggingVolume.deviceId === device.id
                        ? (draggingVolume.volume ?? 0)
                        : (selectedDevices.find((d) => d.id === device.id)?.volume ?? 1.0)) * 100,
                    )}
                    %
                  </span>
                </div>
              )}
            </label>
          </div>
        ))}

        {/* Show unavailable devices that are still selected */}
        {selectedDevices
          .filter(
            (deviceSetting) =>
              !isDeviceAvailable(deviceSetting.id, availableDevices) && deviceSetting.id,
          )
          .map((deviceSetting) => (
            <div key={deviceSetting.id} className="form-control mb-1 last:mb-0">
              <label className="cursor-pointer flex items-center gap-2 p-1 hover:bg-base-200 rounded">
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
                    <MdError size={18} />
                  </div>
                  {deviceSetting.name.replace(' (Default)', '')}
                </span>
                {/* Volume slider for selected devices */}
                {
                  <div className="flex items-center gap-1 w-32">
                    <input
                      type="range"
                      min="0"
                      max="2"
                      step="0.02"
                      value={
                        draggingVolume.deviceId === deviceSetting.id
                          ? (draggingVolume.volume ?? 0)
                          : deviceSetting.volume
                      }
                      className="range range-xs range-primary [--range-fill:0]"
                      onChange={(e) => {
                        if (draggingVolume.deviceId === deviceSetting.id) {
                          setDraggingVolume({
                            ...draggingVolume,
                            volume: parseFloat(e.target.value),
                          });
                        }
                      }}
                      onMouseDown={(e) =>
                        setDraggingVolume({
                          deviceId: deviceSetting.id,
                          volume: parseFloat(e.currentTarget.value),
                        })
                      }
                      onMouseUp={(e) => {
                        if (draggingVolume.deviceId === deviceSetting.id) {
                          handleVolumeChange(
                            deviceSetting.id,
                            parseFloat(e.currentTarget.value),
                            deviceType,
                          );
                          setDraggingVolume({ deviceId: null, volume: null });
                        }
                      }}
                    />
                    <span className="text-xs w-8 text-right">
                      {Math.round(
                        (draggingVolume.deviceId === deviceSetting.id
                          ? (draggingVolume.volume ?? 0)
                          : deviceSetting.volume) * 100,
                      )}
                      %
                    </span>
                  </div>
                }
              </label>
            </div>
          ))}
      </>
    );
  };
  return (
    <div className="p-4 bg-base-300 rounded-lg shadow-md border border-custom">
      <h2 className="text-xl font-semibold mb-4">Input/Output Devices</h2>
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

      <div className="mt-4 flex flex-col gap-3">
        <label className="cursor-pointer flex items-center">
          <input
            type="checkbox"
            name="forceMonoInputSources"
            checked={settings.forceMonoInputSources}
            onChange={(e) => updateSettings({ forceMonoInputSources: e.target.checked })}
            className="checkbox checkbox-sm checkbox-accent"
          />
          <span className="ml-2">Mono Input Devices</span>
        </label>

        <label className="cursor-pointer flex items-center">
          <input
            type="checkbox"
            name="enableSeparateAudioTracks"
            checked={settings.enableSeparateAudioTracks}
            onChange={(e) => updateSettings({ enableSeparateAudioTracks: e.target.checked })}
            className="checkbox checkbox-sm checkbox-accent"
          />
          <span className="ml-2">Separate Audio Tracks</span>
        </label>

        <div className="flex flex-col gap-1 w-70">
          <label className="label-text text-base-content mb-1">Output Audio Mode</label>
          {[
            {
              value: 'All' as AudioOutputMode,
              label: 'All PC Audio',
              icons: <MdVolumeUp className="h-4 w-4" />,
            },
            {
              value: 'GameOnly' as AudioOutputMode,
              label: 'Game Audio Only',
              icons: <LuGamepad2 className="h-4 w-4" />,
            },
            {
              value: 'GameAndDiscord' as AudioOutputMode,
              label: 'Game + Discord Audio Only',
              icons: (
                <span className="flex items-center gap-1.5">
                  <LuGamepad2 className="h-4 w-4" />
                  <FaDiscord className="h-4 w-4" />
                </span>
              ),
            },
          ].map((option) => (
            <label
              key={option.value}
              className="cursor-pointer flex items-center gap-2 p-1 hover:bg-base-200 rounded"
            >
              <input
                type="radio"
                name="audioOutputMode"
                className="radio radio-sm radio-accent"
                checked={settings.audioOutputMode === option.value}
                onChange={() => updateSettings({ audioOutputMode: option.value })}
              />
              <span className="flex items-center gap-1.5 text-sm">
                {option.label}
                {option.icons}
              </span>
            </label>
          ))}
        </div>

        <AnimatePresence>
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
              className="mt-3 bg-amber-900 bg-opacity-30 border border-amber-500 rounded px-3 text-amber-400 text-sm flex items-center"
            >
              <div className="py-2 flex items-center w-full">
                <MdWarning className="h-5 w-5 mr-2 shrink-0" />
                <motion.span className="flex-1">
                  You have selected more than 5 audio sources. Only the first 5 will be saved as
                  separate audio tracks. Any additional sources will be recorded in the Full Mix
                  only.
                </motion.span>
                <Button
                  variant="ghost"
                  size="xs"
                  aria-label="Dismiss track limit warning"
                  className="text-amber-300 hover:text-amber-100"
                  onClick={() => {
                    setTrackLimitWarnDismissed(true);
                    localStorage.setItem('segra.trackLimitWarnDismissedSig', selectionSig);
                  }}
                >
                  <MdClose className="h-4 w-4" />
                </Button>
              </div>
            </motion.div>
          )}
        </AnimatePresence>
      </div>
    </div>
  );
}
