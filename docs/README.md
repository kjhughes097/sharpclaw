# SharpClaw Documentation

Welcome to the comprehensive SharpClaw documentation. This directory contains detailed technical documentation about the architecture, design decisions, and implementation of the SharpClaw personal agent framework.

## Table of Contents

### Architecture & Design
- [Architecture Overview](./architecture-overview.md) - High-level system architecture and component relationships
- [Database Design](./database-design.md) - PostgreSQL schema and data modeling decisions
- [Agent System](./agent-system.md) - Core agent architecture and lifecycle
- [Backend Providers](./backend-providers.md) - Multi-LLM backend abstraction layer
- [MCP Integration](./mcp-integration.md) - Model Context Protocol tool execution system

### Components
- [API Layer](./api-layer.md) - REST API architecture and endpoints
- [Web Frontend](./web-frontend.md) - React-based user interface
- [Authentication](./authentication.md) - JWT-based auth system
- [Session Management](./session-management.md) - Conversation persistence and lifecycle
- [Telegram Integration](./telegram-integration.md) - Telegram bot implementation

### Latest Features
- [Develop Branch Features](./features-develop.md) - Session archiving, knowledge management, and workspace browser

### Development
- [Project Structure](./project-structure.md) - Codebase organization and conventions
- [Development Setup](./development-setup.md) - Local development environment
- [Deployment Guide](./deployment-guide.md) - Production deployment options
- [Configuration](./configuration.md) - Environment variables and settings

### Design Decisions
- [Technology Choices](./technology-choices.md) - Why .NET, PostgreSQL, React, etc.
- [Security Design](./security-design.md) - Security architecture and considerations
- [Performance & Scaling](./performance-scaling.md) - Performance optimization strategies

## Quick Reference

### Key Concepts
- **Agent**: An AI assistant with specific persona, backend model, and tool permissions
- **Session**: A conversation thread between user and agent with persistent history
- **Backend**: LLM provider abstraction (Anthropic, OpenAI, OpenRouter, GitHub Copilot)
- **MCP Server**: Tool provider following Model Context Protocol for safe execution
- **Permission Policy**: Agent-specific rules for tool execution approval

### Core Components
- **SharpClaw.Core**: Framework abstractions and domain models
- **SharpClaw.Api**: REST API and business logic
- **SharpClaw.Web**: React frontend application
- **SharpClaw.{Provider}**: LLM backend implementations
- **SharpClaw.Telegram**: Telegram bot service

### Getting Started
For a quick start, see the main [README.md](../README.md) in the root directory.