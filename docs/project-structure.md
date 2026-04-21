# Project Structure

This document outlines the organization of the SharpClaw codebase, explaining the purpose and contents of each project and directory.

## Repository Overview

```
sharpclaw/
├── SharpClaw.Core/              # Framework abstractions and domain models
├── SharpClaw.Api/               # REST API and business logic  
├── SharpClaw.Web/               # React frontend application
├── SharpClaw.Anthropic/         # Claude/Anthropic backend provider
├── SharpClaw.OpenAI/            # OpenAI/GPT backend provider  
├── SharpClaw.OpenRouter/        # OpenRouter multi-model provider
├── SharpClaw.Copilot/           # GitHub Copilot SDK integration
├── SharpClaw.Telegram/          # Telegram bot service
├── SharpClaw.RebuildHook/       # Development utility
├── agents/                      # Agent definitions (markdown)
├── scripts/                     # Development and deployment scripts
├── docs/                        # Technical documentation
├── docker-compose.yml           # Multi-container deployment
├── Dockerfile                   # API container image
└── SharpClaw.slnx              # .NET solution file
```

## Core Projects

### SharpClaw.Core
**Purpose**: Shared framework providing abstractions and domain models

**Key Files**:
```
SharpClaw.Core/
├── IAgentBackend.cs            # LLM provider abstraction
├── IAgentBackendProvider.cs    # Backend factory interface
├── AgentPersona.cs             # Agent configuration model
├── AgentRunner.cs              # Agent lifecycle management
├── SessionStore.cs             # PostgreSQL data access layer
├── McpServerRegistry.cs        # MCP server management
├── PermissionGate.cs           # Tool execution security
├── ConversationHistory.cs      # Chat message handling
├── ChatTypes.cs                # Message and tool schemas
├── TokenUsageRecord.cs         # Usage tracking models
└── SharpClaw.Core.csproj       # Project dependencies
```

**Dependencies**:
- `Npgsql` - PostgreSQL driver
- `ModelContextProtocol.Client` - MCP protocol implementation
- `Microsoft.Extensions.Logging` - Structured logging

### SharpClaw.Api  
**Purpose**: REST API providing business logic and data orchestration

**Structure**:
```
SharpClaw.Api/
├── Controllers/                # REST API endpoints
│   ├── AgentsController.cs     # Agent management
│   ├── SessionsController.cs   # Conversation handling
│   ├── AuthController.cs       # User authentication
│   ├── McpsController.cs       # MCP server management
│   ├── WorkspaceController.cs  # File system operations
│   └── KnowledgeController.cs  # Knowledge base integration
├── Services/                   # Business logic services
│   ├── BackendSettingsService.cs    # LLM provider config
│   ├── BackendModelService.cs       # Model discovery
│   ├── SessionRuntimeService.cs     # Agent execution
│   ├── KnowledgeService.cs          # Knowledge base
│   ├── AuthService.cs               # Authentication logic
│   └── JwtTokenService.cs           # JWT token handling
├── Models/                     # DTOs and request/response models  
├── Middleware/                 # HTTP request pipeline
│   └── JwtAuthMiddleware.cs    # JWT validation
├── Program.cs                  # Application entry point
├── ApiMapper.cs                # Entity to DTO mapping
└── ApiValidator.cs             # Input validation helpers
```

**Dependencies**:
- `ASP.NET Core` - Web framework
- `Scalar.AspNetCore` - OpenAPI documentation
- `BCrypt.Net-Next` - Password hashing
- `System.IdentityModel.Tokens.Jwt` - JWT handling

### SharpClaw.Web
**Purpose**: React-based web interface for agent interaction

**Structure**:
```
SharpClaw.Web/
├── public/                     # Static assets
├── src/
│   ├── components/             # React components
│   │   ├── AgentManagement/    # Agent CRUD interface
│   │   ├── Chat/               # Conversation interface
│   │   ├── Settings/           # Configuration panels
│   │   └── Auth/               # Login/logout
│   ├── services/               # API client services
│   ├── hooks/                  # Custom React hooks
│   ├── types/                  # TypeScript type definitions
│   ├── utils/                  # Utility functions
│   └── App.tsx                 # Root application component
├── package.json                # Node.js dependencies
├── vite.config.ts              # Build configuration
├── tailwind.config.js          # CSS framework config
└── Dockerfile                  # Container image
```

**Dependencies**:
- `React 18` - UI framework with hooks
- `TypeScript` - Type safety
- `Tailwind CSS` - Utility-first styling  
- `Vite` - Build tool and dev server
- `React Router` - Client-side routing

## Backend Provider Projects

### SharpClaw.Anthropic
**Purpose**: Claude model integration via Anthropic API

```
SharpClaw.Anthropic/
├── AnthropicBackendProvider.cs      # Provider factory
├── AnthropicBackend.cs              # Agent backend implementation
├── AnthropicModelService.cs         # Model discovery
└── AnthropicSettings.cs             # Configuration
```

### SharpClaw.OpenAI
**Purpose**: GPT model integration via OpenAI API

```
SharpClaw.OpenAI/
├── OpenAIBackendProvider.cs         # Provider factory
├── OpenAIBackend.cs                 # Agent backend implementation
├── OpenAIModelService.cs            # Model discovery  
└── OpenAISettings.cs                # Configuration
```

### SharpClaw.OpenRouter
**Purpose**: Multi-model access via OpenRouter

```
SharpClaw.OpenRouter/  
├── OpenRouterBackendProvider.cs     # Provider factory
├── OpenRouterBackend.cs             # Agent backend implementation
├── OpenRouterModelService.cs        # Model discovery
└── OpenRouterSettings.cs            # Configuration
```

### SharpClaw.Copilot
**Purpose**: GitHub Copilot SDK integration

```
SharpClaw.Copilot/
├── CopilotBackendProvider.cs        # Provider factory
├── CopilotBackend.cs                # Agent backend implementation
├── CopilotModelService.cs           # Model discovery
└── CopilotSettings.cs               # Configuration
```

## Supporting Projects

### SharpClaw.Telegram
**Purpose**: Telegram bot for mobile/messaging access

```
SharpClaw.Telegram/
├── TelegramBot.cs                   # Bot service implementation
├── TelegramSettings.cs              # Configuration
├── Program.cs                       # Service entry point
└── Dockerfile                       # Container image
```

### SharpClaw.RebuildHook
**Purpose**: Development utility for live reload during development

```
SharpClaw.RebuildHook/
├── Program.cs                       # File watcher service
└── SharpClaw.RebuildHook.csproj    # Minimal dependencies
```

## Configuration and Data

### agents/
**Purpose**: Agent definitions in markdown format

```
agents/
├── ade.md                          # Routing agent
├── cody.md                         # Software development agent
├── debbie.md                       # Debugging specialist
├── noah.md                         # Knowledge management
└── remy.md                         # Research specialist
```

Each agent file follows this format:
```markdown
---
name: Agent Name
description: Brief description
backend: anthropic|openai|openrouter|copilot
model: specific-model-name
mcpServers: [list-of-mcp-servers]
permissionPolicy:
  tool.pattern: auto_approve|ask|deny
isEnabled: true|false
---

Agent system prompt and instructions...
```

### scripts/
**Purpose**: Development and deployment automation

```
scripts/
├── setup.sh                       # Environment setup
├── deploy.sh                      # Production deployment
├── backup.sh                      # Database backup
└── migrate.sh                     # Schema migration
```

## Build and Deployment Files

### docker-compose.yml
**Purpose**: Multi-container orchestration for local development and production

**Services**:
- `postgres` - PostgreSQL database
- `sharpclaw` - API backend (built from Dockerfile)  
- `web` - React frontend (built from SharpClaw.Web/Dockerfile)
- `telegram` - Telegram bot service

### Dockerfile
**Purpose**: Multi-stage build for API backend

**Stages**:
1. **Build stage**: Restore NuGet packages and compile .NET projects
2. **Publish stage**: Create optimized release build
3. **Runtime stage**: Minimal runtime image with only published binaries

### SharpClaw.slnx
**Purpose**: .NET solution file defining project relationships

**Project References**:
- All SharpClaw.* projects included
- Proper dependency ordering for build
- Shared configuration for code analysis

## Coding Conventions

### Namespace Organization
```csharp
namespace SharpClaw.Core;           // Core abstractions
namespace SharpClaw.Api.Controllers; // API endpoints
namespace SharpClaw.Api.Services;   // Business logic
namespace SharpClaw.Anthropic;      // Provider implementations
```

### File Naming
- **PascalCase** for C# files and classes
- **kebab-case** for configuration files and scripts
- **camelCase** for TypeScript/JavaScript files
- **lowercase** for Docker and infrastructure files

### Project Dependencies
```
┌─────────────────┐
│ SharpClaw.Web   │ (independent frontend)
└─────────────────┘

┌─────────────────┐    ┌─────────────────┐
│ SharpClaw.Api   │───►│ SharpClaw.Core  │
└─────────────────┘    └─────────────────┘
         ▲                       ▲
         │                       │
┌─────────────────┐    ┌─────────────────┐
│ Provider        │───►│ Provider        │
│ Projects        │    │ Dependencies    │
└─────────────────┘    └─────────────────┘
```

### Shared Patterns
- **Dependency injection** throughout .NET projects
- **Async/await** for all I/O operations  
- **Readonly record types** for immutable data
- **Sealed classes** where inheritance not intended
- **Nullable reference types** enabled

## Development Workflow

### Local Development
1. **Clone repository**: `git clone [repository-url]`
2. **Start dependencies**: `docker compose up postgres -d`
3. **Run API**: `dotnet run --project SharpClaw.Api`
4. **Run frontend**: `cd SharpClaw.Web && npm run dev`
5. **Access application**: `http://localhost:3000`

### Adding New Backend Provider
1. **Create project**: `SharpClaw.NewProvider/`
2. **Implement interfaces**: `IAgentBackendProvider`, `IAgentBackend`
3. **Add service registration**: Update `Program.cs` dependency injection
4. **Add agent support**: Update agent markdown to support new backend

### Adding New Agent
1. **Create definition**: `agents/new-agent.md`
2. **Configure permissions**: Define tool access policies
3. **Set MCP servers**: Assign relevant tool capabilities
4. **Test agent**: Use API or web interface to validate behavior