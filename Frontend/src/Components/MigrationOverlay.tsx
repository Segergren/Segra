import { useEffect, useState } from 'react';
import { MigrationStatus } from '../Models/types';

const MigrationOverlay: React.FC = () => {
  const [migrationStatus, setMigrationStatus] = useState<MigrationStatus | null>(null);

  // Debug: uncomment to show the migration UI
  // useEffect(() => setMigrationStatus({ isRunning: true, currentMigration: 'debug', step: 1, stepCount: 2, progress: 45, progressTotal: 200, progressUnit: 'videos' }), []);

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

  const { step, stepCount, progress, progressTotal, progressUnit } = migrationStatus;

  return (
    <div className="fixed inset-0 bg-base-300/95 z-[9999] flex items-center justify-center">
      <div className="text-center max-w-md">
        <div className="mb-6">
          <span className="loading loading-spinner loading-lg text-primary"></span>
        </div>
        <h2 className="text-2xl font-bold mb-4">Updating Segra</h2>
        <p className="text-base-content/70 mb-6">Preparing your content for the new version.</p>
        {stepCount > 1 && (
          <p className="text-sm text-base-content/70 mb-3">
            Step {step} of {stepCount}
          </p>
        )}
        {progressTotal != null && progressTotal > 0 && (
          <div className="flex flex-col items-center gap-2">
            <progress
              className="progress progress-primary w-64"
              value={progress ?? 0}
              max={progressTotal}
            />
            <p className="text-sm text-base-content/60">
              {progress ?? 0} of {progressTotal} {progressUnit}
            </p>
          </div>
        )}
      </div>
    </div>
  );
};

export default MigrationOverlay;
