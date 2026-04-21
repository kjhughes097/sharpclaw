# SharpClaw Documentation

**SharpClaw** is a personal agent framework built on .NET 10 with React frontend. It uses **file-based storage** for simplicity and supports multiple AI backends.

## 📚 Documentation

- [**Architecture Overview**](./architecture-overview.md) - System design, components, and technical decisions
- [**File-Based Storage**](./file-based-storage.md) - How conversations, projects, and data are stored
- [**Agent System**](./agent-system.md) - Complete guide to all 8 agents and their roles
- [**API Reference**](./api-reference.md) - REST endpoints, streaming, and integration

## 🚀 Quick Start

1. **Clone and configure**:
   ```bash
   git clone https://github.com/kjhughes097/SharpClaw
   cd SharpClaw
   cp .env.example .env
   # Edit .env with your API keys
   ```

2. **Run with Docker**:
   ```bash
   docker compose up --build -d
   ```

3. **Access the UI**:
   - Web interface: http://localhost:3000
   - API: http://localhost:5000

## 🏗️ Architecture Summary

```
┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐
│   React Web     │───▶│   .NET API      │───▶│  File Storage   │
│   Frontend      │    │   Backend       │    │   (Markdown)    │
└─────────────────┘    └─────────────────┘    └─────────────────┘
                                │
                                ▼
                       ┌─────────────────┐
                       │  AI Backends    │
                       │ Anthropic/OpenAI│
                       │ OpenRouter/     │
                       │ GitHub Copilot  │
                       └─────────────────┘
```

## 🎯 Key Features

- **8 Specialist Agents** - Router (Ade) + 7 specialists for different domains
- **File-Based Storage** - No database required, everything stored as files
- **Multi-LLM Support** - Anthropic, OpenAI, OpenRouter, GitHub Copilot
- **Project Organization** - Conversations organized into projects and chats
- **Real-time Streaming** - Server-Sent Events for live conversation updates
- **Workspace Integration** - Secure file system access for agents

## 📁 Storage Layout

```
workspace-root/
├── projects/
│   ├── general/
│   │   ├── context.md
│   │   ├── log.md
│   │   └── chats/
│   │       └── [chat-slug]/
│   │           ├── messages.json
│   │           ├── context.md
│   │           ├── log.md
│   │           └── usage.json
│   └── [project-slug]/
│       └── ...
├── memory/
│   └── agents/
│       ├── cody/
│       ├── paige/
│       └── ...
└── content/
    ├── drafts/
    ├── published/
    └── social/
```