import { useState, useMemo, useEffect, useRef } from 'react';
import { useSettings, useSettingsUpdater } from '../Context/SettingsContext';
import { Content, Selection } from '../Models/types';
import DashPlayer from '../Components/DashPlayer';
import ContentCard from '../Components/ContentCard';
import { useSelectedVideo } from '../Context/SelectedVideoContext';
import { MdSearch, MdLiveTv, MdContentCut, MdSave, MdRefresh, MdTimer } from 'react-icons/md';
import { sendMessageToBackend } from '../Utils/MessageUtils';

export default function Sessions() {
  const { state, gameSpecificConfig, replayBufferDuration } = useSettings();
  const updateSettings = useSettingsUpdater();
  const { setSelectedVideo } = useSelectedVideo();
  const { recording } = state;

  // State for active game filter
  const [activeGame, setActiveGame] = useState<string | null>(null);

  // Search query for games
  const [searchQuery, setSearchQuery] = useState('');

  // Player state
  const [currentTime, setCurrentTime] = useState(0);
  const [duration, setDuration] = useState(0);
  const [refreshKey, setRefreshKey] = useState(0);

  // Clipping state
  const [clipStart, setClipStart] = useState<number | null>(null);
  const [clipEnd, setClipEnd] = useState<number | null>(null);
  const [isSavingClip, setIsSavingClip] = useState(false);

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

  // Reset clipping when game changes
  useEffect(() => {
      setClipStart(null);
      setClipEnd(null);
  }, [activeGame]);

  // Get content for the active game
  const activeGameContent = useMemo(() => {
    if (!activeGame) return [];
    return state.content
        .filter(c => c.type === 'Session' && c.game === activeGame)
        .sort((a, b) => new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime());
  }, [state.content, activeGame]);

  // Determine the active video to play
  const activeVideo = useMemo(() => {
    if (!activeGame) return null;

    const isLive = recording?.game === activeGame;

    if (isLive) {
        const liveContent = activeGameContent.find(c => c.isLive);
        if (liveContent) return liveContent;
    }

    return activeGameContent.length > 0 ? activeGameContent[0] : null;
  }, [activeGame, activeGameContent, recording]);

  const getVideoUrl = (video: Content) => {
    // If it's a Dash manifest
    if (video.isLive || video.filePath.endsWith('.mpd')) {
        const parts = video.filePath.replace(/\\/g, '/').split('/');
        const fileName = parts.pop();
        const gameFolder = parts.pop();
        return `http://localhost:2222/api/live/${encodeURIComponent(gameFolder || '')}/${encodeURIComponent(fileName || '')}?t=${refreshKey}`;
    }
    // Standard video file
    return `http://localhost:2222/api/content?input=${encodeURIComponent(video.filePath)}&type=session`;
  };

  const handleCreateClip = () => {
      if (clipStart === null || clipEnd === null || !activeVideo) return;

      setIsSavingClip(true);

      const selection: Selection = {
          id: Math.floor(Math.random() * 1000000),
          type: activeVideo.type,
          startTime: clipStart,
          endTime: clipEnd,
          fileName: activeVideo.fileName, // Backend will likely ignore this for Session stitching, or use it to find source
          game: activeVideo.game,
          title: `Clip ${new Date().toLocaleString()}`,
          isLoading: true
      };

      sendMessageToBackend('CreateClip', { Selections: [selection] });

      // Reset after a delay
      setTimeout(() => {
          setIsSavingClip(false);
          setClipStart(null);
          setClipEnd(null);
      }, 1000);
  };

  const currentBufferDuration = useMemo(() => {
      if (!activeGame) return replayBufferDuration;
      return gameSpecificConfig?.[activeGame]?.bufferDuration ?? replayBufferDuration;
  }, [activeGame, gameSpecificConfig, replayBufferDuration]);

  const handleBufferDurationChange = (e: React.ChangeEvent<HTMLInputElement>) => {
      if (!activeGame) return;
      const val = parseInt(e.target.value);
      if (!isNaN(val) && val > 0) {
          updateSettings({
              gameSpecificConfig: {
                  ...gameSpecificConfig,
                  [activeGame]: { bufferDuration: val }
              }
          });
      }
  };

  const formatTime = (time: number) => {
      const minutes = Math.floor(time / 60);
      const seconds = Math.floor(time % 60);
      return `${minutes}:${seconds.toString().padStart(2, '0')}`;
  };

  return (
    <div className="flex h-full bg-base-200">
      {/* Sidebar - Game List */}
      <div className="w-64 bg-base-300 border-r border-base-content/10 flex flex-col transition-all duration-300">
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

        {/* Game Specific Settings */}
        {activeGame && (
            <div className="p-4 border-t border-base-content/10 bg-base-300">
                <label className="label">
                    <span className="label-text flex items-center gap-2">
                        <MdTimer /> Buffer Duration (s)
                    </span>
                </label>
                <input
                    type="number"
                    className="input input-sm input-bordered w-full"
                    value={currentBufferDuration}
                    onChange={handleBufferDurationChange}
                    min="10"
                    max="3600"
                />
                <p className="text-xs text-gray-500 mt-1">
                    For {activeGame}
                </p>
            </div>
        )}
      </div>

      {/* Main Content */}
      <div className="flex-1 flex flex-col h-full overflow-hidden">

        {/* Player Area */}
        <div className="h-3/5 bg-black relative border-b border-base-content/10 flex flex-col">
            {activeVideo ? (
                <div className="relative flex-1 bg-black min-h-0">
                    {activeVideo.isLive || activeVideo.filePath.endsWith('.mpd') ? (
                        <DashPlayer
                            key={`${activeVideo.filePath}-${refreshKey}`}
                            url={getVideoUrl(activeVideo)}
                            className="w-full h-full"
                            autoplay={true}
                            controls={true}
                            onTimeUpdate={setCurrentTime}
                            onDurationChange={setDuration}
                        />
                    ) : (
                        <video
                            src={getVideoUrl(activeVideo)}
                            className="w-full h-full"
                            controls
                            autoPlay={false}
                            onTimeUpdate={(e) => setCurrentTime(e.currentTarget.currentTime)}
                            onDurationChange={(e) => setDuration(e.currentTarget.duration)}
                        />
                    )}

                    {/* Overlay Info */}
                    <div className="absolute top-4 left-4 right-4 flex justify-between pointer-events-none">
                        <div className="bg-black/60 backdrop-blur-sm px-3 py-1 rounded text-white text-sm pointer-events-auto">
                            <h3 className="font-bold">{activeVideo.game}</h3>
                            <p className="text-xs opacity-80">{new Date(activeVideo.createdAt).toLocaleString()}</p>
                        </div>

                        <div className="flex items-center gap-2">
                             <button
                                className="btn btn-sm btn-ghost btn-circle bg-black/40 text-white hover:bg-black/60 pointer-events-auto"
                                onClick={() => setRefreshKey(k => k + 1)}
                                title="Refresh Player"
                            >
                                <MdRefresh className="w-5 h-5" />
                            </button>

                            {activeVideo.isLive && (
                                <div className="bg-error/90 backdrop-blur-sm px-3 py-1 rounded text-white text-sm font-bold animate-pulse flex items-center gap-2 pointer-events-auto">
                                    <MdLiveTv /> LIVE PREVIEW
                                </div>
                            )}
                        </div>
                    </div>

                    {/* Clipping Overlay - Always visible on bottom if not fullscreen, but DashPlayer controls might hide it.
                        We put controls OUTSIDE the video element or overlay them.
                        For now, let's put them in a bar BELOW the video.
                    */}
                </div>
            ) : (
                <div className="w-full h-full flex items-center justify-center text-gray-500">
                    <p>Select a game to view sessions</p>
                </div>
            )}

            {/* Clipping Controls Bar */}
            {activeVideo && (
                <div className="bg-base-200 p-2 flex items-center justify-between border-t border-base-content/10">
                    <div className="flex items-center gap-4">
                        <div className="flex items-center gap-2">
                            <span className="text-sm font-mono bg-base-300 px-2 py-1 rounded">{formatTime(currentTime)}</span>
                            <span className="text-xs text-gray-500">/ {formatTime(duration)}</span>
                        </div>

                        <div className="h-8 w-px bg-base-content/10"></div>

                        <div className="flex items-center gap-2">
                            <button
                                className={`btn btn-sm ${clipStart !== null ? 'btn-primary' : 'btn-outline'}`}
                                onClick={() => setClipStart(currentTime)}
                            >
                                <MdContentCut className="rotate-180" /> Set Start
                                {clipStart !== null && <span className="text-xs ml-1">({formatTime(clipStart)})</span>}
                            </button>
                            <button
                                className={`btn btn-sm ${clipEnd !== null ? 'btn-primary' : 'btn-outline'}`}
                                onClick={() => setClipEnd(currentTime)}
                            >
                                Set End <MdContentCut />
                                {clipEnd !== null && <span className="text-xs ml-1">({formatTime(clipEnd)})</span>}
                            </button>
                        </div>
                    </div>

                    <button
                        className={`btn btn-sm btn-success ${isSavingClip ? 'loading' : ''}`}
                        disabled={clipStart === null || clipEnd === null || isSavingClip}
                        onClick={handleCreateClip}
                    >
                        {!isSavingClip && <MdSave />}
                        Save Clip
                    </button>
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
                                    // Normally this sets selected video for the main editor.
                                    // Here we might just want to switch the 'activeVideo' within this view?
                                    // But activeVideo is computed from sorted list.
                                    // This UI is tricky. If I click a card, I expect it to play in the player ABOVE.
                                    // But 'activeVideo' logic currently forces "Live" or "Newest".
                                    // I should add state for `manualActiveVideo`.

                                    // Actually, let's keep it simple: Clicking a card goes to the dedicated Editor/Video page.
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
