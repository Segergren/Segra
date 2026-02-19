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
  MdOutlineStopCircle,
  MdReplay30,
} from 'react-icons/md';
import { HiOutlineSparkles } from 'react-icons/hi';
import { BsDisplay } from 'react-icons/bs';
import { AnimatePresence, motion } from 'framer-motion';
import { useRef, useEffect, useState } from 'react';
import Button from './Components/Button';

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
  const [buttonCooldown, setButtonCooldown] = useState(false);

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
        deviceSetting.id !== 'default' &&
        !settings.state.inputDevices.some((d) => d.id === deviceSetting.id),
    );
    const unavailableOutput = settings.outputDevices.some(
      (deviceSetting: { id: string }) =>
        deviceSetting.id !== 'default' &&
        !settings.state.outputDevices.some((d) => d.id === deviceSetting.id),
    );
    return unavailableInput || unavailableOutput;
  };

  return (
    <div className="bg-base-300 w-56 h-screen flex flex-col border-r border-base-400">
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
        <Button
          ref={sessionsRef}
          variant="nav"
          className={selectedMenu === 'Full Sessions' ? 'text-primary' : ''}
          onMouseDown={() => onSelectMenu('Full Sessions')}
        >
          <MdOutlinePlayCircleOutline className="w-6 h-6" />
          Full Sessions
        </Button>
        <Button
          ref={replayRef}
          variant="nav"
          className={selectedMenu === 'Replay Buffer' ? 'text-primary' : ''}
          onMouseDown={() => onSelectMenu('Replay Buffer')}
        >
          <MdReplay30 className="w-6 h-6" />
          Replay Buffer
        </Button>
        <Button
          ref={clipsRef}
          variant="nav"
          className={selectedMenu === 'Clips' ? 'text-primary' : ''}
          onMouseDown={() => onSelectMenu('Clips')}
        >
          <MdOutlineContentCut className="w-6 h-6" />
          Clips
        </Button>
        <Button
          ref={highlightsRef}
          variant="nav"
          className={`justify-between ${selectedMenu === 'Highlights' ? 'text-primary' : ''}`}
          onMouseDown={() => onSelectMenu('Highlights')}
        >
          <span className="flex items-center gap-2">
            <HiOutlineSparkles className="w-6 h-6" />
            Highlights
          </span>
          <div className="ml-auto flex items-center">
            <AnimatePresence>
              {hasActiveAiHighlights && selectedMenu !== 'Highlights' && (
                <motion.div
                  className="flex items-center justify-center"
                  initial={{ opacity: 0 }}
                  animate={{ opacity: 1 }}
                  exit={{ opacity: 0 }}
                  transition={{ duration: 0.2 }}
                >
                  <CircularProgress progress={averageAiProgress} size={24} strokeWidth={2} />
                </motion.div>
              )}
            </AnimatePresence>
          </div>
        </Button>
        <Button
          ref={settingsRef}
          variant="nav"
          className={selectedMenu === 'Settings' ? 'text-primary' : ''}
          onMouseDown={() => onSelectMenu('Settings')}
        >
          <MdOutlineSettings className="w-6 h-6" />
          Settings
        </Button>
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
        <div className="flex flex-col items-center z-50">
          <Button
            variant="primary"
            className="w-full h-12"
            disabled={
              buttonCooldown ||
              !settings.state.hasLoadedObs ||
              (settings.state.recording && recording && recording.endTime !== null)
            }
            onClick={() => {
              setButtonCooldown(true);
              setTimeout(() => setButtonCooldown(false), 1000);
              sendMessageToBackend(
                settings.state.recording || settings.state.preRecording
                  ? 'StopRecording'
                  : 'StartRecording',
              );
            }}
          >
            {settings.state.recording || settings.state.preRecording ? (
              <>
                <MdOutlineStopCircle className="w-4.5 h-4.5 -mr-1" />
                Stop
              </>
            ) : (
              <>
                <BsDisplay className="w-4 h-4 mr-0.5" />
                Start Manually
              </>
            )}
          </Button>
        </div>
      </div>
    </div>
  );
}
