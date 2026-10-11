import { useEffect, useState } from 'react';
import { Check, Info, VideoOff } from 'lucide';
import { MorphIcon } from 'morphicons/react';
import Button from './Button';
import { useAppState } from '../Context/AppStateContext';
import { sendMessageToBackend } from '../Utils/MessageUtils';

const HEVC_MIME_TYPE = 'video/mp4; codecs="hvc1.1.6.L120.90"';

export const isHevcPlaybackSupported = () =>
  typeof MediaSource !== 'undefined' && MediaSource.isTypeSupported(HEVC_MIME_TYPE);

export const isHevcEncoder = (encoderId?: string) =>
  !!encoderId && (encoderId.includes('hevc') || encoderId.includes('h265'));

// Offers to install Microsoft's HEVC Video Extension when WebView2 can't play H.265.
export default function HevcExtensionWarning({
  variant = 'banner',
  className = '',
}: {
  variant?: 'banner' | 'player';
  className?: string;
}) {
  const { platform } = useAppState();
  const [status, setStatus] = useState<'idle' | 'installing' | 'installed'>('idle');

  useEffect(() => {
    const handleMessage = (event: CustomEvent) => {
      if (event.detail?.method !== 'HevcExtensionInstallFinished') return;
      setStatus(event.detail?.content?.success ? 'installed' : 'idle');
    };

    window.addEventListener('websocket-message', handleMessage as EventListener);
    return () => {
      window.removeEventListener('websocket-message', handleMessage as EventListener);
    };
  }, []);

  if (platform === 'linux' || isHevcPlaybackSupported()) return null;

  const actionButton = (
    <Button
      variant="primary"
      size="sm"
      className="shrink-0"
      loading={status === 'installing'}
      onClick={() => {
        if (status === 'installed') {
          sendMessageToBackend('RestartApp');
          return;
        }
        setStatus('installing');
        sendMessageToBackend('InstallHevcExtension');
      }}
    >
      {status === 'installed' ? 'Restart' : status === 'installing' ? 'Installing' : 'Install'}
    </Button>
  );

  if (variant === 'player') {
    return (
      <div
        className={`flex flex-col items-center justify-center gap-3 text-center ${className}`}
        role="status"
      >
        <MorphIcon
          icon={status === 'installed' ? Check : VideoOff}
          size={40}
          spring="snappy"
          className={status === 'installed' ? 'text-success' : 'text-base-content/40'}
        />
        <div>
          <p className="font-semibold text-base-content">
            {status === 'installed' ? 'Restart Segra to play this video' : "This video can't play"}
          </p>
          <p className="text-sm text-base-content/60 mt-1">
            {status === 'installed'
              ? 'The HEVC Video Extension is installed.'
              : "It's H.265, which needs Microsoft's HEVC Video Extension."}
          </p>
        </div>
        {actionButton}
      </div>
    );
  }

  return (
    <div
      className={`bg-base-300 border border-warning/60 rounded-lg px-4 py-3 text-sm flex items-center gap-3 ${className}`}
      role="status"
    >
      <MorphIcon
        icon={status === 'installed' ? Check : Info}
        size={20}
        spring="snappy"
        className={`shrink-0 ${status === 'installed' ? 'text-success' : 'text-warning'}`}
      />
      <span className="min-w-0 flex-1">
        {status === 'installed'
          ? 'The HEVC Video Extension is installed. Restart Segra to play H.265 videos.'
          : "H.265 videos need Microsoft's HEVC Video Extension to play in Segra."}
      </span>
      {actionButton}
    </div>
  );
}
