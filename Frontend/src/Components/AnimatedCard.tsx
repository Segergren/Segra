import React, { ReactNode, Ref } from 'react';
import { motion, useIsPresent } from 'framer-motion';

interface AnimatedCardProps {
  children: ReactNode;
  className?: string;
  ref?: Ref<HTMLDivElement>;
}

const spring = { type: 'spring', stiffness: 500, damping: 30, mass: 1 } as const;
// Tween so the collapse never overshoots below zero height
const collapse = { duration: 0.25, ease: 'easeInOut' } as const;

const AnimatedCard: React.FC<AnimatedCardProps> = ({ children, className = '', ref }) => {
  const isPresent = useIsPresent();

  return (
    <motion.div
      ref={ref}
      initial={{ opacity: 0, y: 50 }}
      animate={{ opacity: 1, y: 0 }}
      // Collapse while sliding out so the cards around it move along
      exit={{
        opacity: 0,
        y: 50,
        height: 0,
        marginTop: 0,
        transition: { ...spring, height: collapse, marginTop: collapse },
      }}
      transition={spring}
      className={`w-full ${isPresent ? '' : 'overflow-hidden'} ${className}`}
    >
      {children}
    </motion.div>
  );
};

export default AnimatedCard;
