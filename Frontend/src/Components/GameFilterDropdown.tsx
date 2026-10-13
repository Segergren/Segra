import React, { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { ChevronDown } from 'lucide-react';
import { AnimatePresence, motion } from 'framer-motion';

export interface GameFilterItem {
  value: string;
  label: React.ReactNode;
}

interface GameFilterDropdownProps {
  items: GameFilterItem[];
  value: string;
  onChange: (value: string) => void;
  disabled?: boolean;
}

const MENU_WIDTH = 256;
const MENU_MAX_HEIGHT = 260;
const WIDTH_SPRING = { type: 'spring', stiffness: 600, damping: 42, mass: 0.6 } as const;

export default function GameFilterDropdown({
  items,
  value,
  onChange,
  disabled = false,
}: GameFilterDropdownProps) {
  const [isOpen, setIsOpen] = useState(false);
  const [triggerWidth, setTriggerWidth] = useState<number>();
  const containerRef = useRef<HTMLDivElement>(null);
  const buttonRef = useRef<HTMLButtonElement>(null);
  const measureRef = useRef<HTMLSpanElement>(null);
  const selected = items.find((i) => i.value === value) ?? items[0];

  if (disabled && isOpen) setIsOpen(false);

  useLayoutEffect(() => {
    const el = measureRef.current;
    if (!el) return;
    // +2 for the trigger's 1px border on each side
    const update = () => setTriggerWidth(el.offsetWidth + 2);
    update();
    const observer = new ResizeObserver(update);
    observer.observe(el);
    return () => observer.disconnect();
  }, []);

  useEffect(() => {
    if (!isOpen) return;
    const onOutside = (e: Event) => {
      if (!containerRef.current?.contains(e.target as Node)) setIsOpen(false);
    };
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        setIsOpen(false);
        buttonRef.current?.focus();
      }
    };
    document.addEventListener('mousedown', onOutside);
    document.addEventListener('focusin', onOutside);
    document.addEventListener('keydown', onKeyDown);
    return () => {
      document.removeEventListener('mousedown', onOutside);
      document.removeEventListener('focusin', onOutside);
      document.removeEventListener('keydown', onKeyDown);
    };
  }, [isOpen]);

  const panelWidth = Math.max(MENU_WIDTH, triggerWidth ?? 0);

  return (
    <div ref={containerRef} className="relative">
      <motion.button
        ref={buttonRef}
        type="button"
        aria-expanded={isOpen}
        aria-haspopup="listbox"
        disabled={disabled}
        onClick={() => setIsOpen((o) => !o)}
        initial={false}
        animate={triggerWidth ? { width: triggerWidth } : undefined}
        transition={WIDTH_SPRING}
        className={`btn btn-sm no-animation btn-secondary border border-base-400 h-8 p-0 overflow-hidden justify-end hover:text-primary hover:*:text-primary text-gray-300 ${disabled ? 'cursor-not-allowed opacity-50' : 'cursor-pointer'}`}
      >
        <span ref={measureRef} className="flex items-center w-max shrink-0 px-3">
          <span className="max-w-56 min-w-0 flex">{selected?.label}</span>
          <motion.span
            aria-hidden
            className="inline-flex items-center ml-2"
            animate={{ rotate: isOpen ? 180 : 0 }}
            transition={{ type: 'spring', stiffness: 300, damping: 20 }}
          >
            <ChevronDown size={20} />
          </motion.span>
        </span>
      </motion.button>

      <AnimatePresence>
        {isOpen && (
          <motion.ul
            role="listbox"
            initial={{ opacity: 0, scale: 0.95 }}
            animate={{ opacity: 1, scale: 1 }}
            exit={{ opacity: 0, scale: 0.95 }}
            transition={{ duration: 0.2, ease: 'easeOut' }}
            className="absolute right-0 top-full mt-1 z-999 origin-top p-2 overflow-y-auto bg-base-300 border border-base-400 rounded-box shadow"
            style={{ width: panelWidth, maxHeight: MENU_MAX_HEIGHT }}
          >
            {items.map((item) => {
              const isActive = item.value === value;
              return (
                <li key={item.value} role="option" aria-selected={isActive}>
                  <button
                    type="button"
                    className={`flex w-full items-center px-3 py-2.5 text-sm font-medium whitespace-nowrap rounded-lg hover:bg-white/5 transition-all duration-200 hover:pl-4 cursor-pointer ${isActive ? 'text-primary' : 'text-base-content'}`}
                    onClick={() => {
                      onChange(item.value);
                      setIsOpen(false);
                      buttonRef.current?.focus();
                    }}
                  >
                    {item.label}
                  </button>
                </li>
              );
            })}
          </motion.ul>
        )}
      </AnimatePresence>
    </div>
  );
}
