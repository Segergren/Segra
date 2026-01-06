import React, { createContext, useContext, useRef, useState, ReactNode } from 'react';

interface ModalContextType {
  openModal: (content: ReactNode) => void;
  closeModal: () => void;
  isModalOpen: boolean;
}

const ModalContext = createContext<ModalContextType | undefined>(undefined);

export const ModalProvider: React.FC<{ children: ReactNode }> = ({ children }) => {
  const modalRef = useRef<HTMLDialogElement>(null);
  const [modalContent, setModalContent] = useState<ReactNode>(null);
  // Track if the initial mousedown started on the backdrop
  const backdropMouseDownRef = useRef<boolean>(false);

  const openModal = (content: ReactNode) => {
    setModalContent(content);
    if (modalRef.current) {
      modalRef.current.showModal();
    }
  };

  const closeModal = () => {
    setModalContent(null);
    if (modalRef.current) {
      modalRef.current.close();
    }
  };

  const isModalOpen = modalContent !== null;

  return (
    <ModalContext.Provider value={{ openModal, closeModal, isModalOpen }}>
      {children}
      <dialog
        ref={modalRef}
        className="modal modal-bottom sm:modal-middle"
        onMouseDown={(e) => {
          // Only mark as backdrop interaction if the mousedown started on the dialog backdrop
          backdropMouseDownRef.current = e.target === modalRef.current;
        }}
        onClick={(e) => {
          // Close only if both mousedown and click occurred on the backdrop
          if (e.target === modalRef.current && backdropMouseDownRef.current) {
            backdropMouseDownRef.current = false;
            closeModal();
          } else {
            backdropMouseDownRef.current = false;
          }
        }}
      >
        <div className="modal-box max-h-[90vh] bg-base-300" onClick={(e) => e.stopPropagation()}>
          {modalContent}
        </div>
        <form
          method="dialog"
          className="modal-backdrop"
          onMouseDown={() => {
            // Mark that interaction started on the backdrop overlay
            backdropMouseDownRef.current = true;
          }}
          onClick={() => {
            // Close only if interaction started on backdrop (prevents drag-out closes)
            if (backdropMouseDownRef.current) {
              backdropMouseDownRef.current = false;
              closeModal();
            }
          }}
        >
          <button>close</button>
        </form>
      </dialog>
    </ModalContext.Provider>
  );
};

export const useModal = (): ModalContextType => {
  const context = useContext(ModalContext);
  if (!context) {
    throw new Error('useModal must be used within a ModalProvider');
  }
  return context;
};
