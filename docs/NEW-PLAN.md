# SharpClaw - Rewrite Plan

## Project

We are going to rewrite SharpClaw from the ground up.
We'll keep a log of all changes we make, features we build, so we will need a `docs` folder, at the top level, from the very start.
Each feature / functionality should have it's own markdown file (named `{feature}.md`) outlining when it was added, what it is (purpose) and any decisions we made.
We will also keep an index of all these feature documents in `index.md` - which will have, per row, {feature name} : {document link}
All existing source and files should be moved to a directory named `old`

## Requirements

These are a rough outline of desired functionality. They are not exhaustive.

### General

- We will use the Github Copilot C# SDK to make the actual LLM calls, manage context, and provide built in tools
- This will be a C# .NET10 project named SharpClaw.
- Configuration will be via appsettings.json in production and appsettings.Development.json for development running
- Logging should be to a local running docker instance of the Grafana stack and to a simple console logger with the format `{date-time} : {log level} : {message}` no other fields
- We will need a docker-compose.yml file to start up the local Grafana stack

### Features - Agents

- Agent definitions should be in a `agents` folder and be markdown files named as `{agent-name}.agent.md`
- Agent definitions will have front matter that defines their name, a brief bio/description, their skills/mcps/tools and other agents they interact with
- Agent files should be loaded into an agentregistry on startup, and when an 'AgentReload' function is called
- When an agent is invoked, it should be loaded into an AgentInstance with it's properties from frontmatter, and definition
- The agent being invoked causes it to make a call via the github copilot SDK

### Features - MCPs

- MCPs should be in a folder named `mcps` and be json format.
- MCPs should be loaded into an MCP registry on startup and when a ReloadMcps function is called
- MCPs that an agent has are defined in the agent markdown definition as a one liner (that gets added to the prompt).

### Features - Skills

- Skills should be in a folder named `skills` and be markdown format with filenames `{skill name}.skill.md`. Skills may have associated scripts or files, any of those would be named '{tool-name}-{script-or-file-name}.{extension}` or the like
- Skills should be loaded into a Skills registry on startup and when a ReloadSkills function is called
- Skills that an agent has are defined in the agent markdown definition as a one liner (that gets added to the prompt).

### Feature - Interactions

- We should allow interactions with agents via a queue/channel system - requests will come in from various front ends (initially web and telegram) and responses streamed back out over the channel/queue.
- For the telegram bot, we should only allow messages from specific user - identified by username e.g. `kjhughes097`
- Telegram interaction are via a telegram bot. The bot should differentiate groups that it's messages are coming from so that I can start a group with myself and the bot and set the agent, then that group chat is for that agent, while another group could be for another agent.
- The request should have, at a minimum, an agent identifier, and a session identifier, so we know how to route it, and what the current context is.
- When a request is made and the agent is waiting on a response from the LLM/SDK it would send regular indications back to the queue/channel so the client knows that it's still processing. For example making the 'typing' notification come up in telegram, or responding with interim 'Thinking...' messages to the web client
- When a client connects it should get the last 5 (make it configurable) turns and display them.
- When processing a message from a channel that has no agent set, if the message is `.{char}` then the agent with that `{char}` (lowercase) as the first letter of it's name should be set as the active agent for that channel/chat. This should not make a LLM/SDK call
- When processing a message if the message is `hi` or `ping` then we should reply with the agent name, and their definition. If no agent is set yet, then just reply with 'No agent set yet'. This should not make a LLM/SDK call.
- There may be other 'commands' that we want to action (without the agent interpretting them via the LLM/SDK call) - so make this extensible

### Feature - Workspace

- The user will be able to define an absolute path (via appsettings) as a workspace root
- Each agent will have their own directory under that `{workspace root}` named `{agent name}` where it will store files and data that it needs.
- There will be a directory name `projects` under `{workspace root}` that all agents will have access to, in case there are projects that require multiple agents
- In `projects` there will be a new directory created for each project we start - agents will need to have a sill/tool to create a directory for a new project.
- There will be a directory name `knowledge` under `{workspace root}` that all agents will have access to, this will store cross agent / global knowledge (see memory')
- When a new fact comes to light, during interactions, that might be of use globally, to all agents, then it will be stored here (see 'memory')

### Feature - Auditing

- Every request and response, tool call, mcp call should be audited to an append only log file (not Grafan/console logging) for that agent, so we can audit what happened.
- The audit file lives in the `{workspace root}/{agent name}` directory and is named `audit.md`. It is **append only** and is **NEVER** deleted

### Feature - Memory

- Each agent will have its own: context, memory, and long-term-memory and audit
- Context is the working context for the session
- Memory `memory.md` is on ongoing summary of the session, that spans multiple contexts/compactions. The agent can access `memory.md` if it's not clear on something in it's current context, or needs to know more details on what happened before the last compaction
- Long Term Memory `memory-{YY-DMM-DD}.md` is a ongoing journal/index of the memory files, that the agent could refer to if required. The list of long term memory files is also indexed and tags associated with each file.
- When `memory.md` is snapped shotted at the end of the day into `memory-{YY-MM-DD}.md` the name of the newly created file and a list of 5 - 20 'tags' that indicate what happened that day should be added to a `memory-index.md` file
- If an agent needs to 'look back' at prior work then it can look for corrosponding tags in the `memory-index.md` file to find which long term memory files contain the content and then load those files for the details.
