import { motion, AnimatePresence } from 'framer-motion';
import { useTranslation } from 'react-i18next';
import DropdownSelect from '../DropdownSelect';
import {
  Settings as SettingsType,
  GpuVendor,
  ClipFPS,
  ClipPreset,
  ClipQualityPreset,
} from '../../Models/types';
import { sendMessageToBackend } from '../../Utils/MessageUtils';
import { useAppState } from '../../Context/AppStateContext';
import HevcExtensionWarning from '../HevcExtensionWarning';

interface ClipSettingsSectionProps {
  settings: SettingsType;
  updateSettings: (updates: Partial<SettingsType>) => void;
}

export default function ClipSettingsSection({
  settings,
  updateSettings,
}: ClipSettingsSectionProps) {
  const { t } = useTranslation('clips');
  const appState = useAppState();

  // GPU quality (CQ/QP) options. Nvidia and AMD share the 0-51 scale; Intel's ICQ scale starts at 1.
  const GPU_QUALITY_OPTIONS = [
    { value: '0', label: t('clipSettings.quality.highest', { value: '0' }) },
    { value: '10', label: '10' },
    { value: '15', label: '15' },
    { value: '20', label: t('clipSettings.quality.high', { value: '20' }) },
    { value: '23', label: t('clipSettings.quality.normal', { value: '23' }) },
    { value: '26', label: '26' },
    { value: '30', label: t('clipSettings.quality.low', { value: '30' }) },
    { value: '35', label: '35' },
    { value: '40', label: '40' },
    { value: '45', label: '45' },
    { value: '51', label: t('clipSettings.quality.lowest', { value: '51' }) },
  ];

  const INTEL_GPU_QUALITY_OPTIONS = [
    { value: '1', label: t('clipSettings.quality.highest', { value: '1' }) },
    ...GPU_QUALITY_OPTIONS.slice(1),
  ];

  const DEFAULT_GPU_QUALITY_OPTIONS = [
    { value: '23', label: t('clipSettings.quality.normal', { value: '23' }) },
  ];

  // Helper function to get available presets based on encoder settings
  const getAvailablePresets = (
    encoder: string,
    codec: string,
    gpuVendor: GpuVendor,
  ): Array<{ value: string; label: string }> => {
    if (encoder === 'cpu') {
      return [
        { value: 'ultrafast', label: t('presetOptions.ultrafast') },
        { value: 'superfast', label: t('presetOptions.superfast') },
        { value: 'veryfast', label: t('presetOptions.veryfast') },
        { value: 'faster', label: t('presetOptions.faster') },
        { value: 'fast', label: t('presetOptions.fast') },
        { value: 'medium', label: t('presetOptions.medium') },
        { value: 'slow', label: t('presetOptions.slow') },
        { value: 'slower', label: t('presetOptions.slower') },
        { value: 'veryslow', label: t('presetOptions.veryslow') },
      ];
    }

    switch (gpuVendor) {
      case GpuVendor.Nvidia:
        // AV1 NVENC uses different presets (p1-p7)
        if (codec === 'av1') {
          return [
            { value: 'p1', label: t('presetOptions.p1') },
            { value: 'p2', label: 'P2' },
            { value: 'p3', label: 'P3' },
            { value: 'p4', label: t('presetOptions.p4') },
            { value: 'p5', label: 'P5' },
            { value: 'p6', label: 'P6' },
            { value: 'p7', label: t('presetOptions.p7') },
          ];
        }
        return [
          { value: 'slow', label: t('presetOptions.slow') },
          { value: 'medium', label: t('presetOptions.medium') },
          { value: 'fast', label: t('presetOptions.fast') },
        ];
      case GpuVendor.AMD:
        return [
          { value: 'quality', label: t('presetOptions.quality') },
          { value: 'transcoding', label: t('presetOptions.transcoding') },
          { value: 'lowlatency', label: t('presetOptions.lowLatency') },
          { value: 'ultralowlatency', label: t('presetOptions.ultraLowLatency') },
        ];
      case GpuVendor.Intel:
        return [
          { value: 'fast', label: t('presetOptions.fast') },
          { value: 'medium', label: t('presetOptions.medium') },
          { value: 'slow', label: t('presetOptions.slow') },
        ];
      default:
        return [];
    }
  };

  const handlePresetChange = (preset: ClipQualityPreset) => {
    sendMessageToBackend('ApplyClipPreset', { preset });
  };

  return (
    <div className="p-4 bg-base-300 rounded-lg shadow-md border border-base-400">
      <h2 className="text-xl font-semibold mb-4">{t('clipSettings.title')}</h2>

      {/* Quality Preset Selector */}
      <div className="mb-4">
        <div className="grid grid-cols-4 gap-3">
          <div
            className={`bg-base-200 p-3 rounded-lg flex flex-col items-center justify-center transition-all transition-200 border cursor-pointer hover:bg-base-300 ${
              settings.clipQualityPreset === 'low' ? 'border-primary' : 'border-base-400'
            }`}
            onClick={() => handlePresetChange('low')}
          >
            <div className="text-sm font-semibold">
              {t('clipSettings.qualityPresets.low.title')}
            </div>
            <div className="text-xs opacity-70 mt-1">
              {t('clipSettings.qualityPresets.low.hint')}
            </div>
          </div>
          <div
            className={`bg-base-200 p-3 rounded-lg flex flex-col items-center justify-center transition-all transition-200 border cursor-pointer hover:bg-base-300 ${
              settings.clipQualityPreset === 'standard' ? 'border-primary' : 'border-base-400'
            }`}
            onClick={() => handlePresetChange('standard')}
          >
            <div className="text-sm font-semibold">
              {t('clipSettings.qualityPresets.standard.title')}
            </div>
            <div className="text-xs opacity-70 mt-1">
              {t('clipSettings.qualityPresets.standard.hint')}
            </div>
          </div>
          <div
            className={`bg-base-200 p-3 rounded-lg flex flex-col items-center justify-center transition-all transition-200 border cursor-pointer hover:bg-base-300 ${
              settings.clipQualityPreset === 'high' ? 'border-primary' : 'border-base-400'
            }`}
            onClick={() => handlePresetChange('high')}
          >
            <div className="text-sm font-semibold">
              {t('clipSettings.qualityPresets.high.title')}
            </div>
            <div className="text-xs opacity-70 mt-1">
              {t('clipSettings.qualityPresets.high.hint')}
            </div>
          </div>
          <div
            className={`bg-base-200 p-3 rounded-lg flex flex-col items-center justify-center transition-all transition-200 border cursor-pointer hover:bg-base-300 ${
              settings.clipQualityPreset === 'custom' ? 'border-primary' : 'border-base-400'
            }`}
            onClick={() => handlePresetChange('custom')}
          >
            <div className="text-sm font-semibold">
              {t('clipSettings.qualityPresets.custom.title')}
            </div>
            <div className="text-xs opacity-70 mt-1">
              {t('clipSettings.qualityPresets.custom.hint')}
            </div>
          </div>
        </div>
      </div>

      {/* Advanced Settings - Only show when Custom preset is selected */}
      <AnimatePresence initial={false}>
        {settings.clipQualityPreset === 'custom' && (
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
              {/* Encoder */}
              <div className="form-control">
                <label className="label">
                  <span className="label-text text-base-content">
                    {t('clipSettings.encoder.label')}
                  </span>
                </label>
                <DropdownSelect
                  items={[
                    { value: 'cpu', label: 'CPU' },
                    ...(appState.gpuVendor !== GpuVendor.Unknown
                      ? [{ value: 'gpu', label: 'GPU' }]
                      : []),
                  ]}
                  value={settings.clipEncoder}
                  onChange={(val) => {
                    const newSettings: Partial<SettingsType> = {
                      clipEncoder: val as 'cpu' | 'gpu',
                    };
                    if (val === 'cpu' && settings.clipEncoder !== 'cpu') {
                      newSettings.clipPreset = 'veryfast' as ClipPreset;
                      // CPU doesn't support AV1, switch to H.264
                      if (settings.clipCodec === 'av1') {
                        newSettings.clipCodec = 'h264';
                      }
                    } else if (val === 'gpu' && settings.clipEncoder !== 'gpu') {
                      // Set default preset based on GPU vendor
                      switch (appState.gpuVendor) {
                        case GpuVendor.AMD:
                          newSettings.clipPreset = 'transcoding' as ClipPreset;
                          break;
                        case GpuVendor.Intel:
                          newSettings.clipPreset = 'medium' as ClipPreset;
                          break;
                        case GpuVendor.Nvidia:
                        default:
                          newSettings.clipPreset = 'medium' as ClipPreset;
                          break;
                      }
                    }
                    updateSettings(newSettings);
                  }}
                />
              </div>

              {/* Quality Control - Different for CPU vs GPU */}
              {settings.clipEncoder === 'cpu' ? (
                <div className="form-control">
                  <label className="label">
                    <span className="label-text text-base-content">
                      {t('clipSettings.quality.label')}
                    </span>
                    <span className="text-xs opacity-60">CRF</span>
                  </label>
                  <DropdownSelect
                    items={[
                      { value: '17', label: t('clipSettings.quality.highest', { value: '17' }) },
                      { value: '18', label: '18' },
                      { value: '19', label: '19' },
                      { value: '20', label: t('clipSettings.quality.high', { value: '20' }) },
                      { value: '21', label: '21' },
                      { value: '22', label: '22' },
                      { value: '23', label: t('clipSettings.quality.normal', { value: '23' }) },
                      { value: '24', label: '24' },
                      { value: '25', label: '25' },
                      { value: '26', label: t('clipSettings.quality.low', { value: '26' }) },
                      { value: '27', label: '27' },
                      { value: '28', label: t('clipSettings.quality.lowest', { value: '28' }) },
                    ]}
                    value={String(settings.clipQualityCpu)}
                    onChange={(val) => updateSettings({ clipQualityCpu: Number(val) })}
                  />
                </div>
              ) : (
                <div className="form-control">
                  <label className="label">
                    <span className="label-text text-base-content">
                      {t('clipSettings.quality.label')}
                    </span>
                    <span className="text-xs opacity-60">
                      {appState.gpuVendor === GpuVendor.AMD
                        ? 'QP'
                        : appState.gpuVendor === GpuVendor.Intel
                          ? 'ICQ'
                          : 'CQ'}
                    </span>
                  </label>
                  <DropdownSelect
                    items={
                      appState.gpuVendor === GpuVendor.Nvidia ||
                      appState.gpuVendor === GpuVendor.AMD
                        ? GPU_QUALITY_OPTIONS
                        : appState.gpuVendor === GpuVendor.Intel
                          ? INTEL_GPU_QUALITY_OPTIONS
                          : DEFAULT_GPU_QUALITY_OPTIONS
                    }
                    value={String(settings.clipQualityGpu)}
                    onChange={(val) => updateSettings({ clipQualityGpu: Number(val) })}
                  />
                </div>
              )}

              {/* Codec */}
              <div className="form-control">
                <label className="label">
                  <span className="label-text text-base-content">
                    {t('clipSettings.codec.label')}
                  </span>
                </label>
                <DropdownSelect
                  items={[
                    { value: 'h264', label: 'H.264' },
                    { value: 'h265', label: 'H.265' },
                    ...(settings.clipEncoder === 'gpu' &&
                    appState.codecs.find((c) => c.internalEncoderId.includes('av1'))
                      ? [{ value: 'av1', label: 'AV1' }]
                      : []),
                  ]}
                  value={settings.clipCodec}
                  onChange={(val) => {
                    const newCodec = val as 'h264' | 'h265' | 'av1';
                    const updates: Partial<SettingsType> = { clipCodec: newCodec };

                    // Auto-adjust preset when switching to/from AV1 on NVIDIA
                    if (appState.gpuVendor === GpuVendor.Nvidia) {
                      if (
                        newCodec === 'av1' &&
                        !['p1', 'p2', 'p3', 'p4', 'p5', 'p6', 'p7'].includes(settings.clipPreset)
                      ) {
                        // Switching to AV1, set default AV1 preset
                        updates.clipPreset = 'p4' as ClipPreset;
                      } else if (
                        newCodec !== 'av1' &&
                        ['p1', 'p2', 'p3', 'p4', 'p5', 'p6', 'p7'].includes(settings.clipPreset)
                      ) {
                        // Switching from AV1 to H.264/H.265, set default preset
                        updates.clipPreset = 'medium' as ClipPreset;
                      }
                    }

                    updateSettings(updates);
                  }}
                  disabled={!appState.hasLoadedObs}
                />
              </div>

              {/* FPS */}
              <div className="form-control">
                <label className="label">
                  <span className="label-text text-base-content">
                    {t('clipSettings.frameRate.label')}
                  </span>
                </label>
                <DropdownSelect
                  items={[
                    { value: '0', label: t('clipSettings.frameRate.original') },
                    { value: '24', label: t('clipSettings.frameRate.fps', { value: '24' }) },
                    { value: '30', label: t('clipSettings.frameRate.fps', { value: '30' }) },
                    { value: '60', label: t('clipSettings.frameRate.fps', { value: '60' }) },
                    { value: '120', label: t('clipSettings.frameRate.fps', { value: '120' }) },
                    { value: '144', label: t('clipSettings.frameRate.fps', { value: '144' }) },
                  ]}
                  value={String(settings.clipFps)}
                  onChange={(val) => updateSettings({ clipFps: Number(val) as ClipFPS })}
                />
              </div>

              {/* Audio Quality */}
              <div className="form-control">
                <label className="label">
                  <span className="label-text text-base-content">
                    {t('clipSettings.audioQuality.label')}
                  </span>
                </label>
                <DropdownSelect
                  items={[
                    { value: '96k', label: t('clipSettings.audioQuality.low', { value: '96' }) },
                    {
                      value: '128k',
                      label: t('clipSettings.audioQuality.medium', { value: '128' }),
                    },
                    { value: '192k', label: t('clipSettings.audioQuality.high', { value: '192' }) },
                    {
                      value: '256k',
                      label: t('clipSettings.audioQuality.veryHigh', { value: '256' }),
                    },
                    {
                      value: '320k',
                      label: t('clipSettings.audioQuality.insane', { value: '320' }),
                    },
                  ]}
                  value={settings.clipAudioQuality}
                  onChange={(val) =>
                    updateSettings({
                      clipAudioQuality: val as '96k' | '128k' | '192k' | '256k' | '320k',
                    })
                  }
                />
              </div>

              {/* Preset */}
              <div className="form-control">
                <label className="label">
                  <span className="label-text text-base-content">
                    {t('clipSettings.preset.label')}
                  </span>
                </label>
                <DropdownSelect
                  items={getAvailablePresets(
                    settings.clipEncoder,
                    settings.clipCodec,
                    appState.gpuVendor,
                  )}
                  value={settings.clipPreset}
                  onChange={(val) => updateSettings({ clipPreset: val as ClipPreset })}
                />
              </div>
            </div>
          </motion.div>
        )}
      </AnimatePresence>

      {settings.clipCodec === 'h265' && <HevcExtensionWarning className="mt-3" />}

      {/* Keep Separate Audio Tracks */}
      {settings.enableSeparateAudioTracks && (
        <label className="flex items-center gap-3 cursor-pointer p-3 bg-base-200 rounded-lg border border-base-400 mt-4">
          <input
            type="checkbox"
            name="clipKeepSeparateAudioTracks"
            checked={settings.clipKeepSeparateAudioTracks}
            onChange={(e) => updateSettings({ clipKeepSeparateAudioTracks: e.target.checked })}
            className="checkbox checkbox-primary checkbox-sm"
          />
          <div>
            <div className="font-semibold">{t('clipSettings.audioTracks.label')}</div>
            <div className="text-sm opacity-70 mt-0.5">
              {t('clipSettings.audioTracks.description')}
            </div>
          </div>
        </label>
      )}
    </div>
  );
}
