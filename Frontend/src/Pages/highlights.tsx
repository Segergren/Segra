import { Crown } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { useAiHighlights } from '../Context/AiHighlightsContext';
import ContentPage from '../Components/ContentPage';
import AiContentCard from '../Components/AiContentCard';

export default function Highlights() {
  const { t } = useTranslation('app');
  const { aiProgress } = useAiHighlights();

  // Pre-render the progress card element
  const progressCardElement =
    Object.keys(aiProgress).length > 0 ? (
      <AiContentCard key="ai-highlight-progress" progress={Object.values(aiProgress)[0]} />
    ) : null;

  return (
    <ContentPage
      contentType="Highlight"
      sectionId="highlights"
      title={t('page.highlights.title')}
      Icon={Crown}
      progressItems={aiProgress}
      isProgressVisible={Object.keys(aiProgress).length > 0}
      progressCardElement={progressCardElement}
    />
  );
}
