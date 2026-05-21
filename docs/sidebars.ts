import type { SidebarsConfig } from '@docusaurus/plugin-content-docs';

const sidebars: SidebarsConfig = {
  docs: [
    'intro',
    {
      type: 'category',
      label: 'Features',
      collapsed: false,
      items: [
        'features/scaffolding',
        'features/agents',
        'features/mcps',
        'features/skills',
        'features/execution',
        'features/commands',
        'features/interactions',
        'features/scheduling',
        'features/telegram',
        'features/web-ui',
        'features/workspace',
        'features/memory',
        'features/auditing',
        'features/logging',
      ],
    },
  ],
};

export default sidebars;
