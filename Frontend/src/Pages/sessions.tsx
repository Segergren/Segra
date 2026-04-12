import { Play } from 'lucide-react';
import ContentPage from '../Components/ContentPage';
import { useSettings } from '../Context/SettingsContext';
import ContentCard from '../Components/ContentCard';

export default function Sessions() {
  const { state } = useSettings();
  const { recording } = state;

  // Pre-render the progress card element
  const isRecordingFinishing = recording && recording.endTime !== null;
  const progressCardElement = isRecordingFinishing ? (
    <ContentCard key="recording-progress" type="Session" isLoading />
  ) : null;

  return (
    <ContentPage
      contentType="Session"
      sectionId="sessions"
      title="Sessions"
      Icon={Play}
      progressItems={isRecordingFinishing ? { recording: true } : {}}
      isProgressVisible={isRecordingFinishing}
      progressCardElement={progressCardElement}
    />
  );
}
