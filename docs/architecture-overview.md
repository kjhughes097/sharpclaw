# Architecture Overview

SharpClaw is a **file-based personal agent framework** built on .NET 10 with React frontend, designed for simplicity and extensibility.

## 🏗️ Core Architecture

### **System Components**

```
┌─────────────────────────────────────────────────────────────┐
│                    SharpClaw Framework                      │
├─────────────────────────────────────────────────────────────┤
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐ │
│  │   Web UI    │  │  REST API   │  │    Agent System     │ │
│  │   React     │◄─┤   .NET 10   │◄─┤  Router + 7 Agents  │ │
│  │   SPA       │  │   Minimal   │  │   (Ade dispatches)  │ │
│  └─────────────┘  └─────────────┘  └─────────────────────┘ │
│                          │                      │           │
│                          ▼                      ▼           │
│  ┌─────────────────────────────────┐  ┌─────────────────┐   │
│  │      File Storage System        │  │  LLM Backends   │   │
│  │    Projects/Chats/Messages      │  │  Anthropic      │   │
│  │    Markdown + JSON Files        │  │  OpenAI         │   │
│  └─────────────────────────────────┘  │  OpenRouter     │   │
│                                       │  GitHub Copilot │   │
│                                       └─────────────────┘   │
└─────────────────────────────────────────────────────────────┘
```

## 📁 File-Based Storage

**No database required** - everything is stored as files for simplicity and portability.

### **Storage Strategy**
- **Projects** contain **chats** which contain **messages**
- **Hierarchical organization** with clear folder structure
- **Human-readable formats**: Markdown for context, JSON for structured data
- **Git-friendly**: All data can be version controlled

### **Storage Layout**
```
workspace-root/
├── projects/                    # All user projects
│   ├── general/                 # Default project
│   │   ├── context.md          # Project description
│   │   ├── log.md              # Project activity log
│   │   └── chats/              # All conversations
│   │       └── 20260421-120000-my-chat/
│   │           ├── messages.json    # Conversation history
│   │           ├── context.md      # Chat context/summary
│   │           ├── log.md         # Chat event log
│   │           └── usage.json     # Token usage tracking
│   └── [other-projects]/
├── memory/                     # Agent memory files
│   └── agents/
│       ├── cody/
│       │   ├── working.md      # Current context
│       │   ├── memory.md       # Mid-term memory
│       │   └── history.md      # Long-term memory
│       ├── paige/ (media)
│       ├── fin/ (finance)
│       └── ...
└── content/                    # Content management (Paige)
    ├── drafts/
    ├── published/
    └── social/
```

## 🤖 Agent System

### **Agent Router (Ade)**
- **Central dispatcher** that routes user requests to specialist agents
- **Context awareness** - understands which agent is best for each task
- **Conversation continuity** - maintains thread context across agent switches

### **8 Specialist Agents**

| Agent | Role | Domain |
|-------|------|--------|
| **Ade** | Router | Request routing and conversation management |
| **Cody** | Developer | Software architecture, coding, debugging |
| **Debbie** | Critical Thinking | Analysis, critique, problem decomposition |
| **Noah** | Knowledge | Information management, facts, research |
| **Remy** | Reminders | Task management, scheduling, notifications |
| **Paige** | Media | Social media, blog posts, brand messaging |
| **Fin** | Finance | Budgeting, UK tax, investments, market trends |
| **Myles** | Running | Trail/ultra running, Strava, gear, training |

### **Agent Lifecycle**
1. **User message** arrives at API
2. **Ade evaluates** the request and selects appropriate agent
3. **Selected agent** processes request with access to:
   - Conversation history
   - Agent-specific memory files
   - Workspace files (via secure file system tools)
   - Web search capabilities
4. **Response generated** and streamed back via Server-Sent Events

## 🔗 LLM Backend Abstraction

### **Multi-Provider Support**
SharpClaw abstracts LLM providers through a unified `ILlmService` interface:

- **Anthropic Claude** - Primary backend for most agents
- **OpenAI GPT** - Alternative backend option
- **OpenRouter** - Access to multiple models through single API
- **GitHub Copilot** - Code-focused interactions

### **Provider Configuration**
Each agent can specify:
```yaml
service: llm
model: claude-sonnet-4-20250514
tools:
  - filesystem
  - web-search
```

## 🌐 API Layer

### **ASP.NET Core Minimal API**
- **RESTful endpoints** for projects, chats, messages
- **Server-Sent Events** for real-time streaming
- **File upload/download** for workspace integration
- **CORS enabled** for React SPA integration

### **Key Endpoints**
```
GET    /projects              # List all projects
POST   /projects              # Create new project
GET    /projects/{slug}/chats # List chats in project
POST   /projects/{slug}/chats # Create new chat
GET    /chats/{id}/messages   # Get chat messages
POST   /chats/{id}/send       # Send message (streaming)
```

## 🛠️ Development Architecture

### **Technology Stack**
- **Backend**: .NET 10, ASP.NET Core Minimal API
- **Frontend**: React 18+ Single Page Application
- **Storage**: File system (Markdown + JSON)
- **Streaming**: Server-Sent Events (SSE)
- **Deployment**: Docker Compose

### **Project Structure**
```
src/
├── SharpClaw.Api/          # REST API and SSE endpoints
├── SharpClaw.Core/         # Business logic and file management
├── SharpClaw.Llm/          # LLM service abstractions
├── SharpClaw.Anthropic/    # Anthropic Claude integration
├── SharpClaw.Copilot/      # GitHub Copilot integration
├── SharpClaw.Telegram/     # Telegram bot interface
└── SharpClaw.Web/          # React frontend application
```

## 🔒 Security Considerations

### **File System Access**
- **Path validation** prevents directory traversal attacks
- **Workspace boundaries** - agents can only access designated areas
- **Tool permissions** - granular control over agent capabilities

### **API Security**
- **CORS configuration** for cross-origin requests
- **Input validation** on all endpoints
- **File upload restrictions** with type and size limits

## 🚀 Performance & Scalability

### **File-Based Benefits**
- **Zero database overhead** - no connection pools or queries
- **Simple backup/restore** - just copy files
- **Git integration** - version control for all data
- **Horizontal scaling** - share filesystem across instances

### **Streaming Architecture**
- **Real-time responses** via Server-Sent Events
- **Memory efficient** - messages streamed as generated
- **Connection management** - automatic cleanup of disconnected clients

## 🔧 Extension Points

### **Adding New Agents**
1. Create `{name}.agent.md` file with YAML frontmatter
2. Define personality, expertise, and tools in Markdown
3. Agent automatically available through router

### **Adding New LLM Backends**
1. Implement `ILlmService` interface
2. Register in DI container
3. Configure in agent definitions

### **Adding New Tools**
1. Implement tool interface in `SharpClaw.Core`
2. Register with `ToolRegistry`
3. Add to agent tool lists as needed