import { createContext, useContext, useState, ReactNode } from 'react';
import { Segment } from '../Models/types';

interface SegmentsContextType {
  segments: Segment[];
  addSegment: (seg: Segment) => void;
  updateSegment: (seg: Segment) => void;
  updateSegmentsArray: (seg: Segment[]) => void;
  removeSegment: (id: number) => void;
  clearSegmentsForVideo: (fileName: string) => void;
  clearAllSegments: () => void;
}

const SegmentsContext = createContext<SegmentsContextType | undefined>(undefined);

export const SegmentsProvider = ({ children }: { children: ReactNode }) => {
  const [segments, setSegments] = useState<Segment[]>([]);

  const addSegment = (seg: Segment) => {
    setSegments((prev) => [...prev, seg]);
  };

  const updateSegment = (updatedSeg: Segment) => {
    setSegments((prev) => prev.map((seg) => (seg.id === updatedSeg.id ? updatedSeg : seg)));
  };

  const updateSegmentsArray = (newSegments: Segment[]) => {
    setSegments(newSegments);
  };

  const removeSegment = (id: number) => {
    setSegments((prev) => prev.filter((seg) => seg.id !== id));
  };

  const clearSegmentsForVideo = (fileName: string) => {
    setSegments((prev) => prev.filter((seg) => seg.fileName !== fileName));
  };

  const clearAllSegments = () => {
    setSegments(() => []);
  };

  return (
    <SegmentsContext.Provider
      value={{
        segments,
        addSegment,
        updateSegment,
        removeSegment,
        clearSegmentsForVideo,
        updateSegmentsArray,
        clearAllSegments,
      }}
    >
      {children}
    </SegmentsContext.Provider>
  );
};

export const useSegments = (): SegmentsContextType => {
  const context = useContext(SegmentsContext);
  if (!context) {
    throw new Error('useSegments must be used within a SegmentsProvider');
  }
  return context;
};
