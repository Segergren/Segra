import React from 'react';
import { useSettings } from '../Context/SettingsContext';
import DashPlayer from '../Components/DashPlayer';

const LivePreview: React.FC = () => {
    const settings = useSettings();
    const recording = settings.state?.recording;

    if (!recording || !recording.filePath) {
        return (
            <div className="flex items-center justify-center w-full h-full">
                <p className="text-xl text-gray-400">No active recording.</p>
            </div>
        );
    }

    const videoUrl = `http://localhost:2222/api/content?input=${encodeURIComponent(recording.filePath)}&type=session`;

    return (
        <div className="flex flex-col w-full h-full p-4">
             <div className="mb-4">
                <h1 className="text-2xl font-bold text-white">Live Preview</h1>
                <p className="text-gray-400">Recording: {recording.game} - {recording.fileName}</p>
            </div>
            <div className="flex-1 w-full min-h-0 bg-black rounded-lg overflow-hidden relative shadow-lg border border-base-content/10">
                 <DashPlayer url={videoUrl} className="w-full h-full" autoplay={true} />

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
