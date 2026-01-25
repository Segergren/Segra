import React, { useEffect, useRef } from 'react';
import dashjs from 'dashjs';

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
        player.updateSettings({
            streaming: {
                lowLatencyEnabled: true,
                delay: {
                    liveDelay: 2
                }
            }
        });

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
