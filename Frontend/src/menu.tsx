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
  MdChevronLeft,
  MdChevronRight,
  MdFiberManualRecord,
  MdStop
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
  const [isCollapsed, setIsCollapsed] = useState(false);

  // Update indicator position when selected menu changes
  useEffect(() => {
    const getRefForMenu = () => {
      switch (selectedMenu) {
        case 'Full Sessions': // Assuming 'Session' maps to this or handled elsewhere
        case 'Session':
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
  }, [selectedMenu, isCollapsed]); // Re-calc on collapse change too

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
    <div className={`bg-base-300 ${isCollapsed ? 'w-16' : 'w-56'} h-screen flex flex-col border-r border-custom transition-all duration-300 ease-in-out z-20`}>

      {/* Collapse Toggle */}
      <div className={`flex ${isCollapsed ? 'justify-center' : 'justify-end'} px-2 pt-2`}>
        <button
          onClick={() => setIsCollapsed(!isCollapsed)}
          className="btn btn-ghost btn-sm btn-circle text-gray-400 hover:text-white"
        >
          {isCollapsed ? <MdChevronRight className="w-5 h-5" /> : <MdChevronLeft className="w-5 h-5" />}
        </button>
      </div>

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
          className={`btn btn-secondary ${selectedMenu === 'Session' ? 'text-primary' : ''} w-full justify-start border-base-400 hover:border-base-400 hover:text-primary hover:border-opacity-75 py-3 text-gray-300 ${isCollapsed ? 'px-0 justify-center' : ''}`}
          onMouseDown={() => onSelectMenu('Session')}
          title={isCollapsed ? "Session" : ""}
        >
          <MdOutlinePlayCircleOutline className="w-6 h-6 shrink-0" />
          {!isCollapsed && <span className="ml-2 truncate">Session</span>}
        </button>
        <button
          ref={replayRef}
          className={`btn btn-secondary ${selectedMenu === 'Replay Buffer' ? 'text-primary' : ''} w-full justify-start border-base-400 hover:border-base-400 hover:text-primary hover:border-opacity-75 py-3 text-gray-300 ${isCollapsed ? 'px-0 justify-center' : ''}`}
          onMouseDown={() => onSelectMenu('Replay Buffer')}
          title={isCollapsed ? "Replay Buffer" : ""}
        >
          <MdReplay30 className="w-6 h-6 shrink-0" />
          {!isCollapsed && <span className="ml-2 truncate">Replay Buffer</span>}
        </button>
        <button
          ref={clipsRef}
          className={`btn btn-secondary ${selectedMenu === 'Clips' ? 'text-primary' : ''} w-full justify-start border-base-400 hover:border-base-400 hover:text-primary hover:border-opacity-75 py-3 text-gray-300 ${isCollapsed ? 'px-0 justify-center' : ''}`}
          onMouseDown={() => onSelectMenu('Clips')}
          title={isCollapsed ? "Clips" : ""}
        >
          <MdOutlineContentCut className="w-6 h-6 shrink-0" />
          {!isCollapsed && <span className="ml-2 truncate">Clips</span>}
        </button>
        <button
          ref={highlightsRef}
          className={`btn btn-secondary relative ${selectedMenu === 'Highlights' ? 'text-primary' : ''} w-full border-base-400 hover:border-base-400 hover:text-primary hover:border-opacity-75 py-3 text-gray-300 ${isCollapsed ? 'justify-center px-0' : 'justify-between'}`}
          onMouseDown={() => onSelectMenu('Highlights')}
          title={isCollapsed ? "Highlights" : ""}
        >
          <div className="flex items-center">
            <HiOutlineSparkles className="w-6 h-6 shrink-0" />
            {!isCollapsed && <span className="ml-2 truncate">Highlights</span>}
          </div>
          <AnimatePresence>
            {hasActiveAiHighlights && selectedMenu !== 'Highlights' && (
              <motion.div
                initial={{ opacity: 0 }}
                animate={{ opacity: 1 }}
                exit={{ opacity: 0 }}
                transition={{ duration: 0.2 }}
                className={isCollapsed ? "absolute top-0 right-0" : ""}
              >
                <CircularProgress progress={averageAiProgress} size={isCollapsed ? 12 : 24} strokeWidth={2} />
              </motion.div>
            )}
          </AnimatePresence>
        </button>
        <button
          ref={settingsRef}
          className={`btn btn-secondary ${selectedMenu === 'Settings' ? 'text-primary' : ''} w-full justify-start border-base-400 hover:border-base-400 hover:text-primary hover:border-opacity-75 py-3 text-gray-300 ${isCollapsed ? 'px-0 justify-center' : ''}`}
          onMouseDown={() => onSelectMenu('Settings')}
          title={isCollapsed ? "Settings" : ""}
        >
          <MdOutlineSettings className="w-6 h-6 shrink-0" />
          {!isCollapsed && <span className="ml-2 truncate">Settings</span>}
        </button>
      </div>

      {/* Spacer to push content to the bottom */}
      <div className="grow"></div>

      {/* Status Cards - Hide detailed cards when collapsed? Or just render icons/minimized versions?
          For now, we'll just hide them or let them look squished if not hidden.
          Given they are cards, squishing is bad. I will hide them if collapsed, or show a summary dot.
      */}
      {!isCollapsed && (
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
      )}

      {/* OBS Loading Section */}
      {!hasLoadedObs && (
        <div className={`mb-4 flex flex-col items-center px-4 ${isCollapsed ? 'hidden' : ''}`}>
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
      <div className={`mb-4 px-4 ${isCollapsed ? 'px-2' : ''}`}>
        <div className="flex flex-col items-center">
          {settings.state.recording ? (
            <button
              className={`btn btn-secondary border-base-400 hover:border-base-400 disabled:border-base-400 disabled:bg-base-300 hover:text-accent hover:border-opacity-75 w-full h-12 text-gray-300 ${isCollapsed ? 'px-0 justify-center' : ''}`}
              disabled={!settings.state.hasLoadedObs || (recording && recording.endTime !== null)}
              onClick={() => sendMessageToBackend('StopRecording')}
              title="Stop Recording"
            >
              <MdStop className="w-6 h-6 shrink-0" />
              {!isCollapsed && <span className="ml-2">Stop Recording</span>}
            </button>
          ) : (
            <button
              className={`btn btn-secondary border-base-400 hover:border-base-400 disabled:border-base-400 disabled:bg-base-300 hover:text-accent hover:border-opacity-75 w-full h-12 text-gray-300 ${isCollapsed ? 'px-0 justify-center' : ''}`}
              disabled={!settings.state.hasLoadedObs || settings.state.preRecording != null}
              onClick={() => sendMessageToBackend('StartRecording')}
              title="Start Manually"
            >
              <MdFiberManualRecord className="w-6 h-6 shrink-0" />
              {!isCollapsed && <span className="ml-2">Start Manually</span>}
            </button>
          )}
        </div>
      </div>
    </div>
  );
}
