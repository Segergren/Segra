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
  const isMergingSessions = Object.values(clippingProgress).some(
    (p) => p.outputType === 'Session' && p.progress >= 0,
  );

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
        <ContentCard key="recording-progress" type="Session" isLoading />
      )}
    </>
  );

  return (
    <ContentPage
      contentType="Session"
      sectionId="sessions"
      title="Sessions"
      Icon={Play}
      progressItems={showProgress ? { recording: true } : {}}
      isProgressVisible={showProgress}
      progressCardElement={progressCardElement}
    />
  );
}
