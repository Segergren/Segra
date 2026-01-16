import { useEffect, useState } from 'react';
import { MigrationStatus } from '../Models/types';

const MigrationOverlay: React.FC = () => {
  const [migrationStatus, setMigrationStatus] = useState<MigrationStatus | null>(null);

  useEffect(() => {
    const handleWebSocketMessage = (event: CustomEvent<any>) => {
      const data = event.detail;

      if (data.method === 'MigrationStatus') {
        const status = data.content as MigrationStatus;
        setMigrationStatus(status);
      }
    };

    window.addEventListener('websocket-message', handleWebSocketMessage as EventListener);

    return () => {
      window.removeEventListener('websocket-message', handleWebSocketMessage as EventListener);
    };
  }, []);

  if (!migrationStatus?.isRunning) {
    return null;
  }

  return (
    <div className="fixed inset-0 bg-base-300/95 z-[9999] flex items-center justify-center">
      <div className="text-center max-w-md">
        <div className="mb-6">
          <span className="loading loading-spinner loading-lg text-primary"></span>
        </div>
        <h2 className="text-2xl font-bold mb-4">Updating Segra</h2>
        <p className="text-base-content/70 mb-6">
          Preparing your content for the new version. Almost done!
        </p>
      </div>
    </div>
  );
};

export default MigrationOverlay;
