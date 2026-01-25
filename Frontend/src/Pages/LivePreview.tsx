import React, { useState, useCallback } from 'react';
import { useSettings } from '../Context/SettingsContext';
import DashPlayer from '../Components/DashPlayer';
import { useWebSocketContext } from '../Context/WebSocketContext';

const LivePreview: React.FC = () => {
    const settings = useSettings();
    const { send } = useWebSocketContext();
    const recording = settings.state?.recording;

    const [currentTime, setCurrentTime] = useState(0);
    const [startTime, setStartTime] = useState<number | null>(null);
    const [endTime, setEndTime] = useState<number | null>(null);

    const handleTimeUpdate = useCallback((time: number) => {
        setCurrentTime(time);
    }, []);

    const formatTime = (seconds: number) => {
        const h = Math.floor(seconds / 3600);
        const m = Math.floor((seconds % 3600) / 60);
        const s = Math.floor(seconds % 60);
        if (h > 0) {
            return `${h}:${m.toString().padStart(2, '0')}:${s.toString().padStart(2, '0')}`;
        }
        return `${m}:${s.toString().padStart(2, '0')}`;
    };

    const handleSetStart = () => {
        setStartTime(currentTime);
        // If end time is before start time, clear it
        if (endTime !== null && endTime < currentTime) {
            setEndTime(null);
        }
    };

    const handleSetEnd = () => {
        setEndTime(currentTime);
        // If start time is after end time, clear it
        if (startTime !== null && startTime > currentTime) {
            setStartTime(null);
        }
    };

    const handleCreateClip = () => {
        if (!recording || startTime === null || endTime === null) return;

        const isDash = recording.filePath?.endsWith('.mpd');
        // If DASH, usage 'session' as filename (ClipService expects this for session.mpd)
        // Otherwise strip extension from recording.fileName if present
        const fileName = isDash ? 'session' : recording.fileName.replace(/\.[^/.]+$/, "");

        const selection = {
            id: Date.now(),
            startTime: startTime,
            endTime: endTime,
            fileName: fileName,
            type: 'Session', // Assuming live recordings are Sessions or Buffers in Session folder
            game: recording.game,
            title: `Clip ${new Date().toLocaleTimeString()}`,
            igdbId: 0 // We might not have this easily, but 0 or null is fine
        };

        send('CreateClip', {
            Selections: [selection]
        });

        // Optional: Reset selections or show notification
        setStartTime(null);
        setEndTime(null);
    };

    if (!recording || !recording.filePath) {
        return (
            <div className="flex flex-col w-full h-full p-4 gap-4">
                <h1 className="text-2xl font-bold text-white">Live Preview</h1>
                <div className="flex items-center justify-center w-full h-full flex-1 bg-black rounded-lg border border-base-content/10">
                    <p className="text-xl text-gray-400">No active recording.</p>
                </div>
            </div>
        );
    }

    // recording.filePath is an absolute path, e.g., "C:/Users/.../Segra/Sessions/GameName/session.mpd"
    // We need to extract the GameName and FileName parts to construct the /api/live URL.
    const parts = recording.filePath.replace(/\\/g, '/').split('/');
    const fileName = parts.pop();
    const gameFolder = parts.pop();
    const videoUrl = `http://localhost:2222/api/live/${encodeURIComponent(gameFolder || '')}/${encodeURIComponent(fileName || '')}`;

    return (
        <div className="flex flex-col w-full h-full p-4 gap-4">
             <div className="flex justify-between items-end">
                <div>
                    <h1 className="text-2xl font-bold text-white">Live Preview</h1>
                    <p className="text-gray-400">Recording: {recording.game} - {recording.fileName}</p>
                </div>

                {/* Clip Creation Controls */}
                <div className="flex items-center gap-4 bg-base-300 p-2 rounded-lg">
                    <div className="flex flex-col items-center">
                        <span className="text-xs text-gray-400">Current</span>
                        <span className="font-mono">{formatTime(currentTime)}</span>
                    </div>

                    <div className="divider divider-horizontal mx-0"></div>

                    <div className="flex items-center gap-2">
                         <div className="flex flex-col items-center">
                            <span className="text-xs text-gray-400">Start</span>
                            <span className="font-mono text-primary">{startTime !== null ? formatTime(startTime) : '--:--'}</span>
                        </div>
                        <button
                            className="btn btn-sm btn-outline"
                            onClick={handleSetStart}
                        >
                            Set Start
                        </button>
                    </div>

                    <div className="flex items-center gap-2">
                        <div className="flex flex-col items-center">
                            <span className="text-xs text-gray-400">End</span>
                            <span className="font-mono text-primary">{endTime !== null ? formatTime(endTime) : '--:--'}</span>
                        </div>
                        <button
                            className="btn btn-sm btn-outline"
                            onClick={handleSetEnd}
                        >
                            Set End
                        </button>
                    </div>

                    <div className="divider divider-horizontal mx-0"></div>

                    <button
                        className="btn btn-sm btn-primary"
                        disabled={startTime === null || endTime === null || startTime >= endTime}
                        onClick={handleCreateClip}
                    >
                        Save Clip
                    </button>
                </div>
            </div>

            <div className="flex-1 w-full min-h-0 bg-black rounded-lg overflow-hidden relative shadow-lg border border-base-content/10">
                 <DashPlayer
                    url={videoUrl}
                    className="w-full h-full"
                    autoplay={true}
                    onTimeUpdate={handleTimeUpdate}
                 />

                 <div className="absolute top-4 right-4 flex items-center gap-2">
                    <span className="flex items-center gap-1 px-2 py-1 text-xs font-bold text-white bg-red-600 rounded animate-pulse">
                        LIVE
                    </span>
                 </div>
            </div>
        </div>
    );
};

export default LivePreview;
