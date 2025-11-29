import { useState, useEffect } from 'react';
import { motion, AnimatePresence } from 'framer-motion';
import DropdownSelect from '../DropdownSelect';
import { Settings as SettingsType, VideoQualityPreset } from '../../Models/types';
import { sendMessageToBackend } from '../../Utils/MessageUtils';

interface VideoSettingsSectionProps {
  settings: SettingsType;
  updateSettings: (updates: Partial<SettingsType>) => void;
}

export default function VideoSettingsSection({ settings, updateSettings }: VideoSettingsSectionProps) {
  const [localReplayBufferDuration, setLocalReplayBufferDuration] = useState<string>(
    String(settings.replayBufferDuration),
  );
  const [localReplayBufferMaxSize, setLocalReplayBufferMaxSize] = useState<string>(String(settings.replayBufferMaxSize));
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
  const isRecording = settings.state.recording != null || settings.state.preRecording != null;

  const handlePresetChange = (preset: VideoQualityPreset) => {
    sendMessageToBackend('ApplyVideoPreset', { preset });
  };

  return (
    <div className="p-4 bg-base-300 rounded-lg shadow-md border border-custom">
      <h2 className="text-xl font-semibold mb-4">Video Settings</h2>

      {/* Quality Preset Selector */}
      <div className="mb-4">
        <div className="grid grid-cols-4 gap-3">
          <div
            className={`bg-base-200 p-3 rounded-lg flex flex-col items-center justify-center transition-all transition-200 border cursor-pointer hover:bg-base-300 ${settings.videoQualityPreset === 'low' ? 'border-primary' : 'border-base-400'
              }`}
            onClick={() => handlePresetChange('low')}
          >
            <div className="text-sm font-semibold">Low Quality</div>
            <div className="text-xs text-base-content text-opacity-70 mt-1">720p • 30fps</div>
          </div>
          <div
            className={`bg-base-200 p-3 rounded-lg flex flex-col items-center justify-center transition-all transition-200 border cursor-pointer hover:bg-base-300 ${settings.videoQualityPreset === 'standard' ? 'border-primary' : 'border-base-400'
              }`}
            onClick={() => handlePresetChange('standard')}
          >
            <div className="text-sm font-semibold">Standard</div>
            <div className="text-xs text-base-content text-opacity-70 mt-1">1080p • 60fps</div>
          </div>
          <div
            className={`bg-base-200 p-3 rounded-lg flex flex-col items-center justify-center transition-all transition-200 border cursor-pointer hover:bg-base-300 ${settings.videoQualityPreset === 'high' ? 'border-primary' : 'border-base-400'
              }`}
            onClick={() => handlePresetChange('high')}
          >
            <div className="text-sm font-semibold">High Quality</div>
            <div className="text-xs text-base-content text-opacity-70 mt-1">{settings.state.maxDisplayHeight >= 1440 ? '1440p' : '1080p'} • 60fps</div>
          </div>
          <div
            className={`bg-base-200 p-3 rounded-lg flex flex-col items-center justify-center transition-all transition-200 border cursor-pointer hover:bg-base-300 ${settings.videoQualityPreset === 'custom' ? 'border-primary' : 'border-base-400'
              }`}
            onClick={() => handlePresetChange('custom')}
          >
            <div className="text-sm font-semibold">Custom</div>
            <div className="text-xs text-base-content text-opacity-70 mt-1">Manual config</div>
          </div>
        </div>
      </div>

      {/* Replay Buffer Settings - Only show when Replay Buffer mode is selected */}
      <AnimatePresence>
        {(settings.recordingMode === 'Buffer' || settings.recordingMode === 'Hybrid') && (
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
                <label htmlFor="replayBufferDuration" className="label text-base-content px-0 !block mb-1">
                  <span className="label-text">Buffer Duration (seconds)</span>
                </label>
                <input
                  id="replayBufferDuration"
                  type="number"
                  name="replayBufferDuration"
                  value={localReplayBufferDuration}
                  onChange={(e) => setLocalReplayBufferDuration(e.target.value)}
                  onBlur={() => {
                    const val = Number(localReplayBufferDuration) || 30;
                    if (!localReplayBufferDuration) setLocalReplayBufferDuration('30');
                    updateSettings({ replayBufferDuration: val });
                  }}
                  min="5"
                  max="600"
                  disabled={isRecording}
                  className={`input input-bordered bg-base-200 disabled:bg-base-200 disabled:input-bordered disabled:opacity-80 w-full outline-none focus:border-base-400`}
                />
                <div className="help-text-container">
                  <span className="text-xs text-base-content/60 mt-1">
                    How many seconds of gameplay to keep in memory
                  </span>
                </div>
              </div>

              {/* Buffer Max Size */}
              <div className="form-control w-full">
                <label htmlFor="replayBufferMaxSize" className="label text-base-content px-0 !block mb-1">
                  <span className="label-text">Buffer Maximum Size (MB)</span>
                </label>
                <input
                  id="replayBufferMaxSize"
                  type="number"
                  name="replayBufferMaxSize"
                  value={localReplayBufferMaxSize}
                  onChange={(e) => setLocalReplayBufferMaxSize(e.target.value)}
                  onBlur={() => {
                    const val = Number(localReplayBufferMaxSize) || 1000;
                    if (!localReplayBufferMaxSize) setLocalReplayBufferMaxSize('1000');
                    updateSettings({ replayBufferMaxSize: val });
                  }}
                  min="100"
                  max="5000"
                  disabled={isRecording}
                  className="input input-bordered bg-base-200 disabled:bg-base-200 disabled:input-bordered disabled:opacity-80 w-full outline-none focus:border-base-400"
                />
              </div>
            </motion.div>
          </motion.div>
        )}
      </AnimatePresence>

      {/* Advanced Settings - Only show when Custom preset is selected */}
      <AnimatePresence>
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
            <div className="grid grid-cols-2 gap-4">
              {/* Resolution */}
              <div className="form-control">
                <label className="label">
                  <span className="label-text text-base-content">Resolution</span>
                </label>
                <DropdownSelect
                  items={[
                    { value: '720p', label: '720p' },
                    { value: '1080p', label: '1080p' },
                    ...(settings.state.maxDisplayHeight >= 1440 ? [{ value: '1440p', label: '1440p' }] : []),
                    ...(settings.state.maxDisplayHeight >= 2160 ? [{ value: '4K', label: '4K' }] : []),
                  ]}
                  value={settings.resolution}
                  onChange={(val) => updateSettings({ resolution: val as '720p' | '1080p' | '1440p' | '4K' })}
                />
              </div>

              {/* Frame Rate */}
              <div className="form-control">
                <label className="label">
                  <span className="label-text text-base-content">Frame Rate (FPS)</span>
                </label>
                <DropdownSelect
                  items={[24, 30, 60, 120, 144].map((v) => ({ value: String(v), label: String(v) }))}
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
                    ...(settings.encoder === 'cpu' ? [{ value: 'CRF', label: 'CRF (Constant Rate Factor)' }] : []),
                    ...(settings.encoder !== 'cpu' ? [{ value: 'CQP', label: 'CQP (Constant Quantization Parameter)' }] : []),
                  ]}
                  value={settings.rateControl}
                  onChange={(val) => updateSettings({ rateControl: val })}
                />
              </div>

              {/* Bitrate (for CBR) */}
              {settings.rateControl === 'CBR' && (
                <div className="form-control">
                  <label className="label">
                    <span className="label-text text-base-content">Bitrate (Mbps)</span>
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
                      <span className="label-text text-base-content">Minimum Bitrate (Mbps)</span>
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
                      <span className="label-text text-base-content">Maximum Bitrate (Mbps)</span>
                    </label>
                    <DropdownSelect
                      items={Array.from({ length: 19 }, (_, i) => (i + 2) * 5).map((v) => ({
                        value: String(v),
                        label: `${v} Mbps`,
                      }))}
                      value={String(
                        settings.maxBitrate ??
                        Math.max(settings.minBitrate ?? settings.bitrate, Math.round((settings.bitrate || 10) * 1.5)),
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
                    <span className="label-text text-base-content">CRF Value (0-51)</span>
                  </label>
                  <input
                    type="number"
                    name="crfValue"
                    value={localCrfValue}
                    onChange={(e) => setLocalCrfValue(e.target.value)}
                    onBlur={() => {
                      const val = Number(localCrfValue) || 23;
                      if (!localCrfValue) setLocalCrfValue('23');
                      updateSettings({ crfValue: val });
                    }}
                    min="0"
                    max="51"
                    className="input input-bordered bg-base-200 w-full outline-none focus:border-base-400"
                  />
                </div>
              )}

              {/* CQ Level (for CQP) */}
              {settings.rateControl === 'CQP' && (
                <div className="form-control">
                  <label className="label">
                    <span className="label-text text-base-content">CQ Level (0-30)</span>
                  </label>
                  <input
                    type="number"
                    name="cqLevel"
                    value={localCqLevel}
                    onChange={(e) => setLocalCqLevel(e.target.value)}
                    onBlur={() => {
                      const val = Number(localCqLevel) || 20;
                      if (!localCqLevel) setLocalCqLevel('20');
                      updateSettings({ cqLevel: val });
                    }}
                    min="0"
                    max="30"
                    className="input input-bordered bg-base-200 w-full outline-none focus:border-base-400"
                  />
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
                  items={settings.state.codecs
                    .filter((codec) => (settings.encoder === 'gpu' ? codec.isHardwareEncoder : !codec.isHardwareEncoder))
                    .sort((a, b) => {
                      const priorityOrder = ['jim_nvenc', 'h264_texture_amf', 'obs_x264'];
                      const aIndex = priorityOrder.indexOf(a.internalEncoderId);
                      const bIndex = priorityOrder.indexOf(b.internalEncoderId);
                      if (aIndex !== -1 && bIndex !== -1) return aIndex - bIndex;
                      if (aIndex !== -1) return -1;
                      if (bIndex !== -1) return 1;
                      return 0;
                    })
                    .map((codec) => ({ value: codec.internalEncoderId, label: codec.friendlyName }))}
                  value={
                    settings.state.codecs.find((c) => c.internalEncoderId === settings.codec?.internalEncoderId)
                      ?.internalEncoderId
                  }
                  onChange={(val) =>
                    updateSettings({
                      codec: settings.state.codecs.find((c) => c.internalEncoderId === val),
                    })
                  }
                  disabled={settings.state.codecs.length === 0}
                />
              </div>
            </div>
          </motion.div>
        )}
      </AnimatePresence>

      <div className="flex items-center justify-between mt-3">
        <label className="flex items-center gap-2 cursor-pointer">
          <input
            type="checkbox"
            name="enableDisplayRecording"
            checked={settings.enableDisplayRecording}
            onChange={(e) => updateSettings({ enableDisplayRecording: e.target.checked })}
            className="checkbox checkbox-primary checkbox-sm"
          />
          <span className="font-medium cursor-pointer">Enable Display Recording</span>
        </label>
      </div>

      <AnimatePresence>
        {settings.enableDisplayRecording && (
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
            key="display-selection"
          >
            <div className="flex flex-col mt-2">
              <span className="font-medium">Monitor Selection</span>
              <DropdownSelect
                items={[
                  { value: 'Automatic', label: 'Automatic' },
                  ...settings.state.displays.map((d) => ({
                    value: d.deviceName,
                    label: `${d.deviceName}${d.isPrimary ? ' (Primary)' : ''}`,
                  })),
                ]}
                value={settings.selectedDisplay?.deviceName || 'Automatic'}
                onChange={(val) =>
                  updateSettings({
                    selectedDisplay:
                      val === 'Automatic' ? undefined : settings.state.displays.find((d) => d.deviceName === val),
                  })
                }
              />
            </div>
          </motion.div>
        )}
      </AnimatePresence>

      <div className="flex items-center justify-between mt-3">
        <div className="flex flex-col gap-1">
          <label className="flex items-center gap-2 cursor-pointer">
            <input
              type="checkbox"
              name="recordWindowedApplications"
              checked={settings.recordWindowedApplications}
              onChange={(e) => updateSettings({ recordWindowedApplications: e.target.checked })}
              className="checkbox checkbox-primary checkbox-sm"
              disabled={isRecording || settings.enableDisplayRecording}
            />
            <span className="font-medium cursor-pointer">Capture Windowed Games</span>
            <span className="badge badge-primary badge-sm">Beta</span>
          </label>
          {settings.enableDisplayRecording && (
            <span className="text-xs text-warning ml-7">Cannot be enabled with Display Recording while this feature is in Beta</span>
          )}
        </div>
      </div>
    </div>
  );
}
