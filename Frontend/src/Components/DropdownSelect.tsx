import React, { useRef, useState } from 'react';
import { MdArrowDropDown } from 'react-icons/md';
import { motion } from 'framer-motion';

export interface DropdownItem {
  value: string;
  label: React.ReactNode;
}

interface DropdownSelectProps {
  items: DropdownItem[];
  value: string | undefined;
  onChange: (value: string) => void;
  placeholder?: React.ReactNode;
  buttonClassName?: string;
  menuClassName?: string;
  itemClassName?: string;
  disabled?: boolean;
  align?: 'start' | 'end';
  size?: 'sm' | 'md' | 'lg';
}

export default function DropdownSelect({
  items,
  value,
  onChange,
  placeholder = 'Select',
  buttonClassName,
  menuClassName,
  // ✅ use important prefix BEFORE the utility (`!text-primary`)
  itemClassName = 'justify-start text-sm font-medium hover:bg-white/5 rounded-md transition-all duration-200 hover:pl-3.5',
  disabled = false,
  align = 'end',
  size = 'md',
}: DropdownSelectProps) {
  const selected = items.find((i) => i.value === value);
  const [isOpen, setIsOpen] = useState(false);
  const containerRef = useRef<HTMLDivElement>(null);
  const buttonRef = useRef<HTMLButtonElement>(null);
  const [openDirection, setOpenDirection] = useState<'down' | 'up'>('down');
  const [menuMaxHeight, setMenuMaxHeight] = useState<number | undefined>();

  const computeMenuFit = React.useCallback(() => {
    const btn = buttonRef.current;
    if (!btn) return;
    const rect = btn.getBoundingClientRect();
    const margin = 8;
    const viewportH = window.innerHeight;
    const spaceBelow = Math.max(0, viewportH - rect.bottom - margin);
    const spaceAbove = Math.max(0, rect.top - margin);
    let dir: 'down' | 'up' = 'down';
    let available = spaceBelow;
    if (spaceBelow < 140 && spaceAbove > spaceBelow) {
      dir = 'up';
      available = spaceAbove;
    }
    setOpenDirection(dir);
    setMenuMaxHeight(Math.min(260, Math.max(120, Math.floor(available - 8))));
  }, []);

  React.useEffect(() => {
    if (!isOpen) return;
    const handler = () => computeMenuFit();
    window.addEventListener('resize', handler);
    window.addEventListener('scroll', handler, true);
    return () => {
      window.removeEventListener('resize', handler);
      window.removeEventListener('scroll', handler, true);
    };
  }, [isOpen, computeMenuFit]);

  React.useEffect(() => {
    function onDocMouseDown(e: MouseEvent) {
      if (!containerRef.current?.contains(e.target as Node)) setIsOpen(false);
    }
    function onKeyDown(e: KeyboardEvent) {
      if (e.key === 'Escape') setIsOpen(false);
    }
    document.addEventListener('mousedown', onDocMouseDown);
    document.addEventListener('keydown', onKeyDown);
    return () => {
      document.removeEventListener('mousedown', onDocMouseDown);
      document.removeEventListener('keydown', onKeyDown);
    };
  }, []);

  const sizeBtn = size === 'sm' ? 'btn-sm' : size === 'lg' ? 'btn-lg' : '';
  const computedMenuClassName = menuClassName ?? `dropdown-content menu menu-md bg-base-300 border border-base-400 rounded-box z-[999] w-full p-2 ${openDirection === 'up' ? 'mb-1' : 'mt-1'} shadow flex-nowrap`;

  return (
    <div
      ref={containerRef}
      className={`dropdown w-full ${align === 'end' ? 'dropdown-end' : ''} ${openDirection === 'up' ? 'dropdown-top' : ''} ${isOpen ? 'dropdown-open' : ''}`}
    >
      {/* Trigger */}
      <button
        ref={buttonRef}
        type="button"
        aria-expanded={isOpen}
        disabled={disabled}
        className={buttonClassName ?? `btn border-base-400 w-full justify-between ${sizeBtn}`}
        onMouseDown={(e) => {
          e.preventDefault();
          if (disabled) return;
          if (!isOpen) {
            computeMenuFit();
            setIsOpen(true);
          } else {
            setIsOpen(false);
          }
        }}
      >
        <span className="flex-1 text-left truncate text-base-content font-medium">
          {selected ? selected.label : placeholder}
        </span>
        <motion.span
          aria-hidden
          className="ml-2 inline-flex items-center"
          animate={{ rotate: isOpen ? 180 : 0 }}
          transition={{ type: 'spring', stiffness: 300, damping: 20 }}
        >
          <MdArrowDropDown size={22} />
        </motion.span>
      </button>

      {/* Menu (DaisyUI v5 structure) */}
      <ul
        tabIndex={0}
        className={computedMenuClassName}
        style={menuMaxHeight ? { maxHeight: `${menuMaxHeight}px`, overflowY: 'auto' } : undefined}
      >
        {items.map((item) => {
          const isActive = item.value === value;
          return (
            <li key={item.value} className="w-full">
              <button
                type="button"
                className={`${itemClassName} ${isActive ? 'active !text-primary' : 'text-base-content'} w-full whitespace-nowrap`}
                aria-current={isActive ? 'true' : undefined}
                onClick={() => {
                  if (disabled) return;
                  onChange(item.value);
                  setIsOpen(false);
                  (document.activeElement as HTMLElement | null)?.blur?.();
                }}
              >
                {item.label}
              </button>
            </li>
          );
        })}
      </ul>
    </div>
  );
}
