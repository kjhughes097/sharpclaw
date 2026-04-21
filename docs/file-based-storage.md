# File-Based Storage

SharpClaw uses a **file-based storage system** instead of a traditional database. All conversations, projects, and agent memory are stored as files on disk, making the system simple to deploy, backup, and version control.

## 🎯 Design Philosophy

### **Why File-Based?**
- **Simplicity**: No database server to install, configure, or maintain
- **Portability**: Entire system state is just files and folders
- **Transparency**: All data is human-readable (Markdown + JSON)
- **Version Control**: Complete history can be managed with Git
- **Backup/Recovery**: Simple file copying, no database dumps needed

### **Trade-offs**
✅ **Pros**: Simple deployment, human-readable, Git-friendly, zero database overhead  
⚠️ **Considerations**: Not optimized for high-concurrency or complex queries

## 📁 Storage Architecture

### **Root Structure**
```
$SharpClaw__WorkspaceRoot/
├── projects/           # User projects and conversations
├── memory/            # Agent memory files
├── content/           # Content management (Paige)
├── finance/           # Finance tracking (Fin)
├── running/           # Running logs (Myles)
└── knowledge/         # Knowledge base (Noah)
```

## 🗂️ Projects & Chats

### **Project Structure**
Each project is a directory under `projects/` containing:

```
projects/my-project/
├── context.md         # Project description and goals
├── log.md            # Project-level activity log
└── chats/            # All conversations in this project
    ├── 20260421-120000-initial-discussion/
    ├── 20260421-143000-feature-planning/
    └── 20260422-090000-bug-investigation/
```

### **Chat Structure**
Each chat is a timestamped directory containing:

```
chats/20260421-120000-initial-discussion/
├── messages.json      # Complete conversation history
├── context.md        # Chat summary and context
├── log.md           # Chat-level event log
└── usage.json       # Token usage tracking
```

### **File Formats**

#### **context.md** (Project/Chat)
```markdown
# Project: My Awesome Project
Created: 2026-04-21

## Goals
- Build a great application
- Learn new technologies

## Current State
- Initial planning phase
- Requirements gathering complete

## Recent Activity
- Discussed architecture options
- Selected tech stack
```

#### **messages.json** (Chat History)
```json
[
  {
    "id": "msg_001",
    "role": "user",
    "content": "Help me build a web application",
    "timestamp": "2026-04-21T12:00:00Z",
    "agentSlug": null
  },
  {
    "id": "msg_002", 
    "role": "assistant",
    "content": "I'd be happy to help! What kind of web application are you thinking of building?",
    "timestamp": "2026-04-21T12:00:15Z",
    "agentSlug": "ade"
  }
]
```

#### **usage.json** (Token Tracking)
```json
[
  {
    "timestamp": "2026-04-21T12:00:15Z",
    "agentSlug": "ade",
    "model": "claude-sonnet-4-20250514",
    "inputTokens": 150,
    "outputTokens": 45,
    "costUsd": 0.00285
  }
]
```

## 🧠 Agent Memory System

Each agent maintains persistent memory files under `memory/agents/{agent}/`:

### **Memory File Types**

| File | Purpose | Update Pattern |
|------|---------|----------------|
| `working.md` | Current conversation context | Every few turns |
| `memory.md` | Mid-term memory (past month) | End of conversations |
| `history.md` | Long-term memory (all time) | When topics conclude |

### **Memory Structure**
```
memory/agents/cody/
├── working.md         # Current context and active topics
├── memory.md         # Recent discussions and outcomes  
├── history.md        # Enduring facts and experiences
└── audit/
    ├── 2026-04.log   # April activity log
    └── 2026-05.log   # May activity log
```

### **Sample Memory File**
```markdown
# Cody - Working Memory
Updated: 2026-04-21 12:30

## Current Context
Working on SharpClaw documentation with user. Recently switched from PostgreSQL to file-based storage system.

## Active Topics
- File-based storage architecture
- Agent system documentation
- Markdown-based conversations

## Open Questions
- Performance implications of file-based approach
- Backup strategies for production deployments
```

## 📊 Data Management

### **File Operations**

#### **ProjectManager**
Handles project lifecycle:
```csharp
public sealed class ProjectManager
{
    private readonly string _projectsRoot;
    
    public IReadOnlyList<ProjectInfo> ListProjects()
    public ProjectInfo? GetProject(string slug)  
    public ProjectInfo CreateProject(string name)
    public bool DeleteProject(string slug)
}
```

#### **ChatManager** 
Manages conversations within projects:
```csharp
public sealed class ChatManager
{
    public IReadOnlyList<ChatInfo> ListChats(string projectSlug)
    public ChatInfo CreateChat(string projectSlug, string title)
    public IReadOnlyList<ChatMessage> GetMessages(string projectSlug, string chatSlug)
    public void AppendMessage(string projectSlug, string chatSlug, ChatMessage message)
}
```

### **Slug Generation**
Automatic slug creation for URLs and file names:
- **Projects**: `my-awesome-project`
- **Chats**: `20260421-120000-initial-discussion`

Pattern: `{timestamp}-{title-slug}` ensures uniqueness and chronological sorting.

## 🔍 Performance Considerations

### **File System Optimization**

#### **Directory Scanning**
- **Project listing**: Single directory scan of `projects/`
- **Chat listing**: Single directory scan of `projects/{slug}/chats/`
- **Message loading**: Single JSON file read per chat

#### **Caching Opportunities**
- **Project metadata**: Cache `DirectoryInfo` results
- **Chat summaries**: Cache first-line parsing of `context.md` files
- **Message counts**: Cache JSON array length without full deserialization

### **Scalability Patterns**

#### **Horizontal Scaling**
- **Shared filesystem**: NFS, EFS, or similar for multi-instance deployments
- **Read replicas**: Multiple read-only instances sharing storage
- **Archive strategies**: Move old chats to cold storage

#### **Backup Strategies**
```bash
# Simple file-based backup
tar -czf sharpclaw-backup-$(date +%Y%m%d).tar.gz projects/ memory/ content/

# Incremental backup with rsync
rsync -av --delete $WORKSPACE_ROOT/ backup-destination/

# Git-based versioning
cd $WORKSPACE_ROOT && git add -A && git commit -m "Daily backup $(date)"
```

## 🛡️ Security & Integrity

### **Path Validation**
```csharp
// Prevent directory traversal attacks
var safePath = Path.GetFullPath(Path.Combine(rootPath, userInput));
if (!safePath.StartsWith(rootPath))
    throw new SecurityException("Invalid path");
```

### **File Permissions**
- **Read/write**: Only to designated workspace areas
- **Agent boundaries**: Each agent limited to specific directories
- **User isolation**: Projects scoped to user workspace

### **Data Integrity**
- **JSON validation**: Schema validation on message files
- **Atomic writes**: Use temporary files + rename for consistency
- **Backup validation**: Periodic integrity checks on stored data

## 🔧 Migration & Maintenance

### **Data Migration**
Moving from other systems:
```bash
# Import from database
scripts/export-from-db.py > conversations.json
scripts/import-to-files.py conversations.json

# Bulk operations
find projects/ -name "*.json" -exec jq . {} \; # Validate all JSON
grep -r "search-term" projects/             # Search all conversations
```

### **Maintenance Tasks**
- **Cleanup**: Remove empty projects/chats
- **Archival**: Move old conversations to archive storage
- **Optimization**: Compress large JSON files, split oversized chats
- **Analytics**: Generate usage reports from token tracking files

## 🚀 Development Workflow

### **Local Development**
```bash
# Set up workspace
export SharpClaw__WorkspaceRoot="$HOME/sharpclaw-workspace"
mkdir -p $SharpClaw__WorkspaceRoot/{projects,memory,content}

# Run application
cd SharpClaw && dotnet run
```

### **Testing Strategies**
- **Unit tests**: Mock file operations with in-memory filesystem
- **Integration tests**: Use temporary directories for isolated testing
- **Load tests**: Generate large conversation files to test performance