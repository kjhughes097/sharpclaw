import { useId } from 'react';
import type { AgentDto } from '../../api/types';
import { RelativeTime } from '../../components/ui';

/**
 * The skills the agent reported loading at startup (skills spec section 10). Read-only: skills
 * live in the install's shared `skills/` folder and change only when the agent restarts.
 * Descriptions are operator-written text, rendered as plain text, never Markdown or HTML.
 */
export function SkillsSection({ agent }: { agent: AgentDto }) {
  const id = useId();
  const reported = agent.skillsReportedAt ?? null;

  return (
    <section
      aria-labelledby={`${id}-title`}
      className="space-y-3 rounded-lg border border-border bg-surface p-4"
    >
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 id={`${id}-title`} className="font-semibold">
          Skills
        </h2>
        {reported && (
          <span className="text-sm text-muted">
            Reported <RelativeTime iso={reported} />
          </span>
        )}
      </div>
      {reported === null ? (
        <p className="text-sm text-muted">
          No skills reported. An agent reports its skills when it starts with{' '}
          <code className="font-mono">skill_read</code> in its agent.json.
        </p>
      ) : agent.skills.length === 0 ? (
        <p className="text-sm text-muted">No skills loaded.</p>
      ) : (
        <ul className="space-y-2 text-sm">
          {agent.skills.map((skill) => (
            <li key={skill.name}>
              <span className="font-mono font-medium">{skill.name}</span>
              <p className="text-muted break-words">{skill.description}</p>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
