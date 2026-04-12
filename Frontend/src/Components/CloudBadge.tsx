import { Cloud } from 'lucide-react';

type TooltipSide = 'top' | 'right' | 'bottom' | 'left';

interface CloudBadgeProps {
  tip?: string;
  side?: TooltipSide;
  className?: string;
  iconClassName?: string;
}

export default function CloudBadge({
  tip = 'Uses internet',
  side = 'top',
  className = '',
  iconClassName = '',
}: CloudBadgeProps) {
  return (
    <div
      className={`tooltip tooltip-${side} tooltip-primary inline-flex items-center ${className}`}
      data-tip={tip}
      aria-label={tip}
    >
      <Cloud className={`w-4 h-4 ml-0.5 opacity-50 ${iconClassName}`} />
    </div>
  );
}
