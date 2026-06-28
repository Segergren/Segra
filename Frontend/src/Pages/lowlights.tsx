import { Skull } from 'lucide-react';
import { useAiLowlights } from '../Context/AiLowlightsContext';
import ContentPage from '../Components/ContentPage';
import AiContentCard from '../Components/AiContentCard';

export default function Lowlights() {
  const { aiProgress } = useAiLowlights();

  // Pre-render the progress card element
  const progressCardElement =
    Object.keys(aiProgress).length > 0 ? (
      <AiContentCard key="ai-lowlight-progress" progress={Object.values(aiProgress)[0]} />
    ) : null;

  return (
    <ContentPage
      contentType="Lowlight"
      sectionId="lowlights"
      title="Lowlights"
      Icon={Skull}
      progressItems={aiProgress}
      isProgressVisible={Object.keys(aiProgress).length > 0}
      progressCardElement={progressCardElement}
    />
  );
}
