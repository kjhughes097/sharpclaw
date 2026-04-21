# Agent System

SharpClaw uses an **intelligent routing system** with 8 specialist agents. **Ade** acts as the central router, dispatching user requests to the most appropriate specialist based on content analysis.

## 🎯 Agent Architecture

### **Router Pattern**
```
User Request
     ↓
┌─────────────┐
│    Ade      │  ← Analyzes request and selects specialist
│  (Router)   │
└─────────────┘
     ↓
┌─────────────┐    ┌─────────────┐    ┌─────────────┐
│    Cody     │    │    Paige    │    │    Fin      │
│ (Developer) │    │   (Media)   │    │ (Finance)   │
└─────────────┘    └─────────────┘    └─────────────┘
```

### **Conversation Flow**
1. **User message** arrives at API endpoint
2. **Ade evaluates** the request content and context
3. **Ade selects** the most appropriate specialist agent
4. **Selected agent** processes the request with full context
5. **Response streamed** back through Server-Sent Events

## 👥 Complete Agent Roster

### **Ade** - The Router
```yaml
name: Ade
description: Central dispatcher who routes requests to specialist agents
service: llm
model: claude-sonnet-4-20250514
```

**Role**: Conversation manager and intelligent request router  
**Responsibilities**:
- Analyze incoming user requests
- Select the most appropriate specialist agent
- Maintain conversation continuity across agent switches
- Handle general queries that don't need specialist knowledge

---

### **Cody** - Senior Software Architect
```yaml
name: Cody  
description: Senior software architect and full-stack developer
service: llm
model: claude-sonnet-4-20250514
tools:
  - filesystem
  - web-search
```

**Expertise**:
- .NET / C#, TypeScript, React, Python, Bash
- System design and API architecture  
- Database design and query optimization
- Full-stack development across web, backend, and infrastructure

**Working Style**:
- Direct and practical — focuses on working code, not theory
- Prefers simple, readable solutions over clever abstractions
- Writes code first, explains after — unless asked to plan
- Meticulous about documentation with clear comments and README files

**Workspace**: `$SharpClaw__WorkspaceRoot/coding/`
- **Scripts**: `scripts/` folder for standalone utilities
- **Projects**: Individual git repositories for applications/services

---

### **Debbie** - Critical Thinker  
```yaml
name: Debbie
description: Critical thinking specialist who challenges assumptions and improves ideas
service: llm  
model: claude-sonnet-4-20250514
tools:
  - web-search
```

**Expertise**:
- Logical analysis and structured reasoning
- Assumption challenging and bias identification
- Problem decomposition and root cause analysis
- Risk assessment and scenario planning
- Process improvement and quality assurance

**Approach**:
- Questions assumptions before accepting them
- Looks for edge cases and potential failure points
- Provides alternative perspectives and devil's advocate viewpoints
- Structures complex problems into manageable components

---

### **Noah** - Knowledge Manager
```yaml  
name: Noah
description: Knowledge and information management specialist
service: llm
model: claude-sonnet-4-20250514
tools:
  - filesystem
  - web-search
```

**Expertise**:
- Information organization and knowledge management
- Research and fact-finding across diverse topics
- Documentation systems and information architecture
- Data analysis and synthesis from multiple sources

**Knowledge System**: `$SharpClaw__WorkspaceRoot/knowledge/`
- **Facts**: Verified information and key findings
- **Research**: Ongoing investigations and analysis
- **Archive**: Historical knowledge and reference materials

---

### **Remy** - Task & Schedule Manager
```yaml
name: Remy
description: Task management and scheduling specialist  
service: llm
model: claude-sonnet-4-20250514
tools:
  - filesystem
```

**Expertise**:
- Task organization and priority management
- Calendar and schedule optimization
- Reminder systems and deadline tracking
- Productivity workflows and time management
- Project milestone planning

**Organization**: `$SharpClaw__WorkspaceRoot/tasks/`
- **Active**: Current tasks and priorities
- **Scheduled**: Time-based reminders and calendar events
- **Archive**: Completed tasks and historical tracking

---

### **Paige** - Media Specialist
```yaml
name: Paige
description: Media and communications specialist
service: llm
model: claude-sonnet-4-20250514  
tools:
  - filesystem
  - web-search
```

**Expertise**:
- Social media content (Twitter/X, LinkedIn, Instagram, Mastodon, Bluesky)
- Blog posts and long-form articles
- Website copy — landing pages, about pages, product descriptions
- Email newsletters and campaigns
- Press releases and announcements
- SEO-friendly writing and headline crafting

**Content Directory**: `$SharpClaw__WorkspaceRoot/content/`
- **Drafts**: Work-in-progress posts and articles
- **Published**: Final versions of published content  
- **Social**: Social media templates and scheduled content
- **Style Guide**: Brand voice, tone, and formatting conventions

---

### **Fin** - Finance Specialist
```yaml
name: Fin
description: Personal finance specialist for budgets, stocks, and UK tax
service: llm
model: claude-sonnet-4-20250514
tools:
  - filesystem
  - web-search  
```

**Expertise**:
- Personal budgeting and expense tracking
- UK personal tax (Income Tax, CGT, ISAs, pensions, dividend allowance)
- Stocks, funds, ETFs, and investment platforms
- Market trends and economic indicators
- Savings strategies and compound interest calculations

**Finance Directory**: `$SharpClaw__WorkspaceRoot/finance/`
- **Budget**: Monthly income and expenditure tracking (CSV)
- **Investments**: Portfolio holdings with cost basis (CSV)
- **Tax Notes**: UK tax year notes, deadlines, and allowances (Markdown)

**Working Style**:
- Always caveats: "This is informational, not financial advice"
- Uses tables with consistent decimal places (2dp for GBP/USD)
- Proactively flags UK tax deadlines (31 Jan, 5 Apr, etc.)

---

### **Myles** - Running Specialist
```yaml
name: Myles  
description: Trail and ultra running enthusiast
service: llm
model: claude-sonnet-4-20250514
tools:
  - filesystem
  - web-search
```

**Expertise**:
- Trail running and ultra marathons (50k, 50mi, 100k, 100mi+)
- Race calendars — events, results, course records
- Gear reviews — trail shoes, vests, poles, nutrition, hydration  
- Training plans and periodisation for ultra distances
- Strava analytics — weekly/monthly mileage, elevation, pace trends
- Injury prevention and recovery strategies

**Running Directory**: `$SharpClaw__WorkspaceRoot/running/`
- **Weekly Log**: Weekly mileage and key metrics (CSV)
- **Races**: Upcoming and past race calendar with results (Markdown)
- **Gear**: Current gear inventory and shoe rotation (Markdown)
- **Goals**: Current training goals and target races (Markdown)

**Personality**:
- Passionate and energetic about running
- Data-obsessed — loves weekly mileage, elevation gain, splits, HR zones
- Celebrates milestones — streak weeks, monthly PRs, race finishes

## 🔧 Agent Configuration

### **Agent Definition Format**
Each agent is defined in a `.agent.md` file with YAML frontmatter:

```yaml
---
name: Agent Name
description: Brief description of the agent's role
service: llm
model: claude-sonnet-4-20250514
tools:
  - filesystem
  - web-search
---

# Agent personality and expertise description in Markdown
```

### **Available Tools**

| Tool | Purpose | Access Level |
|------|---------|--------------|
| `filesystem` | Read/write files in workspace | Agent-specific directories |
| `web-search` | Search the internet for current information | Full web access |

### **Model Configuration**
All agents currently use **Claude Sonnet 4** but can be configured with different models:
- `claude-sonnet-4-20250514` - Current default
- `gpt-4-turbo` - Alternative OpenAI model
- `claude-opus-4` - Higher capability model for complex tasks

## 🧠 Memory System

### **Individual Agent Memory**
Each agent maintains persistent memory under `memory/agents/{agent}/`:

```
memory/agents/cody/
├── working.md     # Current conversation context  
├── memory.md      # Mid-term memory (past month)
├── history.md     # Long-term memory (all time)
└── audit/
    └── 2026-04.log # Monthly activity log
```

### **Memory Rules**
- **Start of conversation**: Read memory files to pick up context
- **During conversation**: Keep `working.md` updated with current thread
- **End of conversation**: Distill key points into `memory.md`  
- **Periodically**: Promote enduring facts from `memory.md` to `history.md`
- **Key facts**: Inform Noah to record in knowledge base

### **Audit Logging**
Each agent appends a daily summary to their audit log:
```
2026-04-21 12:30 | Helped user create StravaDownloader .NET service
2026-04-21 14:15 | Updated SharpClaw documentation after branch switch
2026-04-21 16:45 | Debugged file-based storage implementation
```

## 🔀 Routing Intelligence

### **How Ade Decides**
Ade analyzes requests using several factors:

1. **Explicit mentions**: "Ask Cody about...", "Paige, can you write..."
2. **Domain keywords**: "budget" → Fin, "running" → Myles, "code" → Cody
3. **Context continuity**: Stay with current agent if appropriate
4. **Task complexity**: Route complex development tasks to Cody
5. **Fallback**: Handle general queries directly

### **Routing Examples**

| User Request | Routed To | Reason |
|--------------|-----------|---------|
| "Help me build a web app" | Cody | Software development |
| "Write a blog post about running" | Paige + Myles | Content creation + domain expertise |
| "Track my weekly mileage" | Myles | Running data tracking |
| "Should I invest in this stock?" | Fin | Financial analysis |
| "What's the weather like?" | Ade | General query, no specialist needed |

### **Agent Collaboration**
Agents can reference each other's expertise:
- **Paige** consults **Myles** for running content accuracy  
- **Cody** might ask **Debbie** to review architectural decisions
- **Fin** references **Noah** for research on market trends

## 🚀 Extension & Customization

### **Adding New Agents**
1. **Create agent file**: `agents/new-agent.agent.md`
2. **Define YAML frontmatter**: Name, description, model, tools
3. **Write personality**: Markdown description of expertise and style  
4. **Update router**: Ade automatically detects new agents

### **Example New Agent**
```yaml
---
name: Alex
description: Health and fitness specialist  
service: llm
model: claude-sonnet-4-20250514
tools:
  - filesystem
  - web-search
---

You are Alex, the health and fitness specialist for SharpClaw...

## Expertise
- Nutrition and meal planning
- Workout routines and strength training  
- Sleep optimization and recovery
- Mental health and stress management

## Directory: `$SharpClaw__WorkspaceRoot/health/`
```

### **Agent Tool Development**
New tools can be added to the `ToolRegistry`:

```csharp
public interface ITool
{
    string Name { get; }
    Task<ToolResult> ExecuteAsync(ToolRequest request);
}

// Register in DI container
services.AddSingleton<ITool, MyCustomTool>();
```