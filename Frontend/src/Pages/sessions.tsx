import React, { useState, useMemo, useEffect } from 'react';
import { useSettings } from '../Context/SettingsContext';
import { Content } from '../Models/types';
import DashPlayer from '../Components/DashPlayer';
import ContentCard from '../Components/ContentCard';
import { useSelectedVideo } from '../Context/SelectedVideoContext';
import { MdSearch, MdLiveTv } from 'react-icons/md';

export default function Sessions() {
  const { state } = useSettings();
  const { setSelectedVideo } = useSelectedVideo();
  const { recording } = state;

  // State for active game filter
  const [activeGame, setActiveGame] = useState<string | null>(null);

  // Search query for games
  const [searchQuery, setSearchQuery] = useState('');

  // Extract unique games from content
  const games = useMemo(() => {
    const gameSet = new Set<string>();
    state.content
        .filter(c => c.type === 'Session')
        .forEach(c => gameSet.add(c.game));

    // Also add the currently recording game if it exists
    if (recording?.game) {
        gameSet.add(recording.game);
    }

    return Array.from(gameSet).sort();
  }, [state.content, recording?.game]);

  // Filter games based on search
  const filteredGames = useMemo(() => {
    return games.filter(g => g.toLowerCase().includes(searchQuery.toLowerCase()));
  }, [games, searchQuery]);

  // If no active game is selected, select the first one or the recording one
  useEffect(() => {
    if (!activeGame && games.length > 0) {
        if (recording?.game) {
            setActiveGame(recording.game);
        } else {
            setActiveGame(games[0]);
        }
    }
  }, [games, recording?.game]);

  // Get content for the active game
  const activeGameContent = useMemo(() => {
    if (!activeGame) return [];
    return state.content
        .filter(c => c.type === 'Session' && c.game === activeGame)
        .sort((a, b) => new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime());
  }, [state.content, activeGame]);

  // Determine the active video to play
  // Priority:
  // 1. Live session for the active game (if recording)
  // 2. Most recent session for the active game
  const activeVideo = useMemo(() => {
    if (!activeGame) return null;

    // Check if the active game is currently recording
    const isLive = recording?.game === activeGame;

    if (isLive) {
        // Find the live content object
        // Note: OBSService creates a metadata file immediately on start, so it should be in state.content
        // We look for one with isLive=true
        const liveContent = activeGameContent.find(c => c.isLive);
        if (liveContent) return liveContent;

        // Fallback if metadata isn't synced yet but we know we are recording this game
        // This might happen briefly on start.
        // We can construct a temporary Content object or just wait for sync.
        // For robustness, let's wait for sync (it takes <1s).
    }

    // Return the most recent video
    return activeGameContent.length > 0 ? activeGameContent[0] : null;
  }, [activeGame, activeGameContent, recording]);

  const getVideoUrl = (video: Content) => {
    // If it's a Dash manifest, use the Dash player URL construction
    if (video.isLive || video.filePath.endsWith('.mpd')) {
        const parts = video.filePath.replace(/\\/g, '/').split('/');
        const fileName = parts.pop();
        const gameFolder = parts.pop();
        return `http://localhost:2222/api/live/${encodeURIComponent(gameFolder || '')}/${encodeURIComponent(fileName || '')}`;
    }
    // Standard video file
    return `http://localhost:2222/api/content?input=${encodeURIComponent(video.filePath)}&type=session`;
  };

  return (
    <div className="flex h-full bg-base-200">
      {/* Sidebar - Game List */}
      <div className="w-64 bg-base-300 border-r border-base-content/10 flex flex-col">
        <div className="p-4 border-b border-base-content/10">
            <h2 className="text-xl font-bold mb-4">Games</h2>
            <div className="relative">
                <input
                    type="text"
                    placeholder="Search games..."
                    className="input input-sm input-bordered w-full pr-8"
                    value={searchQuery}
                    onChange={(e) => setSearchQuery(e.target.value)}
                />
                <MdSearch className="absolute right-2 top-1/2 -translate-y-1/2 text-gray-400" />
            </div>
        </div>

        <div className="flex-1 overflow-y-auto">
            {filteredGames.map(game => (
                <button
                    key={game}
                    className={`w-full text-left px-4 py-3 flex items-center justify-between hover:bg-base-200 transition-colors ${activeGame === game ? 'bg-base-200 border-l-4 border-primary' : ''}`}
                    onClick={() => setActiveGame(game)}
                >
                    <span className="truncate font-medium">{game}</span>
                    {recording?.game === game && (
                        <span className="flex items-center gap-1 text-xs text-error animate-pulse font-bold">
                            <MdLiveTv />
                            REC
                        </span>
                    )}
                </button>
            ))}
            {filteredGames.length === 0 && (
                <div className="p-4 text-center text-gray-500 text-sm">
                    No games found
                </div>
            )}
        </div>
      </div>

      {/* Main Content */}
      <div className="flex-1 flex flex-col h-full overflow-hidden">

        {/* Player Area */}
        <div className="h-1/2 bg-black relative border-b border-base-content/10">
            {activeVideo ? (
                <>
                    {activeVideo.isLive || activeVideo.filePath.endsWith('.mpd') ? (
                        <DashPlayer
                            url={getVideoUrl(activeVideo)}
                            className="w-full h-full"
                            autoplay={true}
                            controls={true}
                        />
                    ) : (
                        <video
                            src={getVideoUrl(activeVideo)}
                            className="w-full h-full"
                            controls
                            autoPlay={false} // Don't autoplay old sessions by default to avoid noise
                        />
                    )}

                    {/* Overlay Info */}
                    <div className="absolute top-4 left-4 right-4 flex justify-between pointer-events-none">
                        <div className="bg-black/60 backdrop-blur-sm px-3 py-1 rounded text-white text-sm pointer-events-auto">
                            <h3 className="font-bold">{activeVideo.game}</h3>
                            <p className="text-xs opacity-80">{new Date(activeVideo.createdAt).toLocaleString()}</p>
                        </div>

                        {activeVideo.isLive && (
                            <div className="bg-error/90 backdrop-blur-sm px-3 py-1 rounded text-white text-sm font-bold animate-pulse flex items-center gap-2 pointer-events-auto">
                                <MdLiveTv /> LIVE PREVIEW
                            </div>
                        )}
                    </div>
                </>
            ) : (
                <div className="w-full h-full flex items-center justify-center text-gray-500">
                    <p>Select a game to view sessions</p>
                </div>
            )}
        </div>

        {/* Sessions List */}
        <div className="flex-1 overflow-y-auto p-4 bg-base-200">
            <div className="flex justify-between items-center mb-4">
                <h3 className="text-lg font-bold">
                    Sessions for {activeGame || '...'}
                </h3>
                <span className="text-sm text-gray-500">{activeGameContent.length} recordings</span>
            </div>

            {activeGameContent.length > 0 ? (
                <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-4">
                    {activeGameContent.map((content) => (
                        <div key={content.fileName} className={`${activeVideo?.fileName === content.fileName ? 'ring-2 ring-primary rounded-xl' : ''}`}>
                            <ContentCard
                                content={content}
                                type="Session"
                                onClick={(video) => {
                                    // Normally ContentCard onClick sets the selectedVideo global
                                    // But here we want to play it in the persistent player?
                                    // If we set selectedVideo, App will switch to Video.tsx view.
                                    // The user said "in the full sessions tab i want there to always be a player there"
                                    // This implies we should NOT switch page.
                                    // So we probably shouldn't use setSelectedVideo.
                                    // But ContentCard logic might be hardwired if I pass `onClick`.
                                    // If I pass `onClick`, ContentCard calls it.
                                    // But activeVideo is derived from activeGame + sorting.
                                    // If I want to play a SPECIFIC older video, I need state for `manualSelectedVideo`.
                                    // Let's assume we just want to jump to Video page for detailed editing?
                                    // "list of my games ... click one ... loads session.mpd ... into the player".
                                    // This referred to the Game list.
                                    // If I click a specific session card below, what should happen?
                                    // Standard behavior (Edit/Detailed View) seems appropriate.
                                    setSelectedVideo(video);
                                }}
                            />
                        </div>
                    ))}
                </div>
            ) : (
                <div className="text-center py-10 text-gray-500">
                    No recordings found for this game.
                </div>
            )}
        </div>
      </div>
    </div>
  );
}
