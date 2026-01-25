import { useSettings } from './Context/SettingsContext';
import RecordingCard from './Components/RecordingCard';
import CircularProgress from './Components/CircularProgress';
import { sendMessageToBackend } from './Utils/MessageUtils';
import { useUploads } from './Context/UploadContext';
import { useImports } from './Context/ImportContext';
import { useClipping } from './Context/ClippingContext';
import { useUpdate } from './Context/UpdateContext';
import { useObsDownload } from './Context/ObsDownloadContext';
import { useAiHighlights } from './Context/AiHighlightsContext';
import UploadCard from './Components/UploadCard';
import ImportCard from './Components/ImportCard';
import ClippingCard from './Components/ClippingCard';
import UpdateCard from './Components/UpdateCard';
import UnavailableDeviceCard from './Components/UnavailableDeviceCard';
import AnimatedCard from './Components/AnimatedCard';
import {
  MdOutlineContentCut,
  MdOutlinePlayCircleOutline,
  MdOutlineSettings,
  MdReplay30,
} from 'react-icons/md';
import { HiOutlineSparkles } from 'react-icons/hi';
import { AnimatePresence, motion } from 'framer-motion';
import { useRef, useEffect, useState } from 'react';

interface MenuProps {
  selectedMenu: string;
  onSelectMenu: (menu: string) => void;
}

export default function Menu({ selectedMenu, onSelectMenu }: MenuProps) {
  const settings = useSettings();
  const { hasLoadedObs, recording, preRecording } = settings.state;
  const { updateInfo } = useUpdate();
  const { aiProgress } = useAiHighlights();
  const { obsDownloadProgress } = useObsDownload();

  // Create refs for each menu button
  const sessionsRef = useRef<HTMLButtonElement>(null);
  const replayRef = useRef<HTMLButtonElement>(null);
  const clipsRef = useRef<HTMLButtonElement>(null);
  const highlightsRef = useRef<HTMLButtonElement>(null);
  const settingsRef = useRef<HTMLButtonElement>(null);

  // State to store the indicator position
  const [indicatorPosition, setIndicatorPosition] = useState({ top: 12 });

  // Update indicator position when selected menu changes
  useEffect(() => {
    const getRefForMenu = () => {
      switch (selectedMenu) {
        case 'Full Sessions':
          return sessionsRef;
        case 'Replay Buffer':
          return replayRef;
        case 'Clips':
          return clipsRef;
        case 'Highlights':
          return highlightsRef;
        case 'Settings':
          return settingsRef;
        default:
          return sessionsRef;
      }
    };

    const activeRef = getRefForMenu();
    if (activeRef.current) {
      const parentRect = activeRef.current.parentElement?.getBoundingClientRect();
      const buttonRect = activeRef.current.getBoundingClientRect();

      if (parentRect) {
        // Calculate relative position to parent with vertical centering
        // Center the 40px indicator with the button
        const buttonCenter = buttonRect.top - parentRect.top + buttonRect.height / 2;
        const indicatorTop = buttonCenter - 20; // 20px is half of the 40px height

        setIndicatorPosition({
          top: indicatorTop,
        });
      }
    }
  }, [selectedMenu]);

  // Check if there are any active AI highlight generations and calculate average progress
  const aiProgressValues = Object.values(aiProgress);
  const hasActiveAiHighlights = aiProgressValues.length > 0;
  const averageAiProgress = hasActiveAiHighlights
    ? Math.round(aiProgressValues.reduce((sum, p) => sum + p.progress, 0) / aiProgressValues.length)
    : 0;

  const hasUnavailableDevices = () => {
    const unavailableInput = settings.inputDevices.some(
      (deviceSetting: { id: string }) =>
        !settings.state.inputDevices.some((d) => d.id === deviceSetting.id),
    );
    const unavailableOutput = settings.outputDevices.some(
      (deviceSetting: { id: string }) =>
        !settings.state.outputDevices.some((d) => d.id === deviceSetting.id),
    );
    return unavailableInput || unavailableOutput;
  };

  return (
    <div className="bg-base-300 w-56 h-screen flex flex-col border-r border-custom">
      {/* Menu Items */}
      <div className="flex flex-col space-y-2 px-4 text-left py-2 relative mt-2">
        {/* Selection indicator rectangle */}
        <div
          className="absolute w-1.5 bg-primary rounded-r transition-all duration-200 ease-in-out"
          style={{
            left: 0,
            top: `${indicatorPosition.top}px`,
            height: '40px',
          }}
        />
        <button
          ref={sessionsRef}
          className={`btn btn-secondary ${selectedMenu === 'Full Sessions' ? 'text-primary' : ''} w-full justify-start border-base-400 hover:border-base-400 hover:text-primary hover:border-opacity-75 py-3 text-gray-300`}
          onMouseDown={() => onSelectMenu('Full Sessions')}
        >
          <MdOutlinePlayCircleOutline className="w-6 h-6" />
          Full Sessions
        </button>
        <button
          ref={replayRef}
          className={`btn btn-secondary ${selectedMenu === 'Replay Buffer' ? 'text-primary' : ''} w-full justify-start border-base-400 hover:border-base-400 hover:text-primary hover:border-opacity-75 py-3 text-gray-300`}
          onMouseDown={() => onSelectMenu('Replay Buffer')}
        >
          <MdReplay30 className="w-6 h-6" />
          Replay Buffer
        </button>
        <button
          ref={clipsRef}
          className={`btn btn-secondary ${selectedMenu === 'Clips' ? 'text-primary' : ''} w-full justify-start border-base-400 hover:border-base-400 hover:text-primary hover:border-opacity-75 py-3 text-gray-300`}
          onMouseDown={() => onSelectMenu('Clips')}
        >
          <MdOutlineContentCut className="w-6 h-6" />
          Clips
        </button>
        <button
          ref={highlightsRef}
          className={`btn btn-secondary ${selectedMenu === 'Highlights' ? 'text-primary' : ''} w-full justify-between border-base-400 hover:border-base-400 hover:text-primary hover:border-opacity-75 py-3 text-gray-300`}
          onMouseDown={() => onSelectMenu('Highlights')}
        >
          <span className="flex items-center gap-2">
            <HiOutlineSparkles className="w-6 h-6" />
            Highlights
          </span>
          <AnimatePresence>
            {hasActiveAiHighlights && selectedMenu !== 'Highlights' && (
              <motion.div
                initial={{ opacity: 0 }}
                animate={{ opacity: 1 }}
                exit={{ opacity: 0 }}
                transition={{ duration: 0.2 }}
              >
                <CircularProgress progress={averageAiProgress} size={24} strokeWidth={2} />
              </motion.div>
            )}
          </AnimatePresence>
        </button>
        <button
          ref={settingsRef}
          className={`btn btn-secondary ${selectedMenu === 'Settings' ? 'text-primary' : ''} w-full justify-start border-base-400 hover:border-base-400 hover:text-primary hover:border-opacity-75 py-3 text-gray-300`}
          onMouseDown={() => onSelectMenu('Settings')}
        >
          <MdOutlineSettings className="w-6 h-6" />
          Settings
        </button>
      </div>

      {/* Spacer to push content to the bottom */}
      <div className="grow"></div>

      {/* Status Cards */}
      <div className="mt-auto p-2 space-y-2">
        <AnimatePresence>
          {updateInfo && (
            <AnimatedCard key="update-card">
              <UpdateCard />
            </AnimatedCard>
          )}
        </AnimatePresence>

        <AnimatePresence>
          {Object.values(useUploads().uploads).map((file) => (
            <AnimatedCard key={file.fileName}>
              <UploadCard upload={file} />
            </AnimatedCard>
          ))}
        </AnimatePresence>

        <AnimatePresence>
          {Object.values(useImports().imports).map((importItem) => (
            <AnimatedCard key={importItem.id}>
              <ImportCard importItem={importItem} />
            </AnimatedCard>
          ))}
        </AnimatePresence>

        {/* Show warning if there are unavailable audio devices */}
        <AnimatePresence>
          {hasUnavailableDevices() && (
            <AnimatedCard key="unavailable-device-card">
              <UnavailableDeviceCard />
            </AnimatedCard>
          )}
        </AnimatePresence>

        <AnimatePresence>
          {(preRecording || (recording && recording.endTime == null)) && (
            <AnimatedCard key="recording-card">
              <RecordingCard recording={recording} preRecording={preRecording} />
            </AnimatedCard>
          )}
        </AnimatePresence>

        <AnimatePresence>
          {Object.values(useClipping().clippingProgress).map((clipping) => (
            <AnimatedCard key={clipping.id}>
              <ClippingCard clipping={clipping} />
            </AnimatedCard>
          ))}
        </AnimatePresence>
      </div>

      {/* OBS Loading Section */}
      {!hasLoadedObs && (
        <div className="mb-4 flex flex-col items-center px-4">
          {obsDownloadProgress !== null && obsDownloadProgress < 100 ? (
            <>
              <p className="text-center text-sm text-gray-300 mb-2">Downloading OBS</p>
              <div className="w-full bg-base-200 rounded-full h-1.5">
                <div
                  className="h-1.5 rounded-full bg-primary transition-all duration-300"
                  style={{ width: `${obsDownloadProgress}%` }}
                ></div>
              </div>
              <p className="text-gray-500 text-xs mt-1">{obsDownloadProgress}%</p>
            </>
          ) : (
            <>
              <div
                style={{
                  width: '3.5rem',
                  height: '2rem',
                }}
                className="loading loading-infinity"
              ></div>
              <p className="text-center mt-2 disabled">Starting OBS</p>
            </>
          )}
        </div>
      )}

      {/* Start and Stop Buttons */}
      <div className="mb-4 px-4">
        <div className="flex flex-col items-center">
          {settings.state.recording ? (
            <button
              className="btn btn-secondary border-base-400 hover:border-base-400 disabled:border-base-400 disabled:bg-base-300 hover:text-accent hover:border-opacity-75 w-full h-12 text-gray-300"
              disabled={!settings.state.hasLoadedObs || (recording && recording.endTime !== null)}
              onClick={() => sendMessageToBackend('StopRecording')}
            >
              Stop Recording
            </button>
          ) : (
            <button
              className="btn btn-secondary border-base-400 hover:border-base-400 disabled:border-base-400 disabled:bg-base-300 hover:text-accent hover:border-opacity-75 w-full h-12 text-gray-300"
              disabled={!settings.state.hasLoadedObs || settings.state.preRecording != null}
              onClick={() => sendMessageToBackend('StartRecording')}
            >
              Start Manually
            </button>
          )}
        </div>
      </div>
    </div>
  );
}
