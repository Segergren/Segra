import { useState, useEffect, useMemo } from 'react';
import { useSettings, useSettingsUpdater } from '../Context/SettingsContext';
import { useUpdate } from '../Context/UpdateContext';
import AccountSection from '../Components/Settings/AccountSection';
import CaptureModeSection from '../Components/Settings/CaptureModeSection';
import VideoSettingsSection from '../Components/Settings/VideoSettingsSection';
import StorageSettingsSection from '../Components/Settings/StorageSettingsSection';
import ClipSettingsSection from '../Components/Settings/ClipSettingsSection';
import AudioDevicesSection from '../Components/Settings/AudioDevicesSection';
import KeybindingsSection from '../Components/Settings/KeybindingsSection';
import GameDetectionSection from '../Components/Settings/GameDetectionSection';
import GameIntegrationsSection from '../Components/Settings/GameIntegrationsSection';
import HighlightsSection from '../Components/Settings/HighlightsSection';
import LowlightsSection from '../Components/Settings/LowlightsSection';
import PreferencesSection from '../Components/Settings/PreferencesSection';
import MenuCustomizationSection from '../Components/Settings/MenuCustomizationSection';
import AdvancedSection from '../Components/Settings/AdvancedSection';

type SectionId =
  | 'account'
  | 'recording'
  | 'clips'
  | 'games'
  | 'storage'
  | 'preferences'
  | 'advanced';

const ALL_NAV_ITEMS: { id: SectionId; label: string }[] = [
  { id: 'account', label: 'Account' },
  { id: 'recording', label: 'Recording' },
  { id: 'clips', label: 'Clips' },
  { id: 'storage', label: 'Storage' },
  { id: 'games', label: 'Games' },
  { id: 'preferences', label: 'Preferences' },
  { id: 'advanced', label: 'Advanced' },
];

function SectionHeader({ id, children }: { id: string; children: React.ReactNode }) {
  return (
    <div id={id} className="scroll-mt-16 mb-0">
      <h2 className="text-sm font-semibold text-primary uppercase tracking-wider mb-2 mt-8 first:mt-0">
        {children}
      </h2>
    </div>
  );
}

export default function Settings() {
  const { openReleaseNotesModal, checkForUpdates } = useUpdate();
  const settings = useSettings();
  const updateSettings = useSettingsUpdater();
  // Airplane mode removes the Account section entirely (no login/cloud UI).
  const navItems = useMemo(
    () =>
      settings.airplaneMode ? ALL_NAV_ITEMS.filter((item) => item.id !== 'account') : ALL_NAV_ITEMS,
    [settings.airplaneMode],
  );
  const [activeSection, setActiveSection] = useState<SectionId>(navItems[0].id);

  const scrollToSection = (id: SectionId) => {
    document.getElementById(id)?.scrollIntoView({ behavior: 'smooth' });
  };

  // Scroll spy to track which section is currently visible
  useEffect(() => {
    // The settings page scrolls inside an ancestor container; find it so we can detect the very top.
    let scroller: HTMLElement | null = null;
    const getScroller = (): HTMLElement | null => {
      if (scroller?.isConnected) return scroller;
      let node = document.getElementById(navItems[0].id)?.parentElement ?? null;
      while (node) {
        const overflowY = getComputedStyle(node).overflowY;
        if (
          (overflowY === 'auto' || overflowY === 'scroll') &&
          node.scrollHeight > node.clientHeight
        ) {
          scroller = node;
          return node;
        }
        node = node.parentElement;
      }
      return null;
    };

    const handleScroll = () => {
      // At the very top, always select the first section (Account); the upper-third check below
      // otherwise picks whichever later heading already sits above the line.
      if ((getScroller()?.scrollTop ?? 0) <= 0) {
        setActiveSection(navItems[0].id);
        return;
      }

      const viewportCenter = window.innerHeight / 2.3; // Check upper-third of viewport

      // Find the last section whose top has passed the check point
      for (let i = navItems.length - 1; i >= 0; i--) {
        const element = document.getElementById(navItems[i].id);
        if (element) {
          const rect = element.getBoundingClientRect();
          if (rect.top <= viewportCenter) {
            setActiveSection(navItems[i].id);
            return;
          }
        }
      }

      // Default to first section if none found
      setActiveSection(navItems[0].id);
    };

    window.addEventListener('scroll', handleScroll, true);
    handleScroll(); // Initial check

    return () => window.removeEventListener('scroll', handleScroll, true);
  }, [navItems]);

  return (
    <div className="min-h-full bg-base-200 dark:bg-base-300">
      {/* Sticky Jump Nav */}
      <div className="sticky top-0 z-50 bg-base-200 dark:bg-base-300 border-b border-base-400 px-5 py-3">
        <div className="flex items-center gap-6">
          <h1 className="text-2xl font-bold">Settings</h1>
          <nav className="flex gap-1">
            {navItems.map((item) => (
              <button
                key={item.id}
                onClick={() => scrollToSection(item.id)}
                className={`px-3 py-1.5 text-sm rounded transition-colors cursor-pointer ${
                  activeSection === item.id
                    ? 'text-primary bg-base-300'
                    : 'text-gray-400 hover:text-primary hover:bg-base-300'
                }`}
              >
                {item.label}
              </button>
            ))}
          </nav>
        </div>
      </div>

      {/* Content */}
      <div className="p-5 space-y-6">
        {/* ACCOUNT */}
        {!settings.airplaneMode && (
          <>
            <SectionHeader id="account">Account</SectionHeader>
            <AccountSection />
          </>
        )}

        {/* RECORDING */}
        <SectionHeader id="recording">Recording</SectionHeader>
        <CaptureModeSection settings={settings} updateSettings={updateSettings} />
        <VideoSettingsSection settings={settings} updateSettings={updateSettings} />
        <AudioDevicesSection settings={settings} updateSettings={updateSettings} />
        <KeybindingsSection settings={settings} updateSettings={updateSettings} />

        {/* CLIPS */}
        <SectionHeader id="clips">Clips</SectionHeader>
        <ClipSettingsSection settings={settings} updateSettings={updateSettings} />
        <HighlightsSection settings={settings} updateSettings={updateSettings} />
        <LowlightsSection settings={settings} updateSettings={updateSettings} />

        {/* STORAGE */}
        <SectionHeader id="storage">Storage</SectionHeader>
        <StorageSettingsSection settings={settings} updateSettings={updateSettings} />

        {/* GAMES */}
        <SectionHeader id="games">Games</SectionHeader>
        <GameDetectionSection />
        <GameIntegrationsSection />

        {/* PREFERENCES */}
        <SectionHeader id="preferences">Preferences</SectionHeader>
        <PreferencesSection settings={settings} updateSettings={updateSettings} />
        <MenuCustomizationSection settings={settings} updateSettings={updateSettings} />

        {/* ADVANCED */}
        <SectionHeader id="advanced">Advanced</SectionHeader>
        <AdvancedSection
          settings={settings}
          updateSettings={updateSettings}
          openReleaseNotesModal={openReleaseNotesModal}
          checkForUpdates={checkForUpdates}
        />
      </div>
    </div>
  );
}
