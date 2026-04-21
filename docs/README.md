# SharpClaw Documentation

SharpClaw is a personal AI agent framework built with .NET 10 and React, supporting multiple LLM backends and Model Context Protocol (MCP) integrations.

## Documentation Index

### Core Architecture
- **[Architecture Overview](architecture-overview.md)** - System design and component relationships
- **[Project Structure](project-structure.md)** - Codebase organization and conventions

### Components
- **[Agent System](agent-system.md)** - Agent definitions, routing, and execution
- **[API Layer](api-layer.md)** - REST endpoints and services
- **[Database Design](database-design.md)** - PostgreSQL schema and data models
- **[MCP Integration](mcp-integration.md)** - Model Context Protocol tool execution
- **[Backend Providers](backend-providers.md)** - LLM integration (Anthropic, OpenAI, etc.)

### Features
- **[Session Management](session-management.md)** - Conversation handling and archiving
- **[Workspace Browser](workspace-browser.md)** - Secure file system access
- **[Authentication](authentication.md)** - JWT-based auth system
- **[Telegram Integration](telegram-integration.md)** - Bot interface

### Operations
- **[Configuration](configuration.md)** - Settings and environment setup
- **[Deployment](deployment.md)** - Docker Compose and production setup
- **[Monitoring](monitoring.md)** - Health checks and diagnostics

## Quick Start

1. **Clone and configure**:
   ```bash
   git clone https://github.com/kjhughes097/SharpClaw.git
   cd SharpClaw
   cp .env.example .env
   # Edit .env with your settings
   ```

2. **Start with Docker Compose**:
   ```bash
   docker compose up --build -d
   ```

3. **Access the application**:
   - Web UI: http://localhost:3000
   - API: http://localhost:5000
   - API docs: http://localhost:5000/scalar/v1

## Architecture Overview

SharpClaw uses a modular architecture with:

- **Frontend**: React SPA for real-time chat interface
- **Backend**: .NET 10 API with SSE streaming for real-time responses
- **Database**: PostgreSQL for persistent data storage
- **Integration**: MCP servers for tool execution and external integrations
- **Multi-LLM**: Support for Anthropic, OpenAI, OpenRouter, and GitHub Copilot

The system is designed for personal use with enterprise-level security and extensibility.