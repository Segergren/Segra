import { useSettings, useSettingsUpdater } from '../../Context/SettingsContext';
import { GameIntegrations } from '../../Models/types';
import {
  usePendingRecordingSettings,
  RECORDING_SETTING_GROUPS,
} from '../../Hooks/usePendingRecordingSettings';

interface GameIntegration {
  id: string;
  name: string;
  settingsKey: keyof GameIntegrations;
  bookmarks: string[];
  backgroundImage: string;
  coverOpacity?: number;
  isBeta?: boolean;
  warningText?: string;
}

const GAME_INTEGRATIONS: GameIntegration[] = [
  {
    id: 'lol',
    name: 'League of Legends',
    settingsKey: 'leagueOfLegends',
    bookmarks: ['Kills', 'Assists', 'Deaths'],
    backgroundImage: 'https://segra.tv/api/games/cover/ar57ot',
  },
  {
    id: 'cs2',
    name: 'Counter-Strike 2',
    settingsKey: 'counterStrike2',
    bookmarks: ['Kills', 'Deaths'],
    backgroundImage: 'https://segra.tv/api/games/cover/coaczd',
  },
  {
    id: 'gta',
    name: 'Grand Theft Auto',
    settingsKey: 'gta',
    bookmarks: ['Deaths'],
    backgroundImage: 'https://segra.tv/api/games/cover/ar4pi5',
  },
  {
    id: 'pubg',
    name: 'PUBG: Battlegrounds',
    settingsKey: 'pubg',
    bookmarks: ['Kills', 'Knocks', 'Deaths'],
    backgroundImage: 'https://segra.tv/api/games/cover/sc87ll',
  },
  {
    id: 'rainbow-six-siege',
    name: 'Rainbow Six Siege',
    settingsKey: 'rainbowSixSiege',
    bookmarks: ['Kills', 'Deaths'],
    backgroundImage: 'https://segra.tv/api/games/cover/ar6elp',
  },
  {
    id: 'battlefield-6',
    name: 'Battlefield 6',
    settingsKey: 'battlefield6',
    bookmarks: ['Kills', 'Deaths'],
    backgroundImage: 'https://segra.tv/api/games/cover/coa5zt',
  },
  {
    id: 'rocket-league',
    name: 'Rocket League',
    settingsKey: 'rocketLeague',
    bookmarks: ['Goals', 'Assists'],
    backgroundImage: 'https://segra.tv/api/games/cover/ar5u6d',
  },
  {
    id: 'rust',
    name: 'Rust',
    settingsKey: 'rust',
    bookmarks: ['Deaths'],
    backgroundImage: 'https://segra.tv/api/games/cover/coajjj',
    coverOpacity: 45,
  },
  {
    id: 'wardogs',
    name: 'WARDOGS',
    settingsKey: 'wardogs',
    bookmarks: ['Kills', 'Deaths'],
    backgroundImage: 'https://segra.tv/api/games/cover/cocs6d',
  },
  {
    id: 'minecraft',
    name: 'Minecraft',
    settingsKey: 'minecraft',
    bookmarks: ['Deaths'],
    backgroundImage: 'https://segra.tv/api/games/cover/co8fu7',
  },
  {
    id: 'deadlock',
    name: 'Deadlock',
    settingsKey: 'deadlock',
    bookmarks: ['Kills', 'Assists', 'Deaths'],
    backgroundImage: 'https://segra.tv/api/games/cover/cobc7s',
  },
  {
    id: 'dota2',
    name: 'Dota 2',
    settingsKey: 'dota2',
    bookmarks: ['Kills', 'Assists', 'Deaths'],
    backgroundImage: 'https://segra.tv/api/games/cover/q6dxlfgq7e01ktv2zejz',
  },
  {
    id: 'war-thunder',
    name: 'War Thunder',
    settingsKey: 'warThunder',
    bookmarks: ['Kills', 'Deaths'],
    backgroundImage: 'https://segra.tv/api/games/cover/co1p78',
  },
  {
    id: 'runescape-dragonwilds',
    name: 'RuneScape: Dragonwilds',
    settingsKey: 'runescapeDragonwilds',
    bookmarks: ['Deaths'],
    backgroundImage: 'https://segra.tv/api/games/cover/ar3en0',
  },
];

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
  onToggle: (enabled: boolean) => void;
}

function GameIntegrationCard({
  integration,
  enabled,
  showBackground,
  onToggle,
}: GameIntegrationCardProps) {
  return (
    <label
      className={`relative block bg-base-200 px-4 py-4 rounded-lg border overflow-hidden cursor-pointer transition-colors ${enabled ? 'border-primary/80' : 'border-base-400'}`}
    >
      {/* Background image */}
      {showBackground && (
        <div
          className="absolute inset-0 bg-cover bg-center pointer-events-none"
          style={{
            backgroundImage: `url(${integration.backgroundImage})`,
            opacity: (integration.coverOpacity ?? 25) / 100,
          }}
        />
      )}
      <div className="relative z-10">
        <div className="flex items-center gap-2">
          <h3 className="text-base font-semibold truncate">{integration.name}</h3>
          {integration.isBeta && (
            <span className="badge badge-primary badge-sm drop-shadow-md">Beta</span>
          )}
          <input
            type="checkbox"
            className="sr-only"
            aria-label={integration.name}
            checked={enabled}
            onChange={(e) => onToggle(e.target.checked)}
          />
        </div>
        <div className="flex flex-wrap gap-1 mt-1.5">
          {integration.bookmarks.map((bookmark) => (
            <span
              key={bookmark}
              className={`badge badge-sm border-0 drop-shadow-md ${getBookmarkBadgeClass(bookmark)}`}
            >
              {bookmark}
            </span>
          ))}
        </div>
        {integration.warningText && (
          <p className="text-xs text-warning mt-1">{integration.warningText}</p>
        )}
      </div>
    </label>
  );
}

export default function GameIntegrationsSection() {
  const settings = useSettings();
  const updateSettings = useSettingsUpdater();
  const hasPendingChanges = usePendingRecordingSettings(RECORDING_SETTING_GROUPS.gameIntegrations);

  const handleToggle = (settingsKey: GameIntegration['settingsKey'], enabled: boolean) => {
    updateSettings({
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
    <div className="p-4 bg-base-300 rounded-lg shadow-md border border-base-400">
      <div className="flex items-center gap-2 mb-2">
        <h2 className="text-xl font-semibold">Game Integrations</h2>
        {hasPendingChanges && (
          <span className="text-xs text-warning">(applies to next recording)</span>
        )}
      </div>
      <p className="text-sm opacity-70 mb-4">
        Enable automatic event detection for supported games. When enabled, Segra will automatically
        bookmark kills, goals, and other events during gameplay.
      </p>

      {/* Up to 4 per row, based on the section's own width rather than the window */}
      <div className="grid grid-cols-[repeat(auto-fill,minmax(max(13rem,calc((100%_-_2.25rem)/4)),1fr))] gap-3">
        {GAME_INTEGRATIONS.map((integration) => (
          <GameIntegrationCard
            key={integration.id}
            integration={integration}
            enabled={settings.gameIntegrations[integration.settingsKey].enabled}
            showBackground={settings.showGameBackground}
            onToggle={(enabled) => handleToggle(integration.settingsKey, enabled)}
          />
        ))}
      </div>
    </div>
  );
}
