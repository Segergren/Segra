import { Play } from 'lucide-react';
import ContentPage from '../Components/ContentPage';
import { useAppState } from '../Context/AppStateContext';
import { useClipping } from '../Context/ClippingContext';
import ContentCard from '../Components/ContentCard';

export default function Sessions() {
  const { isFinishingRecording } = useAppState();
  const { clippingProgress } = useClipping();
  const isMergingSessions = Object.values(clippingProgress).some(
    (p) => p.outputType === 'Session' && p.progress >= 0,
  );
  const showProgress = isFinishingRecording || isMergingSessions;

  // Pre-render the progress card element
  const progressCardElement = showProgress ? (
    <ContentCard key="recording-progress" type="Session" isLoading />
  ) : null;

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
