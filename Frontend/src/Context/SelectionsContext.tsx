import { createContext, useContext, useState, ReactNode } from 'react';
import { Selection } from '../Models/types';

interface SelectionsContextType {
  selections: Selection[];
  addSelection: (sel: Selection) => void;
  updateSelection: (sel: Selection) => void;
  updateSelectionsArray: (sel: Selection[]) => void;
  removeSelection: (id: number) => void;
  clearSelectionsForVideo: (fileName: string) => void;
  clearAllSelections: () => void;
}

const SelectionsContext = createContext<SelectionsContextType | undefined>(undefined);

export const SelectionsProvider = ({ children }: { children: ReactNode }) => {
  const [selections, setSelections] = useState<Selection[]>([]);

  const addSelection = (sel: Selection) => {
    setSelections((prev) => [...prev, sel]);
  };

  const updateSelection = (updatedSel: Selection) => {
    setSelections((prev) => prev.map((sel) => (sel.id === updatedSel.id ? updatedSel : sel)));
  };

  const updateSelectionsArray = (newSelections: Selection[]) => {
    setSelections(newSelections);
  };

  const removeSelection = (id: number) => {
    setSelections((prev) => prev.filter((sel) => sel.id !== id));
  };

  const clearSelectionsForVideo = (fileName: string) => {
    setSelections((prev) => prev.filter((sel) => sel.fileName !== fileName));
  };

  const clearAllSelections = () => {
    setSelections(() => []);
  };

  return (
    <SelectionsContext.Provider
      value={{
        selections,
        addSelection,
        updateSelection,
        removeSelection,
        clearSelectionsForVideo,
        updateSelectionsArray,
        clearAllSelections,
      }}
    >
      {children}
    </SelectionsContext.Provider>
  );
};

export const useSelections = (): SelectionsContextType => {
  const context = useContext(SelectionsContext);
  if (!context) {
    throw new Error('useSelections must be used within a SelectionsProvider');
  }
  return context;
};
