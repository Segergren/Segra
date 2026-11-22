import { useState, useEffect, useLayoutEffect, useMemo, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { useSettings, useSettingsUpdater } from '../Context/SettingsContext';
import { useScroll } from '../Context/ScrollContext';
import { useUpdate } from '../Context/UpdateContext';
import AccountSection from '../Components/Settings/AccountSection';
import CaptureModeSection from '../Components/Settings/CaptureModeSection';
import VideoSettingsSection from '../Components/Settings/VideoSettingsSection';
import StorageSettingsSection from '../Components/Settings/StorageSettingsSection';
import ClipSettingsSection from '../Components/Settings/ClipSettingsSection';
import AudioDevicesSection from '../Components/Settings/AudioDevicesSection';
import WebcamSection from '../Components/Settings/WebcamSection';
import HotkeysSection from '../Components/Settings/HotkeysSection';
import PendingRecordingSettingsBanner from '../Components/Settings/PendingRecordingSettingsBanner';
import HotkeyBrokerWarning from '../Components/Settings/HotkeyBrokerWarning';
import InputGroupWarning from '../Components/Settings/InputGroupWarning';
import GameDetectionSection from '../Components/Settings/GameDetectionSection';
import GameIntegrationsSection from '../Components/Settings/GameIntegrationsSection';
import HighlightsSection from '../Components/Settings/HighlightsSection';
import PreferencesSection from '../Components/Settings/PreferencesSection';
import MenuCustomizationSection from '../Components/Settings/MenuCustomizationSection';
import AdvancedSection from '../Components/Settings/AdvancedSection';
import LanguageSection from '../Components/Settings/LanguageSection';
import { getRequestedSettingsSection } from '../Utils/SettingsNavigation';

type SectionId =
  'account' | 'recording' | 'clips' | 'games' | 'storage' | 'preferences' | 'advanced';

// Section ids double as scroll anchors and localisation keys; only the rendered label is
// translated.
const ALL_NAV_ITEMS: { id: SectionId; labelKey: string }[] = [
  { id: 'account', labelKey: 'account' },
  { id: 'recording', labelKey: 'recording' },
  { id: 'clips', labelKey: 'clips' },
  { id: 'storage', labelKey: 'storage' },
  { id: 'games', labelKey: 'games' },
  { id: 'preferences', labelKey: 'preferences' },
  { id: 'advanced', labelKey: 'advanced' },
];

function SectionHeader({ id, children }: { id: string; children: React.ReactNode }) {
  return (
    <div id={id} className="scroll-mt-5 mb-0">
      <h2 className="text-sm font-semibold text-primary uppercase tracking-wider mb-2 mt-8 first:mt-0">
        {children}
      </h2>
    </div>
  );
}

export default function Settings() {
  const { t } = useTranslation('settings');
  const { openReleaseNotesModal, checkForUpdates, canSelfUpdate } = useUpdate();
  const settings = useSettings();
  const updateSettings = useSettingsUpdater();
  // Airplane mode removes the Account section entirely (no login/cloud UI).
  const navItems = useMemo(
    () =>
      settings.airplaneMode ? ALL_NAV_ITEMS.filter((item) => item.id !== 'account') : ALL_NAV_ITEMS,
    [settings.airplaneMode],
  );
  const [activeSection, setActiveSection] = useState<SectionId>(navItems[0].id);
  const { scrollPositions, setScrollPosition } = useScroll();
  const containerRef = useRef<HTMLDivElement>(null);
  const scrollTopRef = useRef(scrollPositions.settings);
  const [isScrolled, setIsScrolled] = useState(scrollPositions.settings > 0);

  useLayoutEffect(() => {
    const requested = getRequestedSettingsSection();
    const target = requested ? document.getElementById(requested) : null;
    if (target) target.scrollIntoView({ block: 'start' });
    else if (containerRef.current) containerRef.current.scrollTop = scrollTopRef.current;
    return () => setScrollPosition('settings', scrollTopRef.current);
  }, [setScrollPosition]);

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
    <div className="flex h-full flex-col bg-base-200">
      {/* Jump Nav, outside the scroller so its bottom line spans the scrollbar too */}
      <div
        className={`shrink-0 px-5 pt-5 pb-3 border-b transition-colors duration-300 ${isScrolled ? 'border-base-400/50' : 'border-transparent'}`}
      >
        <div className="flex items-center gap-6">
          <h1 className="text-[1.75rem] font-bold">{t('title')}</h1>
          <nav className="flex gap-1">
            {navItems.map((item) => (
              <button
                key={item.id}
                onClick={() => scrollToSection(item.id)}
                className={`px-3 py-1.5 text-base rounded transition-colors cursor-pointer ${
                  activeSection === item.id
                    ? 'text-primary'
                    : 'text-base-content/70 hover:text-primary'
                }`}
              >
                {t('nav.' + item.labelKey)}
              </button>
            ))}
          </nav>
        </div>
      </div>

      {/* Content */}
      <div
        ref={containerRef}
        onScroll={(e) => {
          scrollTopRef.current = e.currentTarget.scrollTop;
          setIsScrolled(e.currentTarget.scrollTop > 0);
        }}
        className="settings-content min-h-0 flex-1 overflow-y-scroll p-5 space-y-6"
      >
        <PendingRecordingSettingsBanner />

        {/* ACCOUNT */}
        {!settings.airplaneMode && (
          <>
            <SectionHeader id="account">{t('nav.account')}</SectionHeader>
            <AccountSection />
          </>
        )}

        {/* RECORDING */}
        <SectionHeader id="recording">{t('nav.recording')}</SectionHeader>
        <CaptureModeSection settings={settings} updateSettings={updateSettings} />
        <HotkeysSection settings={settings} updateSettings={updateSettings} />
        <HotkeyBrokerWarning />
        <InputGroupWarning />
        <VideoSettingsSection settings={settings} updateSettings={updateSettings} />
        <AudioDevicesSection settings={settings} updateSettings={updateSettings} />
        <WebcamSection settings={settings} updateSettings={updateSettings} />

        {/* CLIPS */}
        <SectionHeader id="clips">{t('nav.clips')}</SectionHeader>
        <ClipSettingsSection settings={settings} updateSettings={updateSettings} />
        <HighlightsSection settings={settings} updateSettings={updateSettings} />

        {/* STORAGE */}
        <SectionHeader id="storage">{t('nav.storage')}</SectionHeader>
        <StorageSettingsSection settings={settings} updateSettings={updateSettings} />

        {/* GAMES */}
        <SectionHeader id="games">{t('nav.games')}</SectionHeader>
        <GameDetectionSection />
        <GameIntegrationsSection />

        {/* PREFERENCES */}
        <SectionHeader id="preferences">{t('nav.preferences')}</SectionHeader>
        <PreferencesSection settings={settings} updateSettings={updateSettings} />
        <LanguageSection />
        <MenuCustomizationSection settings={settings} updateSettings={updateSettings} />

        {/* ADVANCED */}
        <SectionHeader id="advanced">{t('nav.advanced')}</SectionHeader>
        <AdvancedSection
          settings={settings}
          updateSettings={updateSettings}
          openReleaseNotesModal={openReleaseNotesModal}
          checkForUpdates={checkForUpdates}
          canSelfUpdate={canSelfUpdate}
        />
      </div>
    </div>
  );
}
