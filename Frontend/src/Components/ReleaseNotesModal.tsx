import { useContext, useEffect, useState } from 'react';
import { gt } from 'semver';
import Markdown from 'markdown-to-jsx';
import { ReleaseNote } from '../Models/WebSocketMessages';
import { ReleaseNotesContext } from '../App';
import { sendMessageToBackend } from '../Utils/MessageUtils';
import Button from './Button';

// CSS to ensure list bullets and images display properly
const listStyles = `
  .release-content ul {
    list-style-type: disc !important;
    padding-left: 1.5rem !important;
    margin-bottom: 1rem !important;
  }
  .release-content ul li {
    display: list-item !important;
    margin-bottom: 0.5rem !important;
  }
  .release-content ul li::marker {
    color: hsl(var(--p)) !important;
  }
  .release-content ol {
    list-style-type: decimal !important;
    padding-left: 1.5rem !important;
    margin-bottom: 1rem !important;
  }
  .release-content ol li {
    display: list-item !important;
    margin-bottom: 0.5rem !important;
  }
  .release-content img {
    border-radius: 0.5rem;
    max-width: 100%;
    margin-top: 1rem;
    margin-bottom: 1.5rem;
    border: 1px solid hsl(var(--b3));
  }
`;

interface ReleaseNotesModalProps {
  onClose: () => void;
  filterVersion: string | null;
}

const GITHUB_REPO_URL = 'https://github.com/Segergren/Segra';

function linkifyIssueReferences(text: string): string {
  return text.replace(/(?<!\[)#(\d+)/g, `[#$1](${GITHUB_REPO_URL}/issues/$1)`);
}

function decodeBase64(base64: string): string {
  try {
    return decodeURIComponent(escape(atob(base64)));
  } catch {
    return 'Error decoding content';
  }
}

function formatDate(isoDate: string): string {
  try {
    return new Date(isoDate).toLocaleDateString('en-US', {
      month: 'short',
      day: 'numeric',
      year: 'numeric',
    });
  } catch {
    return isoDate;
  }
}

const markdownOverrides = {
  p: (props: any) => <p className="mb-3 text-gray-300 leading-relaxed" {...props} />,
  h1: (props: any) => <h1 className="text-2xl font-bold mb-4 text-white" {...props} />,
  h2: (props: any) => <h2 className="text-xl font-bold mb-3 text-white" {...props} />,
  h3: (props: any) => <h3 className="text-lg font-semibold mb-2 text-white" {...props} />,
  ul: (props: any) => <ul {...props} />,
  ol: (props: any) => <ol {...props} />,
  li: (props: any) => <li className="text-gray-300" {...props} />,
  a: (props: any) => {
    const { href, children } = props;
    return (
      <a
        href={href}
        className="text-primary underline cursor-pointer hover:opacity-80"
        onClick={(e) => {
          e.preventDefault();
          if (href) {
            sendMessageToBackend('OpenInBrowser', { Url: href });
          }
        }}
      >
        {children}
      </a>
    );
  },
  strong: (props: any) => <strong className="text-white font-semibold" {...props} />,
  code: (props: any) => (
    <code className="bg-base-200 px-1.5 py-0.5 rounded text-sm text-primary" {...props} />
  ),
  pre: (props: any) => (
    <pre className="bg-base-200 p-3 rounded-lg mb-4 overflow-x-auto text-sm" {...props} />
  ),
  blockquote: (props: any) => (
    <blockquote className="border-l-4 border-primary pl-4 italic my-4 text-gray-400" {...props} />
  ),
  img: (props: any) => (
    <img className="rounded-lg max-w-full mt-4 mb-6 border border-base-400" {...props} />
  ),
};

export default function ReleaseNotesModal({ onClose, filterVersion }: ReleaseNotesModalProps) {
  const [notes, setNotes] = useState<ReleaseNote[]>([]);
  const [isLoading, setIsLoading] = useState(true);
  const { releaseNotes } = useContext(ReleaseNotesContext);

  useEffect(() => {
    if (releaseNotes.length > 0) {
      setNotes(releaseNotes);
      setIsLoading(false);
    } else {
      const timer = setTimeout(() => setIsLoading(false), 2000);
      return () => clearTimeout(timer);
    }
  }, [releaseNotes]);

  const filteredNotes = filterVersion
    ? notes.filter((note) => gt(note.version, filterVersion, { loose: true }))
    : notes;

  return (
    <>
      <style dangerouslySetInnerHTML={{ __html: listStyles }} />
      {/* Header */}
      <div className="pb-4 border-b border-base-400">
        <h2 className="font-bold text-2xl text-white">What's New</h2>
        {filterVersion && (
          <p className="text-gray-400 text-sm mt-1">Updates since v{filterVersion}</p>
        )}
        <Button variant="ghost" icon className="absolute right-4 top-4" onClick={onClose}>
          ✕
        </Button>
      </div>

      {/* Content */}
      <div className="pt-4 space-y-6">
        {isLoading ? (
          <div className="flex justify-center py-8">
            <span className="loading loading-spinner loading-lg text-primary" />
          </div>
        ) : filteredNotes.length === 0 ? (
          <p className="text-center text-gray-400 py-8">
            {filterVersion ? 'You are up to date!' : 'No release notes available.'}
          </p>
        ) : (
          filteredNotes.map((note, index) => (
            <div key={note.version}>
              {/* Version header */}
              <div className="flex items-center gap-3 mb-3">
                <span className="bg-primary/20 text-primary px-2 py-0.5 rounded text-sm font-medium">
                  v{note.version}
                </span>
                <span className="text-gray-500 text-sm">{formatDate(note.releaseDate)}</span>
              </div>

              {/* Content */}
              <div className="release-content">
                <Markdown options={{ overrides: markdownOverrides }}>
                  {linkifyIssueReferences(decodeBase64(note.base64Markdown))}
                </Markdown>
              </div>

              {/* Divider */}
              {index < filteredNotes.length - 1 && (
                <div className="border-t border-base-400 mt-6" />
              )}
            </div>
          ))
        )}
      </div>
    </>
  );
}
