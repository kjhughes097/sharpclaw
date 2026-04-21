# Architecture Overview

SharpClaw is a modern personal agent framework built on .NET 10 that provides a scalable, secure, and extensible platform for AI-powered assistants. This document outlines the high-level architecture and core design principles.

## System Architecture

```
┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐
│                 │    │                 │    │                 │
│   Web Frontend  │◄──►│   REST API      │◄──►│   PostgreSQL    │
│   (React SPA)   │    │   (.NET Core)   │    │   Database      │
│                 │    │                 │    │                 │
└─────────────────┘    └─────────────────┘    └─────────────────┘
                                │
                                ▼
                       ┌─────────────────┐
                       │                 │
                       │  Agent Runtime  │
                       │                 │
                       └─────────────────┘
                                │
              ┌─────────────────┼─────────────────┐
              ▼                 ▼                 ▼
    ┌─────────────────┐ ┌─────────────────┐ ┌─────────────────┐
    │                 │ │                 │ │                 │
    │ LLM Backends    │ │ MCP Servers     │ │ Telegram Bot    │
    │ (Multi-provider)│ │ (Tools & APIs)  │ │ (Optional)      │
    │                 │ │                 │ │                 │
    └─────────────────┘ └─────────────────┘ └─────────────────┘
```

## Core Components

### 1. Frontend Layer (SharpClaw.Web)
- **Technology**: React with TypeScript
- **Purpose**: Web-based chat interface for agent interaction
- **Features**: 
  - JWT-based authentication
  - Real-time conversation streaming
  - Agent management UI
  - Mobile-responsive design
  - Light/dark theme support

### 2. API Layer (SharpClaw.Api)
- **Technology**: ASP.NET Core REST API
- **Purpose**: Business logic and data orchestration
- **Responsibilities**:
  - Agent lifecycle management
  - Session and conversation handling
  - Backend provider coordination
  - Tool permission enforcement
  - User authentication and authorization

### 3. Core Framework (SharpClaw.Core)
- **Purpose**: Shared abstractions and domain models
- **Key Components**:
  - `IAgentBackend` - LLM provider abstraction
  - `SessionStore` - PostgreSQL data access layer
  - `AgentRunner` - Agent execution orchestration
  - `PermissionGate` - Tool execution security

### 4. Backend Providers
Pluggable LLM integrations with unified interface:
- **SharpClaw.Anthropic** - Claude models via Anthropic API
- **SharpClaw.OpenAI** - GPT models via OpenAI API
- **SharpClaw.OpenRouter** - Multi-model access via OpenRouter
- **SharpClaw.Copilot** - GitHub Copilot SDK integration

### 5. MCP Integration
Model Context Protocol for secure tool execution:
- Protocol-compliant tool providers
- Agent-scoped permission policies
- Sandboxed execution environment
- Automatic approval/manual review workflows

### 6. Data Layer
PostgreSQL database providing:
- Agent definitions and configurations
- Conversation history and sessions
- Backend provider settings
- Tool execution logs
- User authentication data

### 7. Session Management & Knowledge System
Advanced session lifecycle and knowledge management:
- **Session Archiving** - Mark completed sessions as archived with timestamps
- **Knowledge Generation** - Auto-generate Markdown summaries from archived sessions
- **Knowledge Storage** - Persist session summaries in workspace `knowledge/` folder
- **Session Organization** - Separate active from archived sessions in UI
- **Knowledge Retrieval** - Browse and search previously archived session summaries

### 8. Workspace Integration
Secure file system access and management:
- **Workspace Browser** - Web UI for navigating workspace files and directories
- **Path Security** - Validation ensures access remains within workspace boundaries
- **File Metadata** - Display file sizes, modification dates, and permissions
- **Agent File Access** - MCP tools can securely access workspace files
- **Directory Operations** - Support for file/folder navigation and content viewing

## Architectural Principles

### 1. **Separation of Concerns**
- Clear boundaries between presentation, business logic, and data layers
- Backend providers isolated behind common interface
- Tool execution decoupled through MCP protocol

### 2. **Extensibility**
- Plugin architecture for LLM backends
- MCP standard for tool integration
- Configuration-driven agent definitions

### 3. **Security by Design**
- JWT-based authentication with proper token lifecycle
- Permission-gated tool execution
- Database parameter sanitization
- Input validation and output sanitization

### 4. **Scalability**
- Stateless API design for horizontal scaling
- Efficient database schema with proper indexing
- Async/await throughout for non-blocking operations
- Connection pooling and resource management

### 5. **Developer Experience**
- Type-safe implementations throughout
- Comprehensive error handling and logging
- Docker-based development and deployment
- OpenAPI specification for API documentation

## Data Flow

### Agent Conversation Flow
1. **User Input** → Web frontend captures user message
2. **API Processing** → REST API validates and stores message
3. **Agent Execution** → Agent runner loads agent configuration and history
4. **Backend Interaction** → Appropriate LLM backend processes the conversation
5. **Tool Execution** → MCP tools executed based on model requests and permissions
6. **Response Generation** → Final response generated and streamed back
7. **Persistence** → Conversation state saved to database

### Agent Management Flow
1. **Agent Definition** → Markdown-based agent configuration
2. **Registration** → API loads and validates agent definition
3. **Backend Assignment** → Agent linked to appropriate LLM backend
4. **Tool Permissions** → MCP permission policies configured
5. **Activation** → Agent becomes available for conversations

## Technology Stack

### Backend
- **.NET 10** - Modern C# with latest language features
- **ASP.NET Core** - High-performance web framework
- **PostgreSQL 16** - Robust relational database
- **Npgsql** - High-performance PostgreSQL driver

### Frontend
- **React 18** - Modern UI framework with hooks
- **TypeScript** - Type safety and enhanced developer experience
- **Vite** - Fast build tool and dev server

### Infrastructure
- **Docker** - Containerized deployment
- **Docker Compose** - Multi-container orchestration
- **JWT** - Stateless authentication
- **OpenAPI** - API documentation and client generation

## Design Trade-offs

### Monolithic vs Microservices
**Choice**: Monolithic API with modular internal architecture

**Rationale**:
- Simpler deployment and debugging for personal/small team use
- Lower operational overhead
- Clear boundaries maintained through interfaces
- Can evolve to microservices if needed

### Database Choice
**Choice**: PostgreSQL over NoSQL alternatives

**Rationale**:
- Strong consistency requirements for conversation history
- Complex relational queries needed for agent management
- JSON support provides flexibility where needed
- Mature ecosystem and tooling

### Frontend Architecture
**Choice**: Single Page Application over Server-Side Rendering

**Rationale**:
- Real-time chat requires persistent connection
- Better user experience for interactive features
- Simpler deployment model with API separation
- Mobile-responsive design easier to achieve