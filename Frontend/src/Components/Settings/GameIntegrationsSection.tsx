import { useState } from 'react';
import { useSettings, useSettingsUpdater } from '../../Context/SettingsContext';
import { GameIntegrations } from '../../Models/types';
import {
  usePendingRecordingSettings,
  RECORDING_SETTING_GROUPS,
} from '../../Hooks/usePendingRecordingSettings';

// Logo files are named after the integration id
const LOGOS = import.meta.glob<string>('../../assets/game-logos/*.webp', {
  eager: true,
  import: 'default',
});

// Every logo gets the same area, so square ones aren't dwarfed by wide ones
const LOGO_AREA = 6400;
const LOGO_MAX_HEIGHT = 56;

const getLogoSize = (img: HTMLImageElement) => {
  const ratio = img.naturalWidth / img.naturalHeight;
  const height = Math.min(Math.sqrt(LOGO_AREA / ratio), LOGO_MAX_HEIGHT);
  return { width: height * ratio, height };
};

interface GameIntegration {
  id: string;
  name: string;
  settingsKey: keyof GameIntegrations;
  backgroundImage: string;
  coverOpacity?: number;
  // Shows a Beta label, with this as its tooltip
  betaNote?: string;
  warningText?: string;
}

const GAME_INTEGRATIONS: GameIntegration[] = [
  {
    id: 'lol',
    name: 'League of Legends',
    settingsKey: 'leagueOfLegends',
    backgroundImage: 'https://segra.tv/api/games/cover/ar57ot',
  },
  {
    id: 'cs2',
    name: 'Counter-Strike 2',
    settingsKey: 'counterStrike2',
    backgroundImage: 'https://segra.tv/api/games/cover/coaczd',
  },
  {
    id: 'fortnite',
    name: 'Fortnite',
    settingsKey: 'fortnite',
    backgroundImage: 'https://segra.tv/api/games/cover/cocxbi',
  },
  {
    id: 'valorant',
    name: 'Valorant',
    settingsKey: 'valorant',
    backgroundImage: 'https://segra.tv/api/games/cover/cocqbp',
  },
  {
    id: 'overwatch',
    name: 'Overwatch',
    settingsKey: 'overwatch',
    backgroundImage: 'https://segra.tv/api/games/cover/cocqgt',
    betaNote: 'Kills and assists only work with color\u00a0blind mode off.',
  },
  {
    id: 'gta',
    name: 'Grand Theft Auto',
    settingsKey: 'gta',
    backgroundImage: 'https://segra.tv/api/games/cover/ar4pi5',
  },
  {
    id: 'pubg',
    name: 'PUBG: Battlegrounds',
    settingsKey: 'pubg',
    backgroundImage: 'https://segra.tv/api/games/cover/sc87ll',
  },
  {
    id: 'rainbow-six-siege',
    name: 'Rainbow Six Siege',
    settingsKey: 'rainbowSixSiege',
    backgroundImage: 'https://segra.tv/api/games/cover/ar6elp',
  },
  {
    id: 'battlefield-6',
    name: 'Battlefield 6',
    settingsKey: 'battlefield6',
    backgroundImage: 'https://segra.tv/api/games/cover/coa5zt',
  },
  {
    id: 'rocket-league',
    name: 'Rocket League',
    settingsKey: 'rocketLeague',
    backgroundImage: 'https://segra.tv/api/games/cover/ar5u6d',
  },
  {
    id: 'rust',
    name: 'Rust',
    settingsKey: 'rust',
    backgroundImage: 'https://segra.tv/api/games/cover/coajjj',
    coverOpacity: 55,
  },
  {
    id: 'wardogs',
    name: 'WARDOGS',
    settingsKey: 'wardogs',
    backgroundImage: 'https://segra.tv/api/games/cover/cocs6d',
  },
  {
    id: 'minecraft',
    name: 'Minecraft',
    settingsKey: 'minecraft',
    backgroundImage: 'https://segra.tv/api/games/cover/co8fu7',
  },
  {
    id: 'deadlock',
    name: 'Deadlock',
    settingsKey: 'deadlock',
    backgroundImage: 'https://segra.tv/api/games/cover/cobc7s',
  },
  {
    id: 'dota2',
    name: 'Dota 2',
    settingsKey: 'dota2',
    backgroundImage: 'https://segra.tv/api/games/cover/q6dxlfgq7e01ktv2zejz',
  },
  {
    id: 'war-thunder',
    name: 'War Thunder',
    settingsKey: 'warThunder',
    backgroundImage: 'https://segra.tv/api/games/cover/co1p78',
  },
  {
    id: 'runescape-dragonwilds',
    name: 'RuneScape: Dragonwilds',
    settingsKey: 'runescapeDragonwilds',
    backgroundImage: 'https://segra.tv/api/games/cover/ar3en0',
  },
];

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
  const logo = LOGOS[`../../assets/game-logos/${integration.id}.webp`];
  const [logoSize, setLogoSize] = useState<{ width: number; height: number }>();

  return (
    <label
      className={`relative block bg-base-200 px-4 py-3 rounded-lg border cursor-pointer transition-colors ${enabled ? 'border-primary' : 'border-base-400'}`}
    >
      {/* Background image */}
      {showBackground && (
        <div
          className="absolute inset-0 rounded-[inherit] bg-cover bg-center pointer-events-none"
          style={{
            backgroundImage: `url(${integration.backgroundImage})`,
            opacity: (integration.coverOpacity ?? 35) / 100,
          }}
        />
      )}
      {integration.betaNote && (
        <span
          className="tooltip tooltip-left tooltip-primary absolute top-1.5 right-2.5 z-20 text-[10px] font-semibold text-primary drop-shadow-md [&::before]:delay-200 [&::after]:delay-200 [&::before]:text-left [&::before]:leading-snug [&::before]:max-w-64 [&::before]:px-3 [&::before]:py-2"
          data-tip={integration.betaNote}
        >
          Beta
        </span>
      )}
      <div className="relative z-10">
        <div className="flex items-center justify-center gap-2 h-14">
          {logo ? (
            <img
              src={logo}
              alt={integration.name}
              onLoad={(e) => setLogoSize(getLogoSize(e.currentTarget))}
              style={logoSize}
              className="h-10 min-w-0 max-w-full object-contain drop-shadow-md"
            />
          ) : (
            <h3 className="text-base font-semibold truncate">{integration.name}</h3>
          )}
          <input
            type="checkbox"
            className="sr-only"
            aria-label={integration.name}
            checked={enabled}
            onChange={(e) => onToggle(e.target.checked)}
          />
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
