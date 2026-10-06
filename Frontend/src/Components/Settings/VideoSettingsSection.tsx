import { useState, useEffect } from 'react';
import { motion, AnimatePresence } from 'framer-motion';
import DropdownSelect from '../DropdownSelect';
import { Settings as SettingsType, VideoQualityPreset } from '../../Models/types';
import { sendMessageToBackend } from '../../Utils/MessageUtils';
import { clampInt } from '../../Utils/NumberUtils';
import { useAppState } from '../../Context/AppStateContext';
import {
  usePendingRecordingSettings,
  RECORDING_SETTING_GROUPS,
} from '../../Hooks/usePendingRecordingSettings';

interface VideoSettingsSectionProps {
  settings: SettingsType;
  updateSettings: (updates: Partial<SettingsType>) => void;
}

export default function VideoSettingsSection({
  settings,
  updateSettings,
}: VideoSettingsSectionProps) {
  const appState = useAppState();
  const hasHdrDisplay = appState.displays.some((d) => d.isHdr);
  const [localReplayBufferDuration, setLocalReplayBufferDuration] = useState<string>(
    String(settings.replayBufferDuration),
  );
  const [localReplayBufferMaxSize, setLocalReplayBufferMaxSize] = useState<string>(
    String(settings.replayBufferMaxSize),
  );
  const [localCrfValue, setLocalCrfValue] = useState<string>(String(settings.crfValue));
  const [localCqLevel, setLocalCqLevel] = useState<string>(String(settings.cqLevel));

  useEffect(() => {
    setLocalReplayBufferDuration(String(settings.replayBufferDuration));
  }, [settings.replayBufferDuration]);

  useEffect(() => {
    setLocalReplayBufferMaxSize(String(settings.replayBufferMaxSize));
  }, [settings.replayBufferMaxSize]);

  useEffect(() => {
    setLocalCrfValue(String(settings.crfValue));
  }, [settings.crfValue]);

  useEffect(() => {
    setLocalCqLevel(String(settings.cqLevel));
  }, [settings.cqLevel]);
  const hasPendingChanges = usePendingRecordingSettings(RECORDING_SETTING_GROUPS.video);

  const handlePresetChange = (preset: VideoQualityPreset) => {
    sendMessageToBackend('ApplyVideoPreset', { preset });
  };

  const monitorSelectionField = appState.usesScreenSharePicker ? (
    <div className="form-control w-full">
      <label className="label text-base-content px-0 !block">
        <span className="label-text">Screen</span>
      </label>
      <div className="input input-bordered bg-base-200 w-full flex items-center justify-between gap-2 pr-1.5">
        <span className="truncate font-medium">
          {settings.pipeWireRestoreToken
            ? 'Picked in screen-share dialog'
            : 'Asks on next recording'}
        </span>
        <button
          className="btn btn-sm border-base-400 bg-base-300 hover:bg-base-100 font-semibold shrink-0"
          disabled={!settings.pipeWireRestoreToken}
          onClick={() => sendMessageToBackend('ResetScreenSelection')}
        >
          Change screen
        </button>
      </div>
      <div className="mt-1 px-1 text-xs opacity-70 leading-snug">
        Used for manual recordings and the always-on replay buffer.
      </div>
    </div>
  ) : (
    <div className="form-control">
      <label className="label">
        <span className="label-text text-base-content">Monitor Selection</span>
      </label>
      <DropdownSelect
        items={[
          { value: 'Automatic', label: 'Automatic' },
          ...appState.displays.map((d, i) => {
            const hasDuplicateName = appState.displays.some(
              (other, j) => j !== i && other.deviceName === d.deviceName,
            );
            const label = hasDuplicateName
              ? `${d.deviceName} (${i + 1})${d.isPrimary ? ' (Primary)' : ''}`
              : `${d.deviceName}${d.isPrimary ? ' (Primary)' : ''}`;
            return { value: d.deviceId, label };
          }),
        ]}
        value={settings.selectedDisplay?.deviceId || 'Automatic'}
        onChange={(val) =>
          updateSettings({
            selectedDisplay:
              val === 'Automatic' ? undefined : appState.displays.find((d) => d.deviceId === val),
          })
        }
      />
      <div className="mt-1 px-1 text-xs opacity-70 leading-snug">
        Used for manual recordings and the always-on replay buffer.
      </div>
    </div>
  );

  return (
    <div className="p-4 bg-base-300 rounded-lg shadow-md border border-base-400">
      <div className="flex items-center gap-2 mb-4">
        <h2 className="text-xl font-semibold">Video Settings</h2>
        {hasPendingChanges && (
          <span className="text-xs text-warning">(applies to next recording)</span>
        )}
      </div>

      {/* Quality Preset Selector */}
      <div className="mb-4">
        <div className="grid grid-cols-4 gap-3">
          <div
            className={`bg-base-200 p-3 rounded-lg flex flex-col items-center justify-center transition-all transition-200 border ${
              settings.videoQualityPreset === 'low' ? 'border-primary' : 'border-base-400'
            } cursor-pointer hover:bg-base-300`}
            onClick={() => handlePresetChange('low')}
          >
            <div className="text-sm font-semibold">Low Quality</div>
            <div className="text-xs opacity-70 mt-1">720p • 30fps</div>
          </div>
          <div
            className={`bg-base-200 p-3 rounded-lg flex flex-col items-center justify-center transition-all transition-200 border ${
              settings.videoQualityPreset === 'standard' ? 'border-primary' : 'border-base-400'
            } cursor-pointer hover:bg-base-300`}
            onClick={() => handlePresetChange('standard')}
          >
            <div className="text-sm font-semibold">Standard</div>
            <div className="text-xs opacity-70 mt-1">1080p • 60fps</div>
          </div>
          <div
            className={`bg-base-200 p-3 rounded-lg flex flex-col items-center justify-center transition-all transition-200 border ${
              settings.videoQualityPreset === 'high' ? 'border-primary' : 'border-base-400'
            } cursor-pointer hover:bg-base-300`}
            onClick={() => handlePresetChange('high')}
          >
            <div className="text-sm font-semibold">High Quality</div>
            <div className="text-xs opacity-70 mt-1">
              {appState.maxDisplayHeight >= 1440 ? '1440p' : '1080p'} • 60fps
            </div>
          </div>
          <div
            className={`bg-base-200 p-3 rounded-lg flex flex-col items-center justify-center transition-all transition-200 border ${
              settings.videoQualityPreset === 'custom' ? 'border-primary' : 'border-base-400'
            } cursor-pointer hover:bg-base-300`}
            onClick={() => handlePresetChange('custom')}
          >
            <div className="text-sm font-semibold">Custom</div>
            <div className="text-xs opacity-70 mt-1">Manual config</div>
          </div>
        </div>
      </div>

      {/* Replay Buffer Settings - Only show when a replay buffer is in use */}
      <AnimatePresence initial={false}>
        {(settings.recordingMode === 'Buffer' ||
          settings.recordingMode === 'Hybrid' ||
          settings.alwaysOnReplayBuffer) && (
          <motion.div
            className="bg-base-300"
            initial={{ opacity: 0, height: 0 }}
            animate={{
              opacity: 1,
              height: 'fit-content',
              transition: {
                duration: 0.3,
                height: { type: 'spring', stiffness: 300, damping: 30 },
              },
            }}
            exit={{
              opacity: 0,
              height: 0,
              transition: {
                duration: 0.2,
              },
            }}
            style={{ overflow: 'visible' }}
          >
            <motion.div
              className="grid grid-cols-2 gap-4"
              initial={{ opacity: 0 }}
              animate={{ opacity: 1, transition: { delay: 0.2 } }}
            >
              {/* Buffer Duration */}
              <div className="form-control w-full">
                <label
                  htmlFor="replayBufferDuration"
                  className="label text-base-content px-0 !block"
                >
                  <span className="label-text">Buffer Duration</span>
                </label>
                <div className="relative w-full">
                  <input
                    id="replayBufferDuration"
                    type="number"
                    name="replayBufferDuration"
                    value={localReplayBufferDuration}
                    onChange={(e) => setLocalReplayBufferDuration(e.target.value)}
                    onBlur={() => {
                      const val = clampInt(localReplayBufferDuration, 5, 600, 30);
                      setLocalReplayBufferDuration(String(val));
                      updateSettings({ replayBufferDuration: val });
                    }}
                    min="5"
                    max="600"
                    className="input input-bordered bg-base-200 disabled:bg-base-200 disabled:input-bordered disabled:opacity-80 w-full pr-12 outline-none focus:border-base-400"
                  />
                  <span className="absolute inset-y-0 right-3 flex items-center text-sm opacity-60 pointer-events-none">
                    sec
                  </span>
                </div>
              </div>

              {/* Buffer Max Size */}
              <div className="form-control w-full">
                <label
                  htmlFor="replayBufferMaxSize"
                  className="label text-base-content px-0 !block"
                >
                  <span className="label-text">Buffer Maximum Size</span>
                </label>
                <div className="relative w-full">
                  <input
                    id="replayBufferMaxSize"
                    type="number"
                    name="replayBufferMaxSize"
                    value={localReplayBufferMaxSize}
                    onChange={(e) => setLocalReplayBufferMaxSize(e.target.value)}
                    onBlur={() => {
                      const val = clampInt(localReplayBufferMaxSize, 100, 5000, 1000);
                      setLocalReplayBufferMaxSize(String(val));
                      updateSettings({ replayBufferMaxSize: val });
                    }}
                    min="100"
                    max="5000"
                    className="input input-bordered bg-base-200 disabled:bg-base-200 disabled:input-bordered disabled:opacity-80 w-full pr-12 outline-none focus:border-base-400"
                  />
                  <span className="absolute inset-y-0 right-3 flex items-center text-sm opacity-60 pointer-events-none">
                    MB
                  </span>
                </div>
              </div>
            </motion.div>
          </motion.div>
        )}
      </AnimatePresence>

      {/* Advanced Settings - Only show when Custom preset is selected */}
      <AnimatePresence initial={false}>
        {settings.videoQualityPreset === 'custom' && (
          <motion.div
            initial={{ opacity: 0, height: 0 }}
            animate={{
              opacity: 1,
              height: 'fit-content',
              transition: {
                duration: 0.3,
                height: { type: 'spring', stiffness: 300, damping: 30 },
              },
            }}
            exit={{
              opacity: 0,
              height: 0,
              transition: {
                duration: 0.2,
              },
            }}
            style={{ overflow: 'visible' }}
          >
            <div className="grid grid-cols-2 gap-4 pb-1 mt-4">
              {/* Resolution */}
              <div className="form-control">
                <label className="label">
                  <span className="label-text text-base-content">Resolution</span>
                </label>
                <DropdownSelect
                  items={[
                    { value: '720p', label: '720p' },
                    { value: '1080p', label: '1080p' },
                    ...(appState.maxDisplayHeight >= 1440
                      ? [{ value: '1440p', label: '1440p' }]
                      : []),
                    ...(appState.maxDisplayHeight >= 2160 ? [{ value: '4K', label: '4K' }] : []),
                  ]}
                  value={settings.resolution}
                  onChange={(val) =>
                    updateSettings({ resolution: val as '720p' | '1080p' | '1440p' | '4K' })
                  }
                />
              </div>

              {/* Frame Rate */}
              <div className="form-control">
                <label className="label">
                  <span className="label-text text-base-content">Frame Rate</span>
                </label>
                <DropdownSelect
                  items={[24, 30, 60, 120, 144].map((v) => ({
                    value: String(v),
                    label: `${v} FPS`,
                  }))}
                  value={String(settings.frameRate)}
                  onChange={(val) => updateSettings({ frameRate: Number(val) })}
                />
              </div>

              {/* Rate Control */}
              <div className="form-control">
                <label className="label">
                  <span className="label-text text-base-content">Rate Control</span>
                </label>
                <DropdownSelect
                  items={[
                    { value: 'CBR', label: 'CBR (Constant Bitrate)' },
                    { value: 'VBR', label: 'VBR (Variable Bitrate)' },
                    ...(settings.encoder === 'cpu'
                      ? [{ value: 'CRF', label: 'CRF (Constant Rate Factor)' }]
                      : []),
                    ...(settings.encoder !== 'cpu'
                      ? [{ value: 'CQP', label: 'CQP (Constant Quantization Parameter)' }]
                      : []),
                  ]}
                  value={settings.rateControl}
                  onChange={(val) => updateSettings({ rateControl: val })}
                />
              </div>

              {/* Bitrate (for CBR) */}
              {settings.rateControl === 'CBR' && (
                <div className="form-control">
                  <label className="label">
                    <span className="label-text text-base-content">Bitrate</span>
                  </label>
                  <DropdownSelect
                    items={Array.from({ length: 19 }, (_, i) => (i + 2) * 5).map((v) => ({
                      value: String(v),
                      label: `${v} Mbps`,
                    }))}
                    value={String(settings.bitrate)}
                    onChange={(val) => updateSettings({ bitrate: Number(val) })}
                  />
                </div>
              )}

              {/* VBR Min/Max Bitrate */}
              {settings.rateControl === 'VBR' && (
                <>
                  <div className="form-control">
                    <label className="label">
                      <span className="label-text text-base-content">Minimum Bitrate</span>
                    </label>
                    <DropdownSelect
                      items={Array.from({ length: 19 }, (_, i) => (i + 2) * 5).map((v) => ({
                        value: String(v),
                        label: `${v} Mbps`,
                      }))}
                      value={String(settings.minBitrate ?? settings.bitrate)}
                      onChange={(val) => {
                        const min = Number(val);
                        const max = Math.max(min, settings.maxBitrate ?? min);
                        updateSettings({ minBitrate: min, maxBitrate: max });
                      }}
                    />
                  </div>
                  <div className="form-control">
                    <label className="label">
                      <span className="label-text text-base-content">Maximum Bitrate</span>
                    </label>
                    <DropdownSelect
                      items={Array.from({ length: 19 }, (_, i) => (i + 2) * 5).map((v) => ({
                        value: String(v),
                        label: `${v} Mbps`,
                      }))}
                      value={String(
                        settings.maxBitrate ??
                          Math.max(
                            settings.minBitrate ?? settings.bitrate,
                            Math.round((settings.bitrate || 10) * 1.5),
                          ),
                      )}
                      onChange={(val) => {
                        const max = Number(val);
                        const min = Math.min(max, settings.minBitrate ?? settings.bitrate);
                        updateSettings({ maxBitrate: max, minBitrate: min });
                      }}
                    />
                  </div>
                </>
              )}

              {/* CRF Value (for CRF) */}
              {settings.rateControl === 'CRF' && (
                <div className="form-control">
                  <label className="label">
                    <span className="label-text text-base-content">CRF Value</span>
                  </label>
                  <div className="relative w-full">
                    <input
                      type="number"
                      name="crfValue"
                      value={localCrfValue}
                      onChange={(e) => setLocalCrfValue(e.target.value)}
                      onBlur={() => {
                        const val = clampInt(localCrfValue, 0, 51, 23);
                        setLocalCrfValue(String(val));
                        updateSettings({ crfValue: val });
                      }}
                      min="0"
                      max="51"
                      className="input input-bordered bg-base-200 disabled:bg-base-200 disabled:opacity-80 w-full pr-14 outline-none focus:border-base-400"
                    />
                    <span className="absolute inset-y-0 right-3 flex items-center text-sm opacity-60 pointer-events-none">
                      0-51
                    </span>
                  </div>
                </div>
              )}

              {/* CQ Level (for CQP) */}
              {settings.rateControl === 'CQP' && (
                <div className="form-control">
                  <label className="label">
                    <span className="label-text text-base-content">CQ Level</span>
                  </label>
                  <div className="relative w-full">
                    <input
                      type="number"
                      name="cqLevel"
                      value={localCqLevel}
                      onChange={(e) => setLocalCqLevel(e.target.value)}
                      onBlur={() => {
                        const val = clampInt(localCqLevel, 0, 30, 20);
                        setLocalCqLevel(String(val));
                        updateSettings({ cqLevel: val });
                      }}
                      min="0"
                      max="30"
                      className="input input-bordered bg-base-200 disabled:bg-base-200 disabled:opacity-80 w-full pr-14 outline-none focus:border-base-400"
                    />
                    <span className="absolute inset-y-0 right-3 flex items-center text-sm opacity-60 pointer-events-none">
                      0-30
                    </span>
                  </div>
                </div>
              )}

              {/* Encoder */}
              <div className="form-control">
                <label className="label">
                  <span className="label-text text-base-content">Video Encoder</span>
                </label>
                <DropdownSelect
                  items={[
                    { value: 'gpu', label: 'GPU' },
                    { value: 'cpu', label: 'CPU' },
                  ]}
                  value={settings.encoder}
                  onChange={(val) => updateSettings({ encoder: val as 'gpu' | 'cpu' })}
                />
              </div>

              {/* Codec */}
              <div className="form-control">
                <label className="label">
                  <span className="label-text text-base-content">Codec</span>
                </label>
                <DropdownSelect
                  items={appState.codecs
                    .filter((codec) =>
                      settings.encoder === 'gpu'
                        ? codec.isHardwareEncoder
                        : !codec.isHardwareEncoder,
                    )
                    .sort((a, b) => {
                      const priorityOrder = ['jim_nvenc', 'h264_texture_amf', 'obs_x264'];
                      const aIndex = priorityOrder.indexOf(a.internalEncoderId);
                      const bIndex = priorityOrder.indexOf(b.internalEncoderId);
                      if (aIndex !== -1 && bIndex !== -1) return aIndex - bIndex;
                      if (aIndex !== -1) return -1;
                      if (bIndex !== -1) return 1;
                      return 0;
                    })
                    .map((codec) => ({
                      value: codec.internalEncoderId,
                      label: codec.friendlyName,
                    }))}
                  value={
                    appState.codecs.find(
                      (c) => c.internalEncoderId === settings.codec?.internalEncoderId,
                    )?.internalEncoderId
                  }
                  onChange={(val) =>
                    updateSettings({
                      codec: appState.codecs.find((c) => c.internalEncoderId === val),
                    })
                  }
                  disabled={appState.codecs.length === 0}
                />
              </div>

              {monitorSelectionField}
            </div>
          </motion.div>
        )}
      </AnimatePresence>

      {settings.videoQualityPreset !== 'custom' && (
        <div className="grid grid-cols-2 gap-4 mt-3">{monitorSelectionField}</div>
      )}

      <div className="flex flex-col gap-3 mt-3">
        <label className="flex items-center gap-3 cursor-pointer p-3 bg-base-200 rounded-lg border border-base-400">
          <input
            type="checkbox"
            checked={settings.stretch4By3}
            onChange={(e) => updateSettings({ stretch4By3: e.target.checked })}
            className="checkbox checkbox-primary checkbox-sm"
          />
          <div>
            <div className="font-semibold">Stretch 4:3 to 16:9</div>
            <div className="text-sm opacity-70 mt-0.5">
              Games running at a 4:3 resolution fill the whole video instead of getting black bars.
            </div>
          </div>
        </label>

        {/* Only shown when at least one display is in HDR mode */}
        {hasHdrDisplay && (
          <label className="flex items-center gap-3 cursor-pointer p-3 bg-base-200 rounded-lg border border-base-400">
            <input
              type="checkbox"
              checked={settings.enableHdr}
              onChange={(e) => updateSettings({ enableHdr: e.target.checked })}
              className="checkbox checkbox-primary checkbox-sm"
            />
            <div>
              <div className="font-semibold">Record in HDR</div>
              <div className="text-sm opacity-70 mt-0.5">
                Keeps HDR brightness and color. Videos can look washed out on screens without HDR.
              </div>
            </div>
          </label>
        )}
      </div>
    </div>
  );
}
