import type { ReactNode } from 'react';
import { Outlet, useLocation, useMatch } from 'react-router';
import { useSystem } from '../api/hooks';
import { ConnectionIndicator, UserMenu } from './ShellParts';
import { TabBar, tabLabel } from './TabBar';
import { Badge } from './ui';

/*
 * The LCARS frame (after thelcars.com's classic template): a left column of stacked colour blocks
 * that bends ("elbows") into horizontal bars, twice: the top panel holds the banner, status, user
 * menu and the tab pills; the bottom panel holds the page. Sizes come from the `--lcars-*`
 * properties on `.lcars-frame` in styles/index.css, which grow with the viewport.
 *
 * Everything that is only decoration (block codes, bar segments, the number cascade) is
 * aria-hidden; the landmarks and controls are the same as the default shell's.
 */

// Decorative "data cascade" numbers, like the template's. Static (and fixed), so nothing re-renders.
const CASCADE = [
  ['93', '1853', '24109', '7302', '0223', '688'],
  ['21509', '68417', '80', '51166', '91007', '4'],
  ['585101', '25403', '31219', '633', '406822', '81205'],
  ['2107853', '12201972', '24487255', '12073', '688', '21982'],
  ['33', '56', '04', '21604', '15421', '25'],
  ['72112', '101088', '604122', '272448', '29620', '339048'],
];

/** "Overview", "Prompts", the agent's name… for the banner. */
function useSectionName(): string {
  const agent = useMatch('/agents/:name')?.params.name;
  const first = useLocation().pathname.split('/')[1] ?? '';
  if (agent) return tabLabel(agent);
  return first ? tabLabel(first) : 'Home';
}

/** A sidebar block: a colour panel with its (decorative) code in the bottom-right corner. */
function Block({ className, code }: { className: string; code?: string }) {
  return (
    <div
      aria-hidden="true"
      className={`flex shrink-0 items-end justify-end px-1.5 pb-1 text-xs text-accent-fg md:px-3 md:text-base ${className}`}
    >
      {code && (
        <>
          <span className="md:hidden">{code.slice(0, 2)}</span>
          <span className="hidden md:inline">{code}</span>
        </>
      )}
    </div>
  );
}

/** A horizontal bar made of coloured segments with black gaps, ending the elbow. */
function Bar({ segments }: { segments: string[] }) {
  return (
    <div aria-hidden="true" className="flex h-(--lcars-bar) shrink-0 gap-1">
      {segments.map((segment, i) => (
        <div key={i} className={segment} />
      ))}
    </div>
  );
}

export function LcarsShell({ children }: { children?: ReactNode }) {
  const system = useSystem();
  const section = useSectionName();

  return (
    <div className="lcars-frame app-height relative flex flex-col overflow-hidden bg-bg p-1.5 md:p-2.5">
      <a
        href="#main"
        className="sr-only focus:not-sr-only focus:absolute focus:top-2 focus:left-2 focus:z-50 focus:rounded focus:bg-surface focus:px-3 focus:py-2"
      >
        Skip to content
      </a>

      {/* Top panel: the left column elbows down-and-right into the bar under the tabs. */}
      <div className="flex shrink-0">
        <div className="flex w-(--lcars-side) shrink-0 flex-col gap-1">
          <div className="flex min-h-12 items-end justify-end bg-lcars-violet px-1.5 pb-1 text-accent-fg md:min-h-20 md:px-3">
            <span className="text-sm font-bold uppercase md:text-xl">
              <span className="md:hidden">SC</span>
              <span className="hidden md:inline">SharpClaw</span>
            </span>
          </div>
          <Block
            className="min-h-(--lcars-outer) flex-1 rounded-bl-(--lcars-outer) bg-lcars-blue"
            code="02-262000"
          />
        </div>
        <div className="flex min-w-0 flex-1 flex-col">
          {/* The blue behind shows only through the black panel's rounded corner: the inner elbow. */}
          <div className="flex-1 bg-lcars-blue">
            <header className="flex h-full min-w-0 flex-col gap-1 rounded-bl-(--lcars-inner) bg-bg pb-2 pl-3 md:gap-2 md:pl-6">
              <div className="flex items-start justify-end gap-6">
                <div
                  aria-hidden="true"
                  className="mt-2 mr-auto hidden grid-cols-6 gap-x-4 text-right text-xs leading-tight text-accent xl:grid"
                >
                  {CASCADE.flatMap((column, c) =>
                    column.slice(0, 3).map((n, r) => (
                      <span key={`${c}-${r}`} className="lcars-cascade">
                        {n}
                      </span>
                    )),
                  )}
                </div>
                <span className="truncate text-right text-3xl leading-none text-accent uppercase md:text-6xl">
                  SharpClaw / {section}
                </span>
              </div>
              <div className="flex min-w-0 items-center justify-end gap-2 text-sm text-fg uppercase md:gap-3">
                <ConnectionIndicator />
                {system.data && <span className="hidden sm:inline">· {system.data.version}</span>}
                {system.data?.provider === 'Fake' && <Badge tone="warn">Fake provider</Badge>}
                <UserMenu />
              </div>
              <TabBar />
            </header>
          </div>
          <Bar
            segments={[
              'flex-[6] bg-lcars-blue',
              'w-6 bg-accent md:w-10',
              'flex-[3] bg-lcars-violet',
              'flex-[5] bg-lcars-violet',
              'w-6 bg-danger md:w-12',
            ]}
          />
        </div>
      </div>

      {/* Bottom panel: the bar over the page elbows down into the left column of blocks. */}
      <div className="mt-1.5 flex min-h-0 flex-1 md:mt-2">
        <div className="flex w-(--lcars-side) shrink-0 flex-col gap-1">
          <Block className="min-h-(--lcars-outer) rounded-tl-(--lcars-outer) bg-danger" code="03-111968" />
          <Block className="flex-[3] bg-danger" code="04-041969" />
          <Block className="flex-1 bg-accent" code="05-1701D" />
          <Block className="flex-[2] bg-lcars-peach" code="06-071984" />
          <Block className="flex-[4] bg-lcars-almond" />
        </div>
        <div className="flex min-w-0 flex-1 flex-col">
          <Bar
            segments={[
              'flex-[6] bg-danger',
              'w-6 bg-lcars-peach md:w-10',
              'flex-[2] bg-danger',
              'flex-[5] bg-lcars-violet',
              'w-6 bg-lcars-peach md:w-12',
            ]}
          />
          <div className="flex min-h-0 flex-1 bg-danger">
            <main
              id="main"
              tabIndex={-1}
              className="flex min-h-0 min-w-0 flex-1 flex-col overflow-hidden rounded-tl-(--lcars-inner) bg-bg pt-2 pl-2 outline-none md:pl-4"
            >
              {children ?? <Outlet />}
            </main>
          </div>
        </div>
      </div>
    </div>
  );
}
