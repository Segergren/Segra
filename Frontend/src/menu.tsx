import { useSettings } from './Context/SettingsContext';
import RecordingCard from './Components/RecordingCard';
import { sendMessageToBackend } from './Utils/MessageUtils';
import { useUploads } from './Context/UploadContext';
import { useImports } from './Context/ImportContext';
import { useClipping } from './Context/ClippingContext';
import { useUpdate } from './Context/UpdateContext';
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
import { AnimatePresence } from 'framer-motion';
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

  // Create refs for each menu button
  const sessionsRef = useRef<HTMLButtonElement>(null);
  const replayRef = useRef<HTMLButtonElement>(null);
  const clipsRef = useRef<HTMLButtonElement>(null);
  const highlightsRef = useRef<HTMLButtonElement>(null);
  const settingsRef = useRef<HTMLButtonElement>(null);

  // State to store the indicator position
  const [indicatorPosition, setIndicatorPosition] = useState({ top: 0 });

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

  // Check if there are any active AI highlight generations
  const hasActiveAiHighlights = Object.values(aiProgress).length > 0;

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
        {' '}
        {/* Added relative positioning */}
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
          className={`btn btn-secondary ${selectedMenu === 'Full Sessions' ? 'bg-base-300 text-primary' : ''} w-full justify-start border-base-400 hover:border-base-400 hover:text-primary hover:border-opacity-75 py-3 text-gray-300`}
          onMouseDown={() => onSelectMenu('Full Sessions')}
        >
          <MdOutlinePlayCircleOutline className="w-6 h-6" />
          Full Sessions
        </button>
        <button
          ref={replayRef}
          className={`btn btn-secondary ${selectedMenu === 'Replay Buffer' ? 'bg-base-300 text-primary' : ''} w-full justify-start border-base-400 hover:border-base-400 hover:text-primary hover:border-opacity-75 py-3 text-gray-300`}
          onMouseDown={() => onSelectMenu('Replay Buffer')}
        >
          <MdReplay30 className="w-6 h-6" />
          Replay Buffer
        </button>
        <button
          ref={clipsRef}
          className={`btn btn-secondary ${selectedMenu === 'Clips' ? 'bg-base-300 text-primary' : ''} w-full justify-start border-base-400 hover:border-base-400 hover:text-primary hover:border-opacity-75 py-3 text-gray-300`}
          onMouseDown={() => onSelectMenu('Clips')}
        >
          <MdOutlineContentCut className="w-6 h-6" />
          Clips
        </button>
        <button
          ref={highlightsRef}
          className={`btn btn-secondary ${selectedMenu === 'Highlights' ? 'bg-base-300 text-primary' : ''} w-full justify-start border-base-400 hover:border-base-400 hover:text-primary hover:border-opacity-75 py-3 text-gray-300`}
          onMouseDown={() => onSelectMenu('Highlights')}
        >
          <div className="relative w-6 h-6 flex items-center justify-center">
            <HiOutlineSparkles
              className={`w-6 h-6 ${hasActiveAiHighlights ? 'text-purple-400 animate-pulse' : ''}`}
            />
          </div>
          <span className={hasActiveAiHighlights ? 'text-purple-400 animate-pulse' : ''}>
            Highlights
          </span>
        </button>
        <button
          ref={settingsRef}
          className={`btn btn-secondary ${selectedMenu === 'Settings' ? 'bg-base-300 text-primary' : ''} w-full justify-start border-base-400 hover:border-base-400 hover:text-primary hover:border-opacity-75 py-3 text-gray-300`}
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
        <div className="mb-4 flex flex-col items-center">
          <div
            style={{
              width: '3.5rem',
              height: '2rem',
            }}
            className="loading loading-infinity"
          ></div>
          <p className="text-center mt-2 disabled">Starting OBS</p>
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
