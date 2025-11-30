import { useEffect, useState, createContext } from 'react';
import Settings from './Pages/settings';
import Menu from './menu';
import Sessions from './Pages/sessions';
import Clips from './Pages/clips';
import ReplayBuffer from './Pages/replay-buffer';
import Highlights from './Pages/highlights';
import { SettingsProvider } from './Context/SettingsContext';
import Video from './Pages/video';
import { useSelectedVideo } from './Context/SelectedVideoContext';
import { useSelectedMenu } from './Context/SelectedMenuContext';
import { themeChange } from 'theme-change';
import { HTML5Backend } from 'react-dnd-html5-backend';
import { DndProvider } from 'react-dnd';
import { SelectionsProvider } from './Context/SelectionsContext';
import { UploadProvider } from './Context/UploadContext';
import { ImportProvider } from './Context/ImportContext';
import { WebSocketProvider } from './Context/WebSocketContext';
import { ClippingProvider } from './Context/ClippingContext';
import { AiHighlightsProvider } from './Context/AiHighlightsContext';
import { CompressionProvider } from './Context/CompressionContext';
import { UpdateProvider } from './Context/UpdateContext';
import { ReleaseNote } from './Models/WebSocketMessages';
import { ScrollProvider } from './Context/ScrollContext';
import { ModalProvider } from './Context/ModalContext';

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

  const { selectedVideo, setSelectedVideo } = useSelectedVideo();
  const { selectedMenu, setSelectedMenu } = useSelectedMenu();

  const handleMenuSelection = (menu: any) => {
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
      <ScrollProvider>
        <SettingsProvider>
          <ReleaseNotesContext.Provider value={{ releaseNotes, setReleaseNotes }}>
            <ModalProvider>
              <SelectionsProvider>
                <DndProvider backend={HTML5Backend}>
                  <UploadProvider>
                    <ImportProvider>
                      <ClippingProvider>
                        <AiHighlightsProvider>
                          <CompressionProvider>
                            <UpdateProvider>
                              <App />
                            </UpdateProvider>
                          </CompressionProvider>
                        </AiHighlightsProvider>
                      </ClippingProvider>
                    </ImportProvider>
                  </UploadProvider>
                </DndProvider>
              </SelectionsProvider>
            </ModalProvider>
          </ReleaseNotesContext.Provider>
        </SettingsProvider>
      </ScrollProvider>
    </WebSocketProvider>
  );
}
