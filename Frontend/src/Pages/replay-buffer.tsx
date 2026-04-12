import { History } from 'lucide-react';
import ContentPage from '../Components/ContentPage';

export default function ReplayBuffer() {
  return (
    <ContentPage
      contentType="Buffer"
      sectionId="replayBuffer"
      title="Replay Buffer"
      Icon={History}
    />
  );
}
