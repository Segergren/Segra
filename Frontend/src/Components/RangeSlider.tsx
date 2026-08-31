import { CSSProperties, InputHTMLAttributes } from 'react';

interface RangeSliderProps extends Omit<InputHTMLAttributes<HTMLInputElement>, 'type'> {
  fillColor?: string;
}

export default function RangeSlider({
  fillColor = 'var(--color-primary)',
  className = '',
  style,
  ...rest
}: RangeSliderProps) {
  const min = Number(rest.min ?? 0);
  const max = Number(rest.max ?? 100);
  const value = Number(rest.value ?? min);
  const pct = max > min ? ((value - min) / (max - min)) * 100 : 0;

  return (
    <input
      type="range"
      className={`h-1.5 rounded-lg appearance-none cursor-pointer disabled:cursor-not-allowed disabled:opacity-60 [&::-webkit-slider-thumb]:appearance-none [&::-webkit-slider-thumb]:w-3.5 [&::-webkit-slider-thumb]:h-3.5 [&::-webkit-slider-thumb]:rounded-full [&::-webkit-slider-thumb]:bg-[var(--slider-fill)] [&::-moz-range-thumb]:w-3.5 [&::-moz-range-thumb]:h-3.5 [&::-moz-range-thumb]:rounded-full [&::-moz-range-thumb]:bg-[var(--slider-fill)] [&::-moz-range-thumb]:border-0 ${className}`}
      style={
        {
          '--slider-fill': fillColor,
          backgroundImage: `linear-gradient(to right, ${fillColor} ${pct}%, #4b5563 ${pct}%)`,
          ...style,
        } as CSSProperties
      }
      {...rest}
    />
  );
}
