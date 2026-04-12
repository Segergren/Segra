import { useContext, useEffect, useState } from 'react';
import { gt } from 'semver';
import Markdown from 'markdown-to-jsx';
import { CircleCheck, X, Calendar } from 'lucide-react';
import { GithubIcon } from './icons/BrandIcons';
import { ReleaseNote } from '../Models/WebSocketMessages';
import { ReleaseNotesContext } from '../App';
import { sendMessageToBackend } from '../Utils/MessageUtils';
import Button from './Button';

// Scoped styles for markdown content that cannot be expressed as overrides
const contentStyles = `
  .release-content {
    color: rgb(170, 176, 188);
    font-size: 0.95rem;
    line-height: 1.65;
  }
  .release-content ul {
    list-style: none !important;
    padding-left: 0 !important;
    margin-bottom: 1rem !important;
  }
  .release-content ul li {
    position: relative;
    padding-left: 1.25rem;
    margin-bottom: 0.5rem;
    display: list-item;
  }
  .release-content ul li::before {
    content: '';
    position: absolute;
    left: 6px;
    top: calc(0.825em - 3px);
    width: 6px;
    height: 6px;
    border-radius: 50%;
    background-color: var(--color-primary);
    opacity: 0.9;
  }
  .release-content ul ul {
    margin-top: 0.5rem !important;
    margin-bottom: 0.25rem !important;
  }
  .release-content ul ul li::before {
    background-color: var(--color-base-400);
  }
  .release-content ol {
    list-style: decimal !important;
    padding-left: 1.5rem !important;
    margin-bottom: 1rem !important;
  }
  .release-content ol li {
    display: list-item;
    margin-bottom: 0.5rem !important;
  }
  .release-content ol li::marker {
    color: var(--color-primary) !important;
    font-weight: 600;
  }
  .release-content img {
    border-radius: 0.5rem;
    max-width: 100%;
    margin-top: 1rem;
    margin-bottom: 1.5rem;
    border: 1px solid var(--color-base-400);
    box-shadow: 0 8px 24px rgba(0, 0, 0, 0.35);
  }
  .release-content hr {
    border: none;
    border-top: 1px solid var(--color-base-400);
    margin: 1.25rem 0;
    opacity: 0.55;
  }
  .release-content table {
    width: 100%;
    margin: 1rem 0;
    border-collapse: collapse;
    font-size: 0.875rem;
  }
  .release-content th,
  .release-content td {
    padding: 0.5rem 0.75rem;
    border: 1px solid var(--color-base-400);
    text-align: left;
  }
  .release-content th {
    background-color: rgba(255, 255, 255, 0.04);
    color: white;
    font-weight: 600;
  }
  .release-content strong,
  .release-content b {
    color: white;
    font-weight: 600;
  }
  .release-content > *:first-child {
    margin-top: 0;
  }
  .release-content > *:last-child {
    margin-bottom: 0;
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

function formatAbsoluteDate(isoDate: string): string {
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

function formatRelativeDate(isoDate: string): string | null {
  try {
    const then = new Date(isoDate).getTime();
    if (Number.isNaN(then)) return null;
    const diffSec = Math.max(0, Math.floor((Date.now() - then) / 1000));
    if (diffSec < 60) return 'just now';
    const diffMin = Math.floor(diffSec / 60);
    if (diffMin < 60) return `${diffMin}m ago`;
    const diffHours = Math.floor(diffMin / 60);
    if (diffHours < 24) return `${diffHours}h ago`;
    const diffDays = Math.floor(diffHours / 24);
    if (diffDays < 7) return `${diffDays}d ago`;
    const diffWeeks = Math.floor(diffDays / 7);
    if (diffWeeks < 5) return `${diffWeeks}w ago`;
    const diffMonths = Math.floor(diffDays / 30);
    if (diffMonths < 12) return `${diffMonths}mo ago`;
    const diffYears = Math.floor(diffDays / 365);
    return `${diffYears}y ago`;
  } catch {
    return null;
  }
}

function getReleaseChannel(version: string): { text: string; className: string } | null {
  if (version.includes('-beta')) {
    return {
      text: 'Beta',
      className: 'bg-purple-500/15 text-purple-300 border-purple-500/30',
    };
  }
  if (version.includes('-rc')) {
    return {
      text: 'RC',
      className: 'bg-blue-500/15 text-blue-300 border-blue-500/30',
    };
  }
  return null;
}

const markdownOverrides = {
  p: (props: any) => <p className="mb-3 leading-relaxed" {...props} />,
  h1: (props: any) => <h1 className="text-xl font-bold mt-5 mb-3 text-white" {...props} />,
  h2: (props: any) => (
    <h2
      className="text-lg font-bold mt-5 mb-2.5 text-white pb-1.5 border-b border-base-400/60"
      {...props}
    />
  ),
  h3: (props: any) => <h3 className="text-base font-semibold mt-4 mb-2 text-white" {...props} />,
  h4: (props: any) => (
    <h4 className="text-sm font-semibold mt-3 mb-2 text-white uppercase tracking-wide" {...props} />
  ),
  a: (props: any) => {
    const { href, children } = props;
    return (
      <a
        href={href}
        className="text-primary underline decoration-primary/30 underline-offset-2 hover:decoration-primary cursor-pointer transition-colors"
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
  em: (props: any) => <em className="text-gray-300" {...props} />,
  code: (props: any) => (
    <code
      className="bg-base-200 px-1.5 py-0.5 rounded text-[0.85em] text-primary border border-base-400/40"
      {...props}
    />
  ),
  pre: (props: any) => (
    <pre
      className="bg-base-200/80 border border-base-400/40 p-3 rounded-lg mb-4 overflow-x-auto text-sm"
      {...props}
    />
  ),
  blockquote: (props: any) => (
    <blockquote
      className="border-l-4 border-primary/60 pl-4 italic my-4 text-gray-400 bg-white/[0.02] py-2 rounded-r-md"
      {...props}
    />
  ),
  table: (props: any) => (
    <div className="overflow-x-auto my-4">
      <table {...props} />
    </div>
  ),
};

function ReleaseSkeleton() {
  return (
    <div className="space-y-8 animate-pulse">
      {Array.from({ length: 2 }).map((_, i) => (
        <div key={i}>
          <div className="flex items-center gap-2 mb-4">
            <div className="h-6 w-16 rounded-md bg-white/5" />
            <div className="h-6 w-14 rounded-md bg-white/5" />
            <div className="h-4 w-28 rounded bg-white/5 ml-auto" />
          </div>
          <div className="space-y-2.5">
            <div className="h-4 rounded bg-white/5 w-11/12" />
            <div className="h-4 rounded bg-white/5 w-9/12" />
            <div className="h-4 rounded bg-white/5 w-10/12" />
            <div className="h-4 rounded bg-white/5 w-7/12" />
          </div>
        </div>
      ))}
    </div>
  );
}

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

  const handleOpenGithubReleases = () => {
    sendMessageToBackend('OpenInBrowser', {
      Url: `${GITHUB_REPO_URL}/releases`,
    });
  };

  return (
    <>
      <style dangerouslySetInnerHTML={{ __html: contentStyles }} />

      {/* Header */}
      <div className="flex items-start justify-between gap-4 pb-4 border-b border-base-400">
        <div className="min-w-0">
          <h2 className="font-bold text-2xl text-white leading-tight">What's New</h2>
          <p className="text-sm text-gray-400 mt-0.5">
            {filterVersion
              ? `Updates since v${filterVersion}`
              : 'Browse recent releases from Segra'}
          </p>
        </div>
        <Button variant="ghost" icon size="sm" onClick={onClose} aria-label="Close">
          <X size={18} />
        </Button>
      </div>

      {/* Body */}
      <div className="pt-5 space-y-8">
        {isLoading ? (
          <ReleaseSkeleton />
        ) : filteredNotes.length === 0 ? (
          <div className="flex flex-col items-center justify-center py-14 text-center">
            <div className="flex items-center justify-center w-14 h-14 rounded-full bg-success/15 text-success border border-success/25 mb-4">
              <CircleCheck size={30} />
            </div>
            <h3 className="text-lg font-semibold text-white mb-1">
              {filterVersion ? "You're up to date!" : 'No release notes available'}
            </h3>
            <p className="text-sm text-gray-400 max-w-sm">
              {filterVersion
                ? 'You are on the latest version of Segra. Check back after the next release for new changes.'
                : 'We could not find any release notes to display right now.'}
            </p>
          </div>
        ) : (
          filteredNotes.map((note, index) => {
            const channel = getReleaseChannel(note.version);
            const isLatest = index === 0;
            const relative = formatRelativeDate(note.releaseDate);
            const absolute = formatAbsoluteDate(note.releaseDate);
            return (
              <article key={note.version} className="relative">
                {/* Version header */}
                <div className="flex items-center gap-2 mb-4 flex-wrap">
                  <span className="inline-flex items-center h-6 bg-primary/15 text-primary px-2.5 rounded-md text-xs font-semibold border border-primary/25 leading-none">
                    v{note.version}
                  </span>
                  {isLatest && (
                    <span className="inline-flex items-center h-6 bg-success/15 text-success px-2 rounded-md text-[11px] font-medium tracking-wide uppercase border border-success/25 leading-none">
                      Latest
                    </span>
                  )}
                  {channel && (
                    <span
                      className={`inline-flex items-center h-6 px-2 rounded-md text-[11px] font-medium tracking-wide uppercase border leading-none ${channel.className}`}
                    >
                      {channel.text}
                    </span>
                  )}
                  <span
                    className="ml-auto inline-flex items-center gap-1.5 text-xs text-gray-500"
                    title={absolute}
                  >
                    <Calendar size={12} />
                    {absolute}
                    {relative && <span className="text-gray-600">· {relative}</span>}
                  </span>
                </div>

                {/* Content */}
                <div className="release-content">
                  <Markdown options={{ overrides: markdownOverrides }}>
                    {linkifyIssueReferences(decodeBase64(note.base64Markdown))}
                  </Markdown>
                </div>

                {/* Divider */}
                {index < filteredNotes.length - 1 && (
                  <div className="mt-8 h-px bg-gradient-to-r from-transparent via-base-400 to-transparent" />
                )}
              </article>
            );
          })
        )}
      </div>

      {/* Footer */}
      {!isLoading && filteredNotes.length > 0 && (
        <div className="mt-6 pt-4 border-t border-base-400 flex items-center justify-between gap-3">
          <span className="text-xs text-gray-500">
            Showing {filteredNotes.length} release
            {filteredNotes.length === 1 ? '' : 's'}
          </span>
          <Button variant="primary" size="sm" onClick={handleOpenGithubReleases}>
            <GithubIcon size={14} />
            View on GitHub
          </Button>
        </div>
      )}
    </>
  );
}
