import { Play } from 'lucide-react';
import ContentPage from '../Components/ContentPage';
import { useAppState } from '../Context/AppStateContext';
import ContentCard from '../Components/ContentCard';

export default function Sessions() {
  const { isFinishingRecording } = useAppState();

  // Pre-render the progress card element
  const progressCardElement = isFinishingRecording ? (
    <ContentCard key="recording-progress" type="Session" isLoading />
  ) : null;

  return (
    <ContentPage
      contentType="Session"
      sectionId="sessions"
      title="Sessions"
      Icon={Play}
      progressItems={isFinishingRecording ? { recording: true } : {}}
      isProgressVisible={isFinishingRecording}
      progressCardElement={progressCardElement}
    />
  );
}
