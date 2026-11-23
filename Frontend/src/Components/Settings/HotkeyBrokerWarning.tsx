import { TriangleAlert } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import Button from '../Button';
import { useAppState } from '../../Context/AppStateContext';
import { sendMessageToBackend } from '../../Utils/MessageUtils';

// Shown only when the automatic install of the elevated-hotkey helper was declined or failed.
export default function HotkeyBrokerWarning() {
  const { t } = useTranslation('audio');
  const { hotkeyBroker } = useAppState();
  if (!hotkeyBroker?.actionRequired) return null;

  return (
    <div
      className="bg-warning/10 border border-warning rounded-lg px-4 py-3 text-warning text-sm flex items-center gap-3"
      role="alert"
    >
      <TriangleAlert className="h-5 w-5 shrink-0" />
      <span className="min-w-0 flex-1">
        {hotkeyBroker.installed ? t('broker.outdated') : t('broker.missing')}
      </span>
      <Button
        variant="primary"
        size="sm"
        className="shrink-0"
        onClick={() => sendMessageToBackend('InstallHotkeyBroker')}
      >
        {hotkeyBroker.installed ? t('broker.update') : t('broker.install')}
      </Button>
    </div>
  );
}
