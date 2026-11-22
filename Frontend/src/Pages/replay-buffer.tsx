import { History } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import ContentPage from '../Components/ContentPage';

export default function ReplayBuffer() {
  const { t } = useTranslation('app');

  return (
    <ContentPage
      contentType="Buffer"
      sectionId="replayBuffer"
      title={t('page.replayBuffer.title')}
      Icon={History}
    />
  );
}
