import { TriangleAlert } from 'lucide-react';
import Button from '../Button';
import { useAppState } from '../../Context/AppStateContext';
import { sendMessageToBackend } from '../../Utils/MessageUtils';

// Shown only when the automatic install of the elevated-hotkey helper was declined or failed.
export default function HotkeyBrokerWarning() {
  const { hotkeyBroker } = useAppState();
  if (!hotkeyBroker?.actionRequired) return null;

  return (
    <div
      className="bg-amber-900 bg-opacity-30 border border-amber-500 rounded-lg px-4 py-3 text-amber-400 text-sm flex items-center gap-3"
      role="alert"
    >
      <TriangleAlert className="h-5 w-5 shrink-0" />
      <span className="min-w-0 flex-1">
        {hotkeyBroker.installed
          ? 'The helper for hotkeys over games running as administrator is out of date. Updating it needs administrator permission.'
          : 'Hotkeys do not reach games that run as administrator. Installing the helper fixes this and needs administrator permission.'}
      </span>
      <Button
        variant="primary"
        size="sm"
        className="shrink-0"
        onClick={() => sendMessageToBackend('InstallHotkeyBroker')}
      >
        {hotkeyBroker.installed ? 'Update' : 'Install'}
      </Button>
    </div>
  );
}
