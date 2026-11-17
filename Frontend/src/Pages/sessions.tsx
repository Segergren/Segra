import { useState } from 'react';
import { Play } from 'lucide-react';
import ContentPage from '../Components/ContentPage';
import { useAppState } from '../Context/AppStateContext';
import { useClipping } from '../Context/ClippingContext';
import { useSettings } from '../Context/SettingsContext';
import ContentCard from '../Components/ContentCard';
import LiveSessionCard from '../Components/LiveSessionCard';

export default function Sessions() {
  const { isFinishingRecording, recording } = useAppState();
  const { showLiveSessionCard } = useSettings();
  const { clippingProgress } = useClipping();
  const merges = Object.values(clippingProgress).filter(
    (p) => p.outputType === 'Session' && p.progress >= 0,
  );
  const isMergingSessions = merges.length > 0;

  // Keep the last recording so its card can stay in place while the file is finalized
  const [lastRecording, setLastRecording] = useState(recording);
  if (recording && recording !== lastRecording) setLastRecording(recording);

  const isFinishing = !recording && isFinishingRecording;
  const liveRecording = recording ?? (isFinishing ? lastRecording : null);
  const showLiveCard = showLiveSessionCard && !!liveRecording?.filePath;

  const showProgress = showLiveCard || isFinishingRecording || isMergingSessions;

  const progressCardElement = (
    <>
      {showLiveCard && (
        <LiveSessionCard
          key={`live-${liveRecording!.filePath}`}
          recording={liveRecording!}
          isFinishing={isFinishing}
        />
      )}
      {((isFinishingRecording && !showLiveCard) || isMergingSessions) && (
        <ContentCard
          key="recording-progress"
          type="Session"
          isLoading
          loadingLabel={isMergingSessions ? 'Merging Sessions' : 'Saving Recording'}
          loadingProgress={
            isMergingSessions
              ? merges.reduce((sum, p) => sum + p.progress, 0) / merges.length
              : undefined
          }
        />
      )}
    </>
  );

  return (
    <ContentPage
      contentType="Session"
      sectionId="sessions"
      title="Full Sessions"
      Icon={Play}
      progressItems={showProgress ? { recording: true } : {}}
      isProgressVisible={showProgress}
      progressCardElement={progressCardElement}
    />
  );
}
