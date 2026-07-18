import { useEffect, useState, createContext } from 'react';
import Settings from './Pages/settings';
import Menu from './menu';
import Sessions from './Pages/sessions';
import Clips from './Pages/clips';
import ReplayBuffer from './Pages/replay-buffer';
import Highlights from './Pages/highlights';
import { SettingsProvider } from './Context/SettingsContext';
import { AppStateProvider } from './Context/AppStateContext';
import Video from './Pages/video';
import { useSelectedVideo } from './Context/SelectedVideoContext';
import { useSelectedMenu } from './Context/SelectedMenuContext';
import { themeChange } from 'theme-change';
import { useSettings } from './Context/SettingsContext';
import { useAppState } from './Context/AppStateContext';
import { DEFAULT_MENU_ITEMS, MenuItemId, menuItemHasContent } from './Models/types';
import { HTML5Backend } from 'react-dnd-html5-backend';
import { DndProvider } from 'react-dnd';
import { SegmentsProvider } from './Context/SegmentsContext';
import { UploadProvider } from './Context/UploadContext';
import { ImportProvider } from './Context/ImportContext';
import { ContentMigrationProvider } from './Context/ContentMigrationContext';
import { WebSocketProvider } from './Context/WebSocketContext';
import { ClippingProvider } from './Context/ClippingContext';
import { AiHighlightsProvider } from './Context/AiHighlightsContext';
import { CompressionProvider } from './Context/CompressionContext';
import { UpdateProvider } from './Context/UpdateContext';
import { ObsDownloadProvider } from './Context/ObsDownloadContext';
import { OcrDownloadProvider } from './Context/OcrDownloadContext';
import { ReleaseNote } from './Models/WebSocketMessages';
import { ScrollProvider } from './Context/ScrollContext';
import { ModalProvider } from './Context/ModalContext';
import { GeneralMessagesProvider } from './Context/GeneralMessagesContext';
import MigrationOverlay from './Components/MigrationOverlay';
import SetupProfileModal from './Components/SetupProfileModal';
import { useAuth } from './Hooks/useAuth';
import { useProfile } from './Hooks/useUserProfile';

// Create a context for release notes that can be accessed globally
export const ReleaseNotesContext = createContext<{
  releaseNotes: ReleaseNote[];
  setReleaseNotes: (notes: ReleaseNote[]) => void;
}>({
  releaseNotes: [],
  setReleaseNotes: () => {},
});

function App() {
  useEffect(() => {
    themeChange(false);
  }, []);

  const { session, signOut } = useAuth();
  const { data: profile } = useProfile();

  const { selectedVideo, setSelectedVideo } = useSelectedVideo();
  const { selectedMenu, setSelectedMenu } = useSelectedMenu();
  const settings = useSettings();
  const appState = useAppState();
  const needsUsername = !settings.airplaneMode && session && profile?.username?.startsWith('user_');

  // Airplane mode hides all cloud features and must not keep a signed-in session.
  useEffect(() => {
    if (settings.airplaneMode && session) {
      signOut();
    }
  }, [settings.airplaneMode, session, signOut]);

  // If the current menu becomes hidden (and has no content keeping it visible),
  // fall back to the default (or first reachable item).
  useEffect(() => {
    const items =
      settings.menuItems && settings.menuItems.length > 0 ? settings.menuItems : DEFAULT_MENU_ITEMS;

    const isReachable = (id: MenuItemId) =>
      id === 'Settings' ||
      items.find((m) => m.id === id)?.visible === true ||
      menuItemHasContent(id, appState.content);

    if (isReachable(selectedMenu as MenuItemId)) return;

    const defaultId = (settings.defaultMenuItem ?? 'Full Sessions') as MenuItemId;
    const fallback =
      (isReachable(defaultId) ? defaultId : null) ??
      items.find((m) => isReachable(m.id as MenuItemId))?.id;
    if (fallback) {
      setSelectedMenu(fallback);
    }
  }, [
    settings.menuItems,
    settings.defaultMenuItem,
    selectedMenu,
    setSelectedMenu,
    appState.content,
  ]);

  const handleMenuSelection = (menu: MenuItemId | string) => {
    setSelectedVideo(null);
    setSelectedMenu(menu);
  };

  const renderContent = () => {
    if (selectedVideo) {
      return (
        <DndProvider backend={HTML5Backend}>
          <Video video={selectedVideo} />
        </DndProvider>
      );
    }

    switch (selectedMenu) {
      case 'Full Sessions':
        return <Sessions />;
      case 'Replay Buffer':
        return <ReplayBuffer />;
      case 'Clips':
        return <Clips />;
      case 'Highlights':
        return <Highlights />;
      case 'Settings':
        return <Settings />;
      default:
        return <Sessions />;
    }
  };

  return (
    <div className="flex h-screen w-screen">
      {needsUsername && <SetupProfileModal />}
      <div className="h-full">
        <Menu selectedMenu={selectedMenu} onSelectMenu={handleMenuSelection} />
      </div>
      <div className="flex-1 max-h-full overflow-auto">{renderContent()}</div>
    </div>
  );
}

export default function AppWrapper() {
  const [releaseNotes, setReleaseNotes] = useState<ReleaseNote[]>([]);

  return (
    <WebSocketProvider>
      <MigrationOverlay />
      <ScrollProvider>
        <SettingsProvider>
          <AppStateProvider>
            <ReleaseNotesContext.Provider value={{ releaseNotes, setReleaseNotes }}>
              <ModalProvider>
                <GeneralMessagesProvider>
                  <SegmentsProvider>
                    <DndProvider backend={HTML5Backend}>
                      <UploadProvider>
                        <ImportProvider>
                          <ContentMigrationProvider>
                            <ClippingProvider>
                              <AiHighlightsProvider>
                                <CompressionProvider>
                                  <UpdateProvider>
                                    <ObsDownloadProvider>
                                      <OcrDownloadProvider>
                                        <App />
                                      </OcrDownloadProvider>
                                    </ObsDownloadProvider>
                                  </UpdateProvider>
                                </CompressionProvider>
                              </AiHighlightsProvider>
                            </ClippingProvider>
                          </ContentMigrationProvider>
                        </ImportProvider>
                      </UploadProvider>
                    </DndProvider>
                  </SegmentsProvider>
                </GeneralMessagesProvider>
              </ModalProvider>
            </ReleaseNotesContext.Provider>
          </AppStateProvider>
        </SettingsProvider>
      </ScrollProvider>
    </WebSocketProvider>
  );
}
