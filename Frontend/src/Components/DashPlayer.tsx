import React, { useEffect, useRef } from 'react';
import * as dashjs from 'dashjs';

interface DashPlayerProps {
    url: string;
    autoplay?: boolean;
    className?: string;
    width?: string | number;
    height?: string | number;
    controls?: boolean;
    muted?: boolean;
}

const DashPlayer: React.FC<DashPlayerProps> = ({
    url,
    autoplay = true,
    className,
    width = '100%',
    height = '100%',
    controls = true,
    muted = false
}) => {
    const videoRef = useRef<HTMLVideoElement>(null);
    const playerRef = useRef<dashjs.MediaPlayerClass | null>(null);

    useEffect(() => {
        if (!videoRef.current || !url) return;

        // Initialize player
        const player = dashjs.MediaPlayer().create();
        player.initialize(videoRef.current, url, autoplay);

        // Configure low latency for live streams if needed
        // Note: lowLatencyEnabled was moved in dash.js 5.x but types might lag or vary.
        // We cast to any to support both structures until types are fully aligned.
        const settings = {
            streaming: {
                delay: {
                    liveDelay: 2,
                },
                buffer: {
                    lowLatencyStallThreshold: 0.5,
                },
            },
        };

        // Inject legacy property if needed, though types don't show it.
        // For Dash.js 5.x, lowLatencyEnabled is often implied by other settings or profile,
        // but explicit config might be under streaming.lowLatencyEnabled (legacy) or just handled by the profile.
        // Since strict mode complains, we avoid the unknown property.
        // If specific low latency toggle is needed and not in types, we'd cast:
        // (settings.streaming as any).lowLatencyEnabled = true;

        player.updateSettings(settings);

        playerRef.current = player;

        // Cleanup
        return () => {
            if (player) {
                player.destroy();
                playerRef.current = null;
            }
        };
    }, [url, autoplay]);

    return (
        <div className={className} style={{ width, height }}>
            <video
                ref={videoRef}
                controls={controls}
                muted={muted}
                style={{ width: '100%', height: '100%' }}
            />
        </div>
    );
};

export default DashPlayer;
