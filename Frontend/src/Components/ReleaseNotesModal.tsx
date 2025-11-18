import { ReleaseNote } from '../Models/WebSocketMessages';
import Markdown from 'markdown-to-jsx';
import { useContext, useEffect, useState } from 'react';
import { gt } from 'semver';
import { ReleaseNotesContext } from '../App';

interface ReleaseNotesModalProps {
  onClose: () => void;
  filterVersion: string | null;
}

// Custom CSS for markdown content
const markdownStyles = `
  .markdown-content ul {
    list-style-type: disc;
    padding-top: 0.75rem;
    padding-left: 2rem;
    margin-bottom: 1.5rem;
  }
  
  .markdown-content ul li {
    display: list-item;
    margin-bottom: 0.75rem;
    line-height: 1.75rem;
  }
  
  .markdown-content ul li::marker {
    color: hsl(var(--p));
    font-size: 1.5em;
  }
  
  .markdown-content ol {
    list-style-type: decimal;
    padding-left: 2rem;
    margin-bottom: 1.5rem;
  }
  
  .markdown-content ol li {
    display: list-item;
    margin-bottom: 0.75rem;
    line-height: 1.75rem;
  }
  
  .markdown-content img {
    border-radius: 0.5rem;
    pointer-events: none;
    max-width: 100%;
    margin: 1.5rem 0;
    border: 1px solid rgba(255, 255, 255, 0.1);
  }
`;

// Custom components for markdown rendering with larger text
const MarkdownComponents = {
  p: (props: any) => <p className="mb-4 text-lg leading-relaxed" {...props} />,
  h1: (props: any) => <h1 className="text-3xl font-bold mb-6 text-white" {...props} />,
  h2: (props: any) => <h2 className="text-2xl font-bold mb-4 text-white" {...props} />,
  h3: (props: any) => <h3 className="text-xl font-bold mb-3 text-white" {...props} />,
  ul: (props: any) => <ul className="markdown-content-ul" {...props} />,
  ol: (props: any) => <ol className="markdown-content-ol" {...props} />,
  li: (props: any) => <li className="markdown-content-li" {...props} />,
  a: (props: any) => (
    <a
      className="text-primary hover:underline"
      target="_blank"
      rel="noopener noreferrer"
      {...props}
    />
  ),
  code: (props: any) => <code className="bg-base-200 px-1 py-0.5 rounded text-sm" {...props} />,
  pre: (props: any) => (
    <pre className="bg-base-200 p-4 rounded-lg mb-6 overflow-x-auto" {...props} />
  ),
  blockquote: (props: any) => (
    <blockquote className="border-l-4 border-primary pl-4 italic my-6" {...props} />
  ),
  hr: (props: any) => <hr className="my-8 border-gray-700" {...props} />,
  img: (props: any) => <img className="markdown-content-img" {...props} />,
};

// Returns true if version2 is newer than version1 using semver precedence (handles -rc.x correctly)
function isVersionNewer(version1: string, version2: string): boolean {
  return gt(version2, version1, { loose: true });
}

export default function ReleaseNotesModal({ onClose, filterVersion }: ReleaseNotesModalProps) {
  const [localReleaseNotes, setLocalReleaseNotes] = useState<ReleaseNote[]>([]);
  const [isLoading, setIsLoading] = useState<boolean>(true);

  // Access the global release notes context
  const { releaseNotes: globalReleaseNotes } = useContext(ReleaseNotesContext);

  // Update local state when global release notes change
  useEffect(() => {
    if (globalReleaseNotes.length > 0) {
      setLocalReleaseNotes(globalReleaseNotes);
      setIsLoading(false);
    } else {
      // If after 2 seconds we still don't have release notes, stop showing the loader
      const timer = setTimeout(() => {
        setIsLoading(false);
      }, 2000);

      return () => clearTimeout(timer);
    }
  }, [globalReleaseNotes]);

  // Format date from ISO string (e.g., "2025-02-26T23:37:33Z") to "26 February 2025"
  const formatDate = (isoDate: string): string => {
    try {
      const date = new Date(isoDate);
      return date.toLocaleDateString('en-GB', {
        day: 'numeric',
        month: 'long',
        year: 'numeric',
      });
    } catch (error) {
      console.error('Error formatting date:', error);
      return isoDate; // Return original string if parsing fails
    }
  };

  // Helper function to properly decode base64 strings with UTF-8 characters
  const decodeBase64 = (base64: string): string => {
    try {
      return decodeURIComponent(escape(atob(base64)));
    } catch (error) {
      console.error('Error decoding base64 markdown:', error);
      return 'Error decoding content';
    }
  };

  // Filter notes to only show those newer than the current app version if filterVersion is provided
  const filteredNotes = filterVersion
    ? localReleaseNotes.filter((note) => isVersionNewer(filterVersion, note.version))
    : localReleaseNotes;

  // Decode the base64 content for each release note
  const decodedReleaseNotes = filteredNotes;

  return (
    <>
      <style dangerouslySetInnerHTML={{ __html: markdownStyles }} />
      {/* Header */}
      <div className="modal-header pb-4 border-b border-gray-700">
        <h2 className="font-bold text-3xl mb-2 text-white">Release Notes</h2>
        <p className="text-gray-400 text-lg">
          {filterVersion
            ? `New updates since v${filterVersion}`
            : `You are running v${__APP_VERSION__}`}
        </p>
        <button
          className="btn btn-circle btn-ghost absolute right-4 top-4 text-2xl hover:bg-base-100/30"
          onClick={onClose}
        >
          ✕
        </button>
      </div>

      <div className="modal-body pt-4">
        {isLoading ? (
          <div className="flex flex-col items-center justify-center py-10">
            <div className="loading loading-spinner loading-lg text-primary"></div>
          </div>
        ) : decodedReleaseNotes.length === 0 ? (
          <div className="flex flex-col items-center justify-center py-10">
            <p className="text-gray-400 text-xl">
              {filterVersion
                ? 'You are up to date! No new release notes available.'
                : 'No release notes available.'}
            </p>
          </div>
        ) : (
          decodedReleaseNotes.map((note, index) => (
            <div key={index} className="mb-6 last:mb-0">
              {/* Version badge with date underneath */}
              <div>
                <div className="text-primary rounded-full font-bold text-2xl inline-block">
                  Version {note.version}
                </div>
                <div className="text-gray-400 text-lg">{formatDate(note.releaseDate)}</div>
              </div>

              {/* Markdown content with custom components for larger text */}
              <div className="text-gray-300 markdown-content text-lg">
                <Markdown options={{ overrides: MarkdownComponents }}>
                  {decodeBase64(note.base64Markdown)}
                </Markdown>
              </div>

              {/* Divider except for last item */}
              {index < decodedReleaseNotes.length - 1 && (
                <div className="border-t border-gray-700 mt-5"></div>
              )}
            </div>
          ))
        )}
      </div>
    </>
  );
}
