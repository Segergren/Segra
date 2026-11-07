import { createContext, useContext, useState, ReactNode, useCallback } from 'react';
import { Content } from '../Models/types';

interface SelectedVideoContextProps {
  selectedVideo: Content | null;
  // Ids of the list the video was opened from, for previous/next navigation
  playlist: string[];
  setSelectedVideo: (video: Content | null, playlist?: string[]) => void;
}

const SelectedVideoContext = createContext<SelectedVideoContextProps | undefined>(undefined);

export const SelectedVideoProvider = ({ children }: { children: ReactNode }) => {
  const [selectedVideo, setSelectedVideoState] = useState<Content | null>(null);
  const [playlist, setPlaylist] = useState<string[]>([]);

  const setSelectedVideo = useCallback((video: Content | null, list?: string[]) => {
    setSelectedVideoState(video);
    if (!video) setPlaylist([]);
    else if (list) setPlaylist(list);
  }, []);

  return (
    <SelectedVideoContext.Provider value={{ selectedVideo, playlist, setSelectedVideo }}>
      {children}
    </SelectedVideoContext.Provider>
  );
};

export const useSelectedVideo = () => {
  const context = useContext(SelectedVideoContext);
  if (!context) {
    throw new Error('useSelectedVideo must be used within a SelectedVideoProvider');
  }
  return context;
};
