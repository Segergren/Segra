import React, { useEffect, useState, useRef, useMemo, memo } from 'react';
import { AiProgress } from '../Models/types';

interface VideoBackgroundProps {
  videoUrl: string;
  bookmarks: Array<{ id: number; type: string; time: string }>;
}

// Memoized video component to prevent re-renders
const VideoBackground = memo(({ videoUrl, bookmarks }: VideoBackgroundProps) => {
  const [currentKillIndex, setCurrentKillIndex] = useState(0);
  const videoRef = useRef<HTMLVideoElement>(null);
  const endTimeRef = useRef<number>(0);

  // Convert time string (HH:MM:SS) to seconds
  const timeToSeconds = (timeStr: string): number => {
    const parts = timeStr.split(':').map(Number);
    if (parts.length === 3) {
      return parts[0] * 3600 + parts[1] * 60 + parts[2];
    } else if (parts.length === 2) {
      return parts[0] * 60 + parts[1];
    }
    return parts[0];
  };

  // Handle video playback of bookmarks
  useEffect(() => {
    if (!videoRef.current || bookmarks.length === 0) {
      // If no kill bookmarks, just play the video normally
      if (videoRef.current) {
        videoRef.current.play().catch((err) => console.error('Video play error:', err));
      }
      return;
    }

    const video = videoRef.current;
    const currentKill = bookmarks[currentKillIndex];
    const killTime = timeToSeconds(currentKill.time);
    const startTime = Math.max(0, killTime - 5); // 5 seconds before
    const endTime = killTime + 5; // 5 seconds after
    
    endTimeRef.current = endTime;

    // Seek to the start time
    video.currentTime = startTime;
    video.play().catch((err) => console.error('Video play error:', err));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [currentKillIndex]);

  // Separate effect for timeupdate listener to avoid re-creating it
  useEffect(() => {
    if (!videoRef.current || bookmarks.length === 0) return;

    const video = videoRef.current;

    const handleTimeUpdate = () => {
      if (video.currentTime >= endTimeRef.current) {
        // Move to next bookmark
        const nextIndex = (currentKillIndex + 1) % bookmarks.length;
        setCurrentKillIndex(nextIndex);
      }
    };

    video.addEventListener('timeupdate', handleTimeUpdate);

    return () => {
      video.removeEventListener('timeupdate', handleTimeUpdate);
    };
  }, [currentKillIndex, bookmarks.length]);

  return (
    <video
      ref={videoRef}
      src={videoUrl}
      className="w-full h-full object-cover"
      muted
      loop={bookmarks.length === 0}
      playsInline
    />
  );
});

VideoBackground.displayName = 'VideoBackground';

interface AiContentCardProps {
  progress: AiProgress;
}

const AiContentCard: React.FC<AiContentCardProps> = ({ progress }) => {
  const [animatedProgress, setAnimatedProgress] = useState(progress.progress);
  const [displayedPercentage, setDisplayedPercentage] = useState(progress.progress);
  const animationFrameRef = useRef<number | null>(null);
  const isFirstRender = useRef(true);

  // Memoize video URL and kill bookmarks to prevent re-renders
  const videoUrl = useMemo(() => {
    return `http://localhost:2222/api/content?input=${encodeURIComponent(progress.content.filePath)}&type=${progress.content.type.toLowerCase()}`;
  }, [progress.content.filePath, progress.content.type]);

  // Get all bookmarks
  const bookmarks = useMemo(() => {
    return progress.content.bookmarks;
  }, [progress.content.bookmarks]);

  useEffect(() => {
    // Skip animation on first render
    if (isFirstRender.current) {
      isFirstRender.current = false;
      setAnimatedProgress(progress.progress);
      setDisplayedPercentage(progress.progress);
      return;
    }

    const timer = setTimeout(() => {
      setAnimatedProgress(progress.progress);

      const startValue = displayedPercentage;
      const endValue = progress.progress;
      const duration = 1200;
      const startTime = performance.now();

      if (animationFrameRef.current) {
        cancelAnimationFrame(animationFrameRef.current);
      }

      const animateCount = (timestamp: number) => {
        const elapsed = timestamp - startTime;
        const progress = Math.min(elapsed / duration, 1);

        const easeOutQuad = (t: number) => t * (2 - t);
        const easedProgress = easeOutQuad(progress);

        const currentValue = startValue + (endValue - startValue) * easedProgress;
        setDisplayedPercentage(Math.round(currentValue));

        if (progress < 1) {
          animationFrameRef.current = requestAnimationFrame(animateCount);
        }
      };

      animationFrameRef.current = requestAnimationFrame(animateCount);
    }, 50);

    return () => {
      clearTimeout(timer);
      if (animationFrameRef.current) {
        cancelAnimationFrame(animationFrameRef.current);
      }
    };
  }, [progress.progress, displayedPercentage]);

  return (
    <div className="card card-compact shadow-xl w-full relative highlight-card overflow-hidden">
      <div className="absolute inset-0 rounded-lg highlight-border">
        <div className="card absolute inset-px bg-base-300 z-2">
          {/* Video Figure */}
          <figure className="relative aspect-video">
            {/* Video Background */}
            <VideoBackground videoUrl={videoUrl} bookmarks={bookmarks} />

            {/* Backdrop Blur Overlay */}
            <div className="absolute inset-0 backdrop-blur-[2px] bg-black/30" />

            {/* Centered Content */}
            <div className="absolute inset-0 flex flex-col items-center justify-center p-4">
              <p className="text-base  text-white/90">Generating AI Highlight</p>
              <div className="mt-3 w-2/3">
                <div className="w-full h-1.5 bg-white/20 rounded-full overflow-hidden">
                  <div
                    className="h-full bg-white/90 rounded-full"
                    style={{
                      width: `${animatedProgress}%`,
                      transition: 'width 1.2s ease-out',
                    }}
                  ></div>
                </div>
                <p className="text-xs text-center mt-1.5 text-white/80">
                  <span>{displayedPercentage}%</span>
                </p>
              </div>
            </div>
          </figure>

          {/* Card Body - Game Title */}
          <div className="card-body gap-1.5 pt-2 text-gray-300">
            <div className="flex justify-between items-center">
              <h2 className="card-title truncate h-[36px]">{progress.content.game}</h2>
            </div>
            <p className="text-sm">
              {progress.message}
            </p>
          </div>
        </div>
      </div>
    </div>
  );
};

export default AiContentCard;
