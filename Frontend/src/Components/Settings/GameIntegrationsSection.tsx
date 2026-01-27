import { useSettings } from '../../Context/SettingsContext';
import { sendMessageToBackend } from '../../Utils/MessageUtils';
import { GameIntegrations } from '../../Models/types';

// Game integration configuration - easy to extend with new games
interface GameIntegration {
  id: string;
  name: string;
  settingsKey: keyof GameIntegrations;
  bookmarks: string[];
  backgroundImage: string;
  isBeta?: boolean;
  warningText?: string;
}

const GAME_INTEGRATIONS: GameIntegration[] = [
  {
    id: 'cs2',
    name: 'Counter-Strike 2',
    settingsKey: 'counterStrike2',
    bookmarks: ['Kills', 'Deaths'],
    backgroundImage: 'https://segra.tv/api/games/cover/coaczd',
  },
  {
    id: 'lol',
    name: 'League of Legends',
    settingsKey: 'leagueOfLegends',
    bookmarks: ['Kills', 'Assists', 'Deaths'],
    backgroundImage: 'https://segra.tv/api/games/cover/coaiyq',
  },
  {
    id: 'pubg',
    name: 'PUBG: Battlegrounds',
    settingsKey: 'pubg',
    bookmarks: ['Kills', 'Knocks', 'Deaths'],
    backgroundImage: 'https://segra.tv/api/games/cover/coaam4',
  },
  {
    id: 'rocket-league',
    name: 'Rocket League',
    settingsKey: 'rocketLeague',
    bookmarks: ['Goals', 'Assists'],
    backgroundImage: 'https://segra.tv/api/games/cover/coaiyq',
    isBeta: true,
  },
];

// Get badge color based on bookmark type
const getBookmarkBadgeClass = (bookmark: string): string => {
  switch (bookmark) {
    case 'Kills':
    case 'Knocks':
    case 'Assists':
    case 'Goals':
      return 'bg-success/15 text-success';
    case 'Deaths':
      return 'bg-error/15 text-error';
    default:
      return 'bg-base-300';
  }
};

interface GameIntegrationCardProps {
  integration: GameIntegration;
  enabled: boolean;
  showBackground: boolean;
  isRecording: boolean;
  onToggle: (enabled: boolean) => void;
}

function GameIntegrationCard({
  integration,
  enabled,
  showBackground,
  isRecording,
  onToggle,
}: GameIntegrationCardProps) {
  return (
    <div className="relative bg-base-200 p-4 rounded-lg border border-custom overflow-hidden">
      {/* Background image */}
      {showBackground && (
        <div
          className="absolute inset-0 bg-cover bg-center opacity-12 pointer-events-none blur-[4px]"
          style={{ backgroundImage: `url(${integration.backgroundImage})` }}
        />
      )}
      <div className="relative z-10 flex flex-col h-full">
        <div className="flex items-center gap-2 mb-2">
          <h3 className="text-lg font-semibold">{integration.name}</h3>
          {integration.isBeta && <span className="badge badge-primary badge-sm">Beta</span>}
        </div>
        <div className="flex flex-wrap gap-1 mb-4">
          {integration.bookmarks.map((bookmark) => (
            <span
              key={bookmark}
              className={`badge badge-sm border-0 ${getBookmarkBadgeClass(bookmark)}`}
            >
              {bookmark}
            </span>
          ))}
        </div>
        {integration.warningText && (
          <p className="text-xs text-warning mb-3">{integration.warningText}</p>
        )}
        <div className="mt-auto">
          <label className="flex items-center gap-3 cursor-pointer">
            <input
              type="checkbox"
              className="toggle toggle-primary"
              checked={enabled}
              disabled={isRecording}
              onChange={(e) => onToggle(e.target.checked)}
            />
            <span className="text-sm">Enable Integration</span>
          </label>
        </div>
      </div>
    </div>
  );
}

export default function GameIntegrationsSection() {
  const settings = useSettings();

  const handleToggle = (settingsKey: GameIntegration['settingsKey'], enabled: boolean) => {
    sendMessageToBackend('UpdateSettings', {
      ...settings,
      gameIntegrations: {
        ...settings.gameIntegrations,
        [settingsKey]: {
          ...settings.gameIntegrations[settingsKey],
          enabled,
        },
      },
    });
  };

  return (
    <div className="p-4 bg-base-300 rounded-lg shadow-md border border-custom">
      <h2 className="text-xl font-semibold mb-2">Game Integrations</h2>
      <p className="text-sm opacity-80 mb-4">
        Enable automatic event detection for supported games. When enabled, Segra will automatically
        bookmark kills, goals, and other events during gameplay.
      </p>

      <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
        {GAME_INTEGRATIONS.map((integration) => (
          <GameIntegrationCard
            key={integration.id}
            integration={integration}
            enabled={settings.gameIntegrations[integration.settingsKey].enabled}
            showBackground={settings.showGameBackground}
            isRecording={settings.state.recording != null}
            onToggle={(enabled) => handleToggle(integration.settingsKey, enabled)}
          />
        ))}
      </div>
    </div>
  );
}
