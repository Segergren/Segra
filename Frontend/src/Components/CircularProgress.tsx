import { useState, useEffect } from 'react';

interface CircularProgressProps {
  progress: number;
  size?: number;
  strokeWidth?: number;
  duration?: number;
  className?: string;
  showText?: boolean;
}

export default function CircularProgress({
  progress,
  size = 24,
  strokeWidth = 2,
  duration = 700,
  className = '',
  showText = false,
}: CircularProgressProps) {
  const radius = (size - strokeWidth) / 2;
  const circumference = 2 * Math.PI * radius;
  const offset = circumference - (progress / 100) * circumference;

  const [displayProgress, setDisplayProgress] = useState(0);

  useEffect(() => {
    if (!showText) return;

    if (progress > 95) {
      setDisplayProgress(progress);
      return;
    }

    const timer = setInterval(() => {
      setDisplayProgress((prev) => {
        const diff = progress - prev;
        if (Math.abs(diff) < 0.1) return progress;
        return prev + diff * 0.15;
      });
    }, 50);

    return () => clearInterval(timer);
  }, [progress, showText]);

  const fontSize = size * 0.3;

  return (
    <div
      className={`relative inline-flex items-center justify-center ${className}`}
      style={{ width: size, height: size }}
    >
      <svg className="-rotate-90" width={size} height={size} viewBox={`0 0 ${size} ${size}`}>
        <circle
          cx={size / 2}
          cy={size / 2}
          r={radius}
          fill="none"
          stroke="currentColor"
          strokeWidth={strokeWidth}
          className="text-base-100"
        />
        <circle
          cx={size / 2}
          cy={size / 2}
          r={radius}
          fill="none"
          stroke="currentColor"
          strokeWidth={strokeWidth}
          strokeLinecap="round"
          className="text-primary ease-in-out"
          style={{
            strokeDasharray: `${circumference} ${circumference}`,
            strokeDashoffset: offset,
            transitionProperty: 'all',
            transitionDuration: `${duration}ms`,
          }}
        />
      </svg>
      {showText && (
        <span className="absolute text-gray-300 font-medium" style={{ fontSize }}>
          {Math.round(displayProgress)}%
        </span>
      )}
    </div>
  );
}
