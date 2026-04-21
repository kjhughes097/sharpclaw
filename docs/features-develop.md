# SharpClaw Develop Branch Features

This document describes the latest features available in the `develop` branch that enhance the core SharpClaw framework.

## 🗃️ Session Archiving & Knowledge Management

### Overview
Complete session lifecycle management with automatic knowledge generation for long-term retention and organization.

### Key Features

#### Session Archiving
- **Manual archiving** via UI archive button or API endpoint
- **Soft deletion** - archived sessions retain full history
- **Timestamp tracking** with `archived_at` field
- **UI organization** - archived sessions separate from active ones

#### Knowledge Generation
Archived sessions automatically generate structured Markdown summaries:

```markdown
# Session Summary: {title}

**Session ID**: {sessionId}  
**Agent**: {agentSlug}  
**Date**: {archivedDate}  
**Tags**: {extractedTags}

## Summary
{conversationRecap}

## Key Points
- {extractedKeyPoints}

## Technical Details
- {technicalDiscussions}
```

#### Knowledge Storage
- **Location**: `{workspace}/knowledge/` directory
- **Naming**: `YYYY-MM-DD-{shortId}-{sanitizedTitle}.md`
- **Searchable**: Files can be indexed and searched by agents
- **Persistent**: Knowledge survives session cleanup

### Implementation Details

#### Database Schema
```sql
-- New columns added to sessions table
ALTER TABLE sessions ADD COLUMN is_archived BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE sessions ADD COLUMN archived_at TIMESTAMPTZ NULL;
```

#### API Endpoints
```http
PUT /api/sessions/{sessionId}/archive
# Archives session and generates knowledge

GET /api/workspace/knowledge
# Lists all knowledge files

GET /api/workspace/knowledge/{filename}
# Retrieves specific knowledge content
```

#### KnowledgeService
```csharp
public sealed class KnowledgeService
{
    // Generates recap summary with title, tags, and content
    public string? GenerateAndStore(string sessionId, string agentSlug, string personaName)
    
    // Lists available knowledge files with metadata
    public IReadOnlyList<KnowledgeEntry> ListKnowledge()
}
```

### Benefits
- **Long-term retention** of valuable conversations
- **Searchable knowledge base** for future reference
- **Organized workspace** with clear active/archived separation
- **Automatic summarization** reduces manual knowledge management

## 🗂️ Workspace Browser

### Overview
Secure file system integration providing web-based workspace navigation and file management.

### Key Features

#### Directory Navigation
- **Hierarchical browsing** of workspace directory structure
- **File metadata display** (size, modification date, permissions)
- **Breadcrumb navigation** for easy path traversal
- **Folder expansion/collapse** for efficient exploration

#### Security Architecture
- **Path validation** ensures all access remains within workspace boundaries
- **Permission respect** follows underlying filesystem access controls
- **Traversal prevention** blocks `../` and absolute path attacks
- **Sanitized responses** prevent information disclosure

#### File Operations
- **Content viewing** for text files with syntax highlighting
- **Binary file detection** with appropriate handling
- **Directory listing** with sorting and filtering options
- **File type recognition** via MIME type detection

### Implementation Details

#### API Endpoints
```http
GET /api/workspace/browse?path={relativePath}
# Browse directory contents securely

GET /api/workspace/files/content?path={relativePath}
# Read file contents with validation
```

#### Response Format
```typescript
interface WorkspaceEntry {
  name: string;
  path: string;
  isDirectory: boolean;
  size: number | null;           // null for directories
  modifiedAt: string | null;     // ISO timestamp
  permissions: string | null;    // e.g., "rw-r--r--"
}
```

#### Security Validation
```csharp
public static bool IsPathWithinRoot(string basePath, string requestedPath)
{
    // Normalize and validate path containment
    var fullBasePath = Path.GetFullPath(basePath);
    var fullRequestedPath = Path.GetFullPath(Path.Combine(basePath, requestedPath));
    return fullRequestedPath.StartsWith(fullBasePath + Path.DirectorySeparatorChar) ||
           fullRequestedPath == fullBasePath;
}
```

#### React UI Component
```typescript
// WorkspaceBrowserView.tsx
export function WorkspaceBrowserView({ onMenuClick }: Props) {
  const [currentPath, setCurrentPath] = useState('');
  const [entries, setEntries] = useState<WorkspaceEntry[]>([]);
  
  // Secure navigation with path validation
  const navigateTo = (path: string) => {
    if (isValidPath(path)) {
      setCurrentPath(path);
      loadEntries(path);
    }
  };
}
```

### Benefits
- **Agent tool access** - agents can browse and access workspace files
- **Development workflow** - easily inspect workspace state during development
- **Security assurance** - contained access prevents system compromise
- **User convenience** - no need to SSH or use external file managers

## 🔄 Integration with Core System

### Agent Integration
Both features integrate seamlessly with existing agent capabilities:

#### Knowledge Access
- Agents can read archived knowledge via MCP tools
- Knowledge files appear in workspace browser
- Searchable content for context retrieval

#### File System Tools
- MCP tools gain secure workspace access
- Agents can create, read, and modify workspace files
- Path validation ensures security boundaries

### Telegram Bot Integration
```typescript
// Archive session via Telegram
if (content.toLowerCase() === '/archive') {
    await this.apiClient.archiveSession(sessionId);
    await ctx.reply('✅ Session archived and knowledge generated');
}
```

### UI Enhancements
- **Archive button** in session sidebar
- **Knowledge browser** in workspace view
- **Visual indicators** for archived sessions
- **Responsive design** for mobile access

## 🚀 Migration & Deployment

### Database Migration
The develop branch includes automatic database schema updates:
```sql
-- Applied automatically on startup
ALTER TABLE sessions ADD COLUMN IF NOT EXISTS is_archived BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE sessions ADD COLUMN IF NOT EXISTS archived_at TIMESTAMPTZ NULL;
```

### Configuration Updates
No breaking configuration changes - existing setups work unchanged.

### Backward Compatibility
- All existing APIs remain functional
- New features are additive, not replacing
- Graceful degradation if features not used

## 📈 Performance Considerations

### Knowledge Generation
- **Async processing** - archiving doesn't block UI
- **Incremental updates** - only generates knowledge for new archives
- **File system efficiency** - knowledge files use standard Markdown format

### Workspace Browser
- **Lazy loading** - directories loaded on demand
- **Caching strategy** - file metadata cached temporarily
- **Efficient queries** - minimal filesystem operations per request

These features significantly enhance SharpClaw's capabilities while maintaining the security and performance characteristics of the core framework.