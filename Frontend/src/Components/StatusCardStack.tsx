import { ReactNode, useLayoutEffect, useRef, useState } from 'react';
import { AnimatePresence } from 'framer-motion';
import AnimatedCard from './AnimatedCard';

export interface StatusCard {
  key: string;
  node: ReactNode;
  // Stays at the bottom regardless of age
  pinned?: boolean;
}

const CARD_GAP = 8;
const BADGE_HEIGHT = 24;

// Stacks cards oldest at the bottom, shows the oldest that fit and collapses the newer ones into a +N badge
export default function StatusCardStack({ cards }: { cards: StatusCard[] }) {
  const areaRef = useRef<HTMLDivElement>(null);
  const cardRefs = useRef(new Map<string, HTMLDivElement>());
  const [visibleCount, setVisibleCount] = useState(cards.length);

  // Keys in the order they first appeared
  const [seenOrder, setSeenOrder] = useState<string[]>([]);
  const keys = cards.map((card) => card.key);
  const order = [
    ...seenOrder.filter((key) => keys.includes(key)),
    ...keys.filter((key) => !seenOrder.includes(key)),
  ];
  if (order.join('\n') !== seenOrder.join('\n')) setSeenOrder(order);

  const byAge = [...cards].sort((a, b) => order.indexOf(a.key) - order.indexOf(b.key));
  const bottomUp = [
    ...byAge.filter((card) => card.pinned),
    ...byAge.filter((card) => !card.pinned),
  ];
  const keysJson = JSON.stringify(bottomUp.map((card) => card.key));

  useLayoutEffect(() => {
    const area = areaRef.current;
    if (!area) return;
    const keys: string[] = JSON.parse(keysJson);

    const measure = () => {
      const style = getComputedStyle(area);
      const available =
        area.clientHeight - parseFloat(style.paddingTop) - parseFloat(style.paddingBottom);
      let used = 0;
      let count = 0;
      for (const key of keys) {
        const next = used + CARD_GAP + (cardRefs.current.get(key)?.offsetHeight ?? 0);
        const badge = count + 1 < keys.length ? CARD_GAP + BADGE_HEIGHT : 0;
        if (next + badge > available) break;
        used = next;
        count++;
      }
      setVisibleCount(count);
    };

    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(area);
    cardRefs.current.forEach((el) => observer.observe(el));
    return () => observer.disconnect();
  }, [keysJson]);

  const shownCount = Math.min(visibleCount, cards.length);
  const hiddenCount = cards.length - shownCount;
  const topDown = [...bottomUp].reverse();

  return (
    // Clip only vertically, and not while a dropdown is open, so card popovers can escape the stack
    <div
      ref={areaRef}
      className="flex-1 min-h-0 flex flex-col overflow-y-clip has-[.dropdown-open,.dropdown:focus-within]:overflow-visible px-2 pb-2"
    >
      {/* Each card carries its own top gap so it collapses together with the card on exit */}
      <div className="relative mt-auto">
        {hiddenCount > 0 && (
          <div className="mx-2 mt-2 flex h-6 items-center justify-center text-xs font-medium text-gray-400">
            +{hiddenCount}
          </div>
        )}
        <AnimatePresence>
          {topDown.map((card, index) => (
            <AnimatedCard
              key={card.key}
              ref={(el) => {
                if (el) cardRefs.current.set(card.key, el);
                else cardRefs.current.delete(card.key);
              }}
              className={`mt-2 ${index < hiddenCount ? 'absolute inset-x-0 top-0 invisible' : ''}`}
            >
              {card.node}
            </AnimatedCard>
          ))}
        </AnimatePresence>
      </div>
    </div>
  );
}
