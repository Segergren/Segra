import { useSettings } from './Context/SettingsContext';
import { useAppState } from './Context/AppStateContext';
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
  Clapperboard,
  OctagonX,
  Settings,
  History,
  Crown,
  Monitor,
  Play,
  LucideIcon,
} from 'lucide-react';
import { AnimatePresence, motion } from 'framer-motion';
import { useRef, useEffect, useLayoutEffect, useState, useMemo } from 'react';
import Button from './Components/Button';
import { MenuItemId, DEFAULT_MENU_ITEMS, menuItemHasContent } from './Models/types';

interface MenuProps {
  selectedMenu: string;
  onSelectMenu: (menu: string) => void;
}

const MENU_ICONS: Record<MenuItemId, LucideIcon> = {
  'Full Sessions': Play,
  'Replay Buffer': History,
  Clips: Clapperboard,
  Highlights: Crown,
  Settings: Settings,
};

export default function Menu({ selectedMenu, onSelectMenu }: MenuProps) {
  const settings = useSettings();
  const appState = useAppState();
  const { hasLoadedObs, recording, preRecording } = appState;
  const { updateInfo } = useUpdate();
  const { aiProgress } = useAiHighlights();
  const { obsDownloadProgress } = useObsDownload();
  const [buttonCooldown, setButtonCooldown] = useState(false);

  const buttonRefs = useRef<Record<string, HTMLDivElement | null>>({});
  const [indicatorPosition, setIndicatorPosition] = useState({ top: 12 });
  const [indicatorAnimated, setIndicatorAnimated] = useState(false);

  const visibleMenuItems = useMemo(() => {
    const items =
      settings.menuItems && settings.menuItems.length > 0 ? settings.menuItems : DEFAULT_MENU_ITEMS;
    // Force-show items that contain content so the user always has a way to reach their files.
    return items.filter(
      (item) =>
        item.id === 'Settings' || item.visible || menuItemHasContent(item.id, appState.content),
    );
  }, [settings.menuItems, appState.content]);

  const computeIndicatorPosition = () => {
    if (!visibleMenuItems.some((item) => item.id === selectedMenu)) return;
    const rowEl = buttonRefs.current[selectedMenu];
    if (!rowEl) return;
    const buttonEl = rowEl.firstElementChild as HTMLElement | null;
    const buttonHeight = buttonEl?.offsetHeight || 48;
    const indicatorTop = rowEl.offsetTop + buttonHeight / 2 - 20;
    setIndicatorPosition({ top: indicatorTop });
  };

  useLayoutEffect(() => {
    // Skip while the active row is mid-exit (App.tsx will redirect selectedMenu to a
    // fallback on the next tick). Otherwise we'd read a stale layout for one frame.
    computeIndicatorPosition();
    // After the row enter/exit animation finishes (200ms), recompute. Showing a row
    // grows its height over the animation window so rows below settle into their final
    // offsetTop only at the end — this second pass corrects the indicator to match.
    const timeoutId = setTimeout(computeIndicatorPosition, 220);
    return () => clearTimeout(timeoutId);
  }, [selectedMenu, visibleMenuItems]);

  // Enable the slide transition only after the first paint, so the initial render
  // snaps the indicator to the correct row without animating from the default top.
  useEffect(() => {
    setIndicatorAnimated(true);
  }, []);

  const aiProgressValues = Object.values(aiProgress);
  const hasActiveAiHighlights = aiProgressValues.length > 0;
  const averageAiProgress = hasActiveAiHighlights
    ? Math.round(aiProgressValues.reduce((sum, p) => sum + p.progress, 0) / aiProgressValues.length)
    : 0;

  const hasUnavailableDevices = () => {
    const unavailableInput = settings.inputDevices.some(
      (deviceSetting: { id: string }) =>
        deviceSetting.id !== 'default' &&
        !appState.inputDevices.some((d) => d.id === deviceSetting.id),
    );
    const unavailableOutput = settings.outputDevices.some(
      (deviceSetting: { id: string }) =>
        deviceSetting.id !== 'default' &&
        !appState.outputDevices.some((d) => d.id === deviceSetting.id),
    );
    return unavailableInput || unavailableOutput;
  };

  return (
    <div className="bg-base-300 w-56 h-screen flex flex-col border-r border-base-400">
      {/* Menu Items */}
      <div className="flex flex-col px-4 text-left py-2 relative mt-2">
        {/* Selection indicator rectangle */}
        <div
          className={`absolute w-1.5 bg-primary rounded-r ${
            indicatorAnimated ? 'transition-all duration-200 ease-in-out' : ''
          }`}
          style={{
            left: 0,
            top: `${indicatorPosition.top}px`,
            height: '40px',
          }}
        />
        <AnimatePresence initial={false} mode="popLayout">
          {visibleMenuItems.map(({ id }) => {
            const Icon = MENU_ICONS[id];
            const isActive = selectedMenu === id;

            const buttonNode =
              id === 'Highlights' ? (
                <Button
                  variant="nav"
                  className={`justify-between ${isActive ? 'text-primary' : ''}`}
                  onMouseDown={() => onSelectMenu(id)}
                >
                  <span className="flex items-center gap-2">
                    <Icon className="w-5 h-5" />
                    {id}
                  </span>
                  <div className="ml-auto flex items-center">
                    <AnimatePresence>
                      {hasActiveAiHighlights && !isActive && (
                        <motion.div
                          className="flex items-center justify-center"
                          initial={{ opacity: 0 }}
                          animate={{ opacity: 1 }}
                          exit={{ opacity: 0 }}
                          transition={{ duration: 0.2 }}
                        >
                          <CircularProgress
                            progress={averageAiProgress}
                            size={24}
                            strokeWidth={2}
                          />
                        </motion.div>
                      )}
                    </AnimatePresence>
                  </div>
                </Button>
              ) : (
                <Button
                  variant="nav"
                  className={isActive ? 'text-primary' : ''}
                  onMouseDown={() => onSelectMenu(id)}
                >
                  <Icon className="w-5 h-5" />
                  {id}
                </Button>
              );

            return (
              <motion.div
                key={id}
                ref={(el) => {
                  buttonRefs.current[id] = el;
                }}
                layout
                initial={{ opacity: 0, height: 0 }}
                animate={{ opacity: 1, height: 'auto' }}
                exit={{ opacity: 0, height: 0 }}
                transition={{ duration: 0.2, ease: 'easeInOut' }}
                className="overflow-hidden pb-2 last:pb-0"
              >
                {buttonNode}
              </motion.div>
            );
          })}
        </AnimatePresence>
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
              !appState.hasLoadedObs ||
              (appState.recording && recording && recording.endTime !== null)
            }
            onClick={() => {
              setButtonCooldown(true);
              setTimeout(() => setButtonCooldown(false), 1000);
              sendMessageToBackend(
                appState.recording || appState.preRecording ? 'StopRecording' : 'StartRecording',
              );
            }}
          >
            {appState.recording || appState.preRecording ? (
              <>
                <OctagonX className="w-4 h-4" />
                Stop
              </>
            ) : (
              <>
                <Monitor className="w-4 h-4" />
                Display Capture
              </>
            )}
          </Button>
        </div>
      </div>
    </div>
  );
}
