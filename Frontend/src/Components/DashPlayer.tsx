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
    onTimeUpdate?: (currentTime: number) => void;
    onDurationChange?: (duration: number) => void;
}

const DashPlayer: React.FC<DashPlayerProps> = ({
    url,
    autoplay = true,
    className,
    width = '100%',
    height = '100%',
    controls = true,
    muted = false,
    onTimeUpdate,
    onDurationChange
}) => {
    const videoRef = useRef<HTMLVideoElement>(null);
    const playerRef = useRef<dashjs.MediaPlayerClass | null>(null);

    useEffect(() => {
        if (!videoRef.current || !url) return;

        // Initialize player
        const player = dashjs.MediaPlayer().create();

        // Configure low latency for live streams if needed
        // Note: lowLatencyEnabled was moved in dash.js 5.x but types might lag or vary.
        // We cast to any to support both structures until types are fully aligned.
        const settings = {
            streaming: {
                delay: {
                    liveDelay: 4,
                },
                buffer: {
                    lowLatencyStallThreshold: 0.5,
                },
                liveCatchup: {
                    enabled: false
                }
            },
            debug: {
                logLevel: dashjs.Debug.LOG_LEVEL_INFO
            }
        };

        player.updateSettings(settings);

        player.on(dashjs.MediaPlayer.events.ERROR, (e: any) => {
            console.error('DashPlayer Error:', e);
        });

        player.initialize(videoRef.current, url, autoplay);

        playerRef.current = player;

        const handleTimeUpdate = () => {
            if (videoRef.current && onTimeUpdate) {
                onTimeUpdate(videoRef.current.currentTime);
            }
        };

        const handleDurationChange = () => {
             if (videoRef.current && onDurationChange) {
                onDurationChange(videoRef.current.duration);
            }
        };

        const videoElement = videoRef.current;
        videoElement.addEventListener('timeupdate', handleTimeUpdate);
        videoElement.addEventListener('durationchange', handleDurationChange);

        // Cleanup
        return () => {
            videoElement.removeEventListener('timeupdate', handleTimeUpdate);
            videoElement.removeEventListener('durationchange', handleDurationChange);

            if (player) {
                player.destroy();
                playerRef.current = null;
            }
        };
    }, [url, autoplay, onTimeUpdate, onDurationChange]);

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
