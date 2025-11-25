import { motion, AnimatePresence } from 'framer-motion';
import DropdownSelect from '../DropdownSelect';
import { Settings as SettingsType, GpuVendor, ClipFPS, ClipPreset, ClipQualityPreset } from '../../Models/types';
import { sendMessageToBackend } from '../../Utils/MessageUtils';

interface ClipSettingsSectionProps {
  settings: SettingsType;
  updateSettings: (updates: Partial<SettingsType>) => void;
}

export default function ClipSettingsSection({ settings, updateSettings }: ClipSettingsSectionProps) {
  // Helper function to get available presets based on encoder settings
  const getAvailablePresets = (
    encoder: string,
    codec: string,
    gpuVendor: GpuVendor,
  ): Array<{ value: string; label: string }> => {
    if (encoder === 'cpu') {
      return [
        { value: 'ultrafast', label: 'Ultrafast' },
        { value: 'superfast', label: 'Superfast' },
        { value: 'veryfast', label: 'Veryfast' },
        { value: 'faster', label: 'Faster' },
        { value: 'fast', label: 'Fast' },
        { value: 'medium', label: 'Medium' },
        { value: 'slow', label: 'Slow' },
        { value: 'slower', label: 'Slower' },
        { value: 'veryslow', label: 'Veryslow' },
      ];
    }

    switch (gpuVendor) {
      case GpuVendor.Nvidia:
        // AV1 NVENC uses different presets (p1-p7)
        if (codec === 'av1') {
          return [
            { value: 'p1', label: 'P1 (Fastest)' },
            { value: 'p2', label: 'P2' },
            { value: 'p3', label: 'P3' },
            { value: 'p4', label: 'P4 (Balanced)' },
            { value: 'p5', label: 'P5' },
            { value: 'p6', label: 'P6' },
            { value: 'p7', label: 'P7 (Slowest/Best Quality)' },
          ];
        }
        return [
          { value: 'slow', label: 'Slow' },
          { value: 'medium', label: 'Medium' },
          { value: 'fast', label: 'Fast' },
          { value: 'hp', label: 'High Performance' },
          { value: 'hq', label: 'High Quality' },
          { value: 'bd', label: 'Blu-ray Disk' },
          { value: 'll', label: 'Low Latency' },
          { value: 'llhq', label: 'Low Latency High Quality' },
          { value: 'llhp', label: 'Low Latency High Performance' },
          { value: 'lossless', label: 'Lossless' },
          { value: 'losslesshp', label: 'Lossless High Performance' },
        ];
      case GpuVendor.AMD:
        return [
          { value: 'quality', label: 'Quality' },
          { value: 'transcoding', label: 'Transcoding (Balanced)' },
          { value: 'lowlatency', label: 'Low Latency (Fast)' },
          { value: 'ultralowlatency', label: 'Ultra Low Latency (Fastest)' },
        ];
      case GpuVendor.Intel:
        return [
          { value: 'fast', label: 'Fast' },
          { value: 'medium', label: 'Medium' },
          { value: 'slow', label: 'Slow' },
        ];
      default:
        return [];
    }
  };

  const handlePresetChange = (preset: ClipQualityPreset) => {
    sendMessageToBackend('ApplyClipPreset', { preset });
  };

  return (
    <div className="p-4 bg-base-300 rounded-lg shadow-md border border-custom">
      <h2 className="text-xl font-semibold mb-4">Clip Settings</h2>

      {/* Quality Preset Selector */}
      <div className="mb-4">
        <div className="grid grid-cols-4 gap-3">
          <div
            className={`bg-base-200 p-3 rounded-lg flex flex-col items-center justify-center transition-all transition-200 border cursor-pointer hover:bg-base-300 ${
              settings.clipQualityPreset === 'low' ? 'border-primary' : 'border-base-400'
            }`}
            onClick={() => handlePresetChange('low')}
          >
            <div className="text-sm font-semibold">Low Quality</div>
            <div className="text-xs text-base-content text-opacity-70 mt-1">Fast • 30fps</div>
          </div>
          <div
            className={`bg-base-200 p-3 rounded-lg flex flex-col items-center justify-center transition-all transition-200 border cursor-pointer hover:bg-base-300 ${
              settings.clipQualityPreset === 'standard' ? 'border-primary' : 'border-base-400'
            }`}
            onClick={() => handlePresetChange('standard')}
          >
            <div className="text-sm font-semibold">Standard</div>
            <div className="text-xs text-base-content text-opacity-70 mt-1">Balanced • 60fps</div>
          </div>
          <div
            className={`bg-base-200 p-3 rounded-lg flex flex-col items-center justify-center transition-all transition-200 border cursor-pointer hover:bg-base-300 ${
              settings.clipQualityPreset === 'high' ? 'border-primary' : 'border-base-400'
            }`}
            onClick={() => handlePresetChange('high')}
          >
            <div className="text-sm font-semibold">High Quality</div>
            <div className="text-xs text-base-content text-opacity-70 mt-1">Quality • 60fps</div>
          </div>
          <div
            className={`bg-base-200 p-3 rounded-lg flex flex-col items-center justify-center transition-all transition-200 border cursor-pointer hover:bg-base-300 ${
              settings.clipQualityPreset === 'custom' ? 'border-primary' : 'border-base-400'
            }`}
            onClick={() => handlePresetChange('custom')}
          >
            <div className="text-sm font-semibold">Custom</div>
            <div className="text-xs text-base-content text-opacity-70 mt-1">Manual config</div>
          </div>
        </div>
      </div>

      {/* Advanced Settings - Only show when Custom preset is selected */}
      <AnimatePresence>
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
                  <span className="label-text text-base-content">Encoder</span>
                </label>
                <DropdownSelect
                  items={[
                    { value: 'cpu', label: 'CPU' },
                    ...(settings.state.gpuVendor !== GpuVendor.Unknown ? [{ value: 'gpu', label: 'GPU' }] : []),
                  ]}
                  value={settings.clipEncoder}
                  onChange={(val) => {
                    const newSettings: Partial<SettingsType> = { clipEncoder: val as 'cpu' | 'gpu' };
                    if (val === 'cpu' && settings.clipEncoder !== 'cpu') {
                      newSettings.clipPreset = 'veryfast' as ClipPreset;
                      // CPU doesn't support AV1, switch to H.264
                      if (settings.clipCodec === 'av1') {
                        newSettings.clipCodec = 'h264';
                      }
                    } else if (val === 'gpu' && settings.clipEncoder !== 'gpu') {
                      // Set default preset based on GPU vendor
                      switch (settings.state.gpuVendor) {
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
                    <span className="label-text text-base-content">Quality (CRF)</span>
                  </label>
                  <DropdownSelect
                    items={[
                      { value: '17', label: '17 (Highest Quality)' },
                      { value: '18', label: '18' },
                      { value: '19', label: '19' },
                      { value: '20', label: '20 (High Quality)' },
                      { value: '21', label: '21' },
                      { value: '22', label: '22' },
                      { value: '23', label: '23 (Normal Quality)' },
                      { value: '24', label: '24' },
                      { value: '25', label: '25' },
                      { value: '26', label: '26 (Low Quality)' },
                      { value: '27', label: '27' },
                      { value: '28', label: '28 (Lowest Quality)' },
                    ]}
                    value={String(settings.clipQualityCpu)}
                    onChange={(val) => updateSettings({ clipQualityCpu: Number(val) })}
                  />
                </div>
              ) : (
                <div className="form-control">
                  <label className="label">
                    <span className="label-text text-base-content">
                      Quality (
                      {settings.state.gpuVendor === GpuVendor.Nvidia
                        ? 'CQ'
                        : settings.state.gpuVendor === GpuVendor.AMD
                          ? 'QP'
                          : settings.state.gpuVendor === GpuVendor.Intel
                            ? 'ICQ'
                            : 'CQ'}
                      )
                    </span>
                  </label>
                  <DropdownSelect
                    items={
                      settings.state.gpuVendor === GpuVendor.Nvidia
                        ? [
                            { value: '0', label: '0 (Highest Quality)' },
                            { value: '10', label: '10' },
                            { value: '15', label: '15' },
                            { value: '20', label: '20 (High Quality)' },
                            { value: '23', label: '23 (Normal Quality)' },
                            { value: '26', label: '26' },
                            { value: '30', label: '30 (Low Quality)' },
                            { value: '35', label: '35' },
                            { value: '40', label: '40' },
                            { value: '45', label: '45' },
                            { value: '51', label: '51 (Lowest Quality)' },
                          ]
                        : settings.state.gpuVendor === GpuVendor.AMD
                          ? [
                              { value: '0', label: '0 (Highest Quality)' },
                              { value: '10', label: '10' },
                              { value: '15', label: '15' },
                              { value: '20', label: '20 (High Quality)' },
                              { value: '23', label: '23 (Normal Quality)' },
                              { value: '26', label: '26' },
                              { value: '30', label: '30 (Low Quality)' },
                              { value: '35', label: '35' },
                              { value: '40', label: '40' },
                              { value: '45', label: '45' },
                              { value: '51', label: '51 (Lowest Quality)' },
                            ]
                          : settings.state.gpuVendor === GpuVendor.Intel
                            ? [
                                { value: '1', label: '1 (Highest Quality)' },
                                { value: '10', label: '10' },
                                { value: '15', label: '15' },
                                { value: '20', label: '20 (High Quality)' },
                                { value: '23', label: '23 (Normal Quality)' },
                                { value: '26', label: '26' },
                                { value: '30', label: '30 (Low Quality)' },
                                { value: '35', label: '35' },
                                { value: '40', label: '40' },
                                { value: '45', label: '45' },
                                { value: '51', label: '51 (Lowest Quality)' },
                              ]
                            : [{ value: '23', label: '23 (Normal Quality)' }]
                    }
                    value={String(settings.clipQualityGpu)}
                    onChange={(val) => updateSettings({ clipQualityGpu: Number(val) })}
                  />
                </div>
              )}

              {/* Codec */}
              <div className="form-control">
                <label className="label">
                  <span className="label-text text-base-content">Codec</span>
                </label>
                <DropdownSelect
                  items={[
                    { value: 'h264', label: 'H.264' },
                    { value: 'h265', label: 'H.265' },
                    ...(settings.clipEncoder === 'gpu' &&
                    settings.state.codecs.find((c) => c.internalEncoderId.includes('av1'))
                      ? [{ value: 'av1', label: 'AV1' }]
                      : []),
                  ]}
                  value={settings.clipCodec}
                  onChange={(val) => {
                    const newCodec = val as 'h264' | 'h265' | 'av1';
                    const updates: Partial<SettingsType> = { clipCodec: newCodec };

                    // Auto-adjust preset when switching to/from AV1 on NVIDIA
                    if (settings.state.gpuVendor === GpuVendor.Nvidia) {
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
                        updates.clipPreset = 'hq' as ClipPreset;
                      }
                    }

                    updateSettings(updates);
                  }}
                  disabled={!settings.state.hasLoadedObs}
                />
              </div>

              {/* FPS */}
              <div className="form-control">
                <label className="label">
                  <span className="label-text text-base-content">FPS</span>
                </label>
                <DropdownSelect
                  items={[
                    { value: '0', label: 'Original FPS' },
                    { value: '24', label: '24 FPS' },
                    { value: '30', label: '30 FPS' },
                    { value: '60', label: '60 FPS' },
                    { value: '120', label: '120 FPS' },
                    { value: '144', label: '144 FPS' },
                  ]}
                  value={String(settings.clipFps)}
                  onChange={(val) => updateSettings({ clipFps: Number(val) as ClipFPS })}
                />
              </div>

              {/* Audio Quality */}
              <div className="form-control">
                <label className="label">
                  <span className="label-text text-base-content">Audio Quality</span>
                </label>
                <DropdownSelect
                  items={[
                    { value: '96k', label: '96 kbps (Low)' },
                    { value: '128k', label: '128 kbps (Medium)' },
                    { value: '192k', label: '192 kbps (High)' },
                    { value: '256k', label: '256 kbps (Very High)' },
                    { value: '320k', label: '320 kbps (Insane)' },
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
                  <span className="label-text text-base-content">Preset</span>
                </label>
                <DropdownSelect
                  items={getAvailablePresets(settings.clipEncoder, settings.clipCodec, settings.state.gpuVendor)}
                  value={settings.clipPreset}
                  onChange={(val) => updateSettings({ clipPreset: val as ClipPreset })}
                />
              </div>
            </div>
          </motion.div>
        )}
      </AnimatePresence>
    </div>
  );
}
