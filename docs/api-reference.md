# API Reference

SharpClaw provides a **RESTful API** built on ASP.NET Core with **Server-Sent Events** for real-time streaming. The API manages projects, chats, messages, and workspace integration.

## 🌐 Base Configuration

### **Development Server**
- **API**: `http://localhost:5000`
- **Web UI**: `http://localhost:3000` 
- **API Docs**: `http://localhost:5000/swagger` (if enabled)

### **Production Deployment**
- **Docker**: Port 5000 (configurable via environment variables)
- **HTTPS**: Recommended with reverse proxy (nginx, Cloudflare)
- **CORS**: Configured for React SPA cross-origin requests

## 📁 Project Management

### **List Projects**
```http
GET /projects
```

**Response:**
```json
[
  {
    "slug": "general",
    "name": "General", 
    "createdAt": "2026-04-13T00:00:00Z",
    "chats": [],
    "totalInputTokens": 15420,
    "totalOutputTokens": 8930
  },
  {
    "slug": "my-project",
    "name": "My Awesome Project",
    "createdAt": "2026-04-21T12:00:00Z", 
    "chats": [
      {
        "slug": "20260421-120000-initial-discussion",
        "title": "Initial Discussion",
        "lastAgent": "cody",
        "createdAt": "2026-04-21T12:00:00Z",
        "lastActivityAt": "2026-04-21T12:30:00Z"
      }
    ],
    "totalInputTokens": 2540,
    "totalOutputTokens": 1890
  }
]
```

### **Get Single Project**
```http  
GET /projects/{slug}
```

**Parameters:**
- `slug` (string) - Project identifier

**Response:** Single project object (same format as list item)

### **Create Project**
```http
POST /projects
Content-Type: application/json

{
  "name": "My New Project"
}
```

**Response:**
```json
{
  "slug": "my-new-project",
  "name": "My New Project", 
  "createdAt": "2026-04-21T15:00:00Z",
  "chats": [],
  "totalInputTokens": 0,
  "totalOutputTokens": 0
}
```

### **Delete Project**
```http
DELETE /projects/{slug}
```

**Note:** Cannot delete the `general` project

## 💬 Chat Management

### **List Chats in Project**
```http
GET /projects/{projectSlug}/chats
```

**Response:**
```json
[
  {
    "slug": "20260421-120000-initial-discussion",
    "title": "Initial Discussion",
    "lastAgent": "cody",
    "createdAt": "2026-04-21T12:00:00Z",
    "lastActivityAt": "2026-04-21T12:30:00Z",
    "totalInputTokens": 850,
    "totalOutputTokens": 620
  }
]
```

### **Get Single Chat**
```http
GET /chats/{chatId}
```

**Parameters:**
- `chatId` (string) - Format: `{projectSlug}--{chatSlug}`

**Response:** Single chat object with full details

### **Create Chat**
```http
POST /projects/{projectSlug}/chats
Content-Type: application/json

{
  "title": "New Discussion Topic"
}
```

**Response:**
```json
{
  "slug": "20260421-150000-new-discussion-topic",
  "title": "New Discussion Topic",
  "lastAgent": null,
  "createdAt": "2026-04-21T15:00:00Z", 
  "lastActivityAt": "2026-04-21T15:00:00Z",
  "totalInputTokens": 0,
  "totalOutputTokens": 0
}
```

### **Delete Chat**
```http
DELETE /chats/{chatId}
```

## 📨 Message System

### **Get Chat Messages**
```http
GET /chats/{chatId}/messages
```

**Response:**
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
    "content": "I'd be happy to help! What kind of web application are you building?",
    "timestamp": "2026-04-21T12:00:15Z", 
    "agentSlug": "ade"
  }
]
```

### **Send Message (Streaming)**
```http
POST /chats/{chatId}/send
Content-Type: application/json
Accept: text/event-stream

{
  "content": "I want to build a task management app"
}
```

**Response:** Server-Sent Events stream

```
data: {"type":"agent","agent":"ade"}

data: {"type":"content","content":"I'll help you plan that task management app! "}

data: {"type":"content","content":"Let me route this to Cody, our development specialist."}

data: {"type":"agent","agent":"cody"}

data: {"type":"content","content":"Great choice! Let's start by understanding your requirements..."}

data: {"type":"done","messageId":"msg_003","usage":{"inputTokens":125,"outputTokens":89}}
```

## 📊 Server-Sent Events

### **Event Types**

| Type | Purpose | Data Format |
|------|---------|-------------|
| `agent` | Agent switch notification | `{"agent": "cody"}` |
| `content` | Streaming message content | `{"content": "text chunk"}` |
| `error` | Error occurred | `{"error": "Error message"}` |
| `done` | Message complete | `{"messageId": "msg_123", "usage": {...}}` |

### **JavaScript Client Example**
```javascript
const eventSource = new EventSource('/chats/my-project--my-chat/send', {
  method: 'POST',
  headers: {
    'Content-Type': 'application/json'
  },
  body: JSON.stringify({ content: 'Hello!' })
});

eventSource.onmessage = (event) => {
  const data = JSON.parse(event.data);
  
  switch(data.type) {
    case 'agent':
      console.log(`Agent: ${data.agent}`);
      break;
    case 'content': 
      console.log(`Content: ${data.content}`);
      break;
    case 'done':
      console.log('Message complete:', data.messageId);
      eventSource.close();
      break;
  }
};
```

## 📂 Workspace Integration

### **Browse Workspace**
```http
GET /workspace/browse?path={relativePath}
```

**Parameters:**
- `path` (string, optional) - Relative path from workspace root

**Response:**
```json
{
  "currentPath": "projects/my-project",
  "items": [
    {
      "name": "README.md",
      "type": "file", 
      "size": 1420,
      "lastModified": "2026-04-21T12:00:00Z"
    },
    {
      "name": "src",
      "type": "directory",
      "size": null,
      "lastModified": "2026-04-21T11:30:00Z"
    }
  ]
}
```

### **Get File Content**
```http
GET /workspace/file?path={relativePath}
```

**Response:** 
- **Text files**: `Content-Type: text/plain`
- **Binary files**: `Content-Type: application/octet-stream`

### **Upload File**
```http
POST /workspace/upload
Content-Type: multipart/form-data

file: [file content]
path: "projects/my-project/document.pdf"
```

## 📈 Analytics & Usage

### **Token Usage by Chat**
```http
GET /chats/{chatId}/usage
```

**Response:**
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

### **Project Statistics**
```http
GET /projects/{slug}/stats
```

**Response:**
```json
{
  "totalChats": 15,
  "totalMessages": 342,
  "totalInputTokens": 45680,
  "totalOutputTokens": 32190,
  "estimatedCostUsd": 12.45,
  "mostActiveAgent": "cody",
  "agentBreakdown": {
    "cody": 156,
    "ade": 98,  
    "paige": 43,
    "debbie": 25
  }
}
```

## 🔧 Configuration Endpoints

### **Get System Info**
```http
GET /system/info
```

**Response:**
```json
{
  "version": "1.0.0",
  "environment": "development", 
  "workspaceRoot": "/home/user/sharpclaw-workspace",
  "availableAgents": ["ade", "cody", "debbie", "noah", "remy", "paige", "fin", "myles"],
  "llmBackends": ["anthropic", "openai", "openrouter", "copilot"]
}
```

### **Health Check**
```http
GET /health
```

**Response:**
```json
{
  "status": "healthy",
  "timestamp": "2026-04-21T15:30:00Z",
  "checks": {
    "fileSystem": "healthy",
    "llmServices": "healthy",
    "workspace": "healthy"
  }
}
```

## 🛡️ Error Handling

### **Standard Error Format**
```json
{
  "error": "ValidationError",
  "message": "Project name is required",
  "details": {
    "field": "name",
    "code": "REQUIRED"
  }
}
```

### **HTTP Status Codes**

| Code | Meaning | Common Scenarios |
|------|---------|------------------|
| `200` | Success | Successful request |
| `201` | Created | Project/chat created |
| `400` | Bad Request | Invalid input, validation errors |
| `404` | Not Found | Project/chat doesn't exist |
| `409` | Conflict | Project/chat already exists |
| `500` | Server Error | File system errors, LLM service failures |

## 🔒 Security Considerations

### **Path Validation**
All file operations validate paths to prevent directory traversal:
```csharp
var safePath = Path.GetFullPath(Path.Combine(workspaceRoot, userPath));
if (!safePath.StartsWith(workspaceRoot))
    throw new SecurityException("Invalid path");
```

### **CORS Configuration**
```csharp
app.UseCors(policy => policy
    .WithOrigins("http://localhost:3000")  // React dev server
    .AllowAnyMethod()
    .AllowAnyHeader()
    .AllowCredentials());
```

### **File Upload Restrictions**
- **Size limit**: 10MB per file
- **Type validation**: Based on file extension and content-type
- **Path restrictions**: Cannot upload outside workspace

## 🚀 Performance Optimizations

### **Streaming Benefits**
- **Memory efficient**: Messages streamed as generated, not buffered
- **Real-time UX**: Users see responses immediately
- **Connection management**: Automatic cleanup of disconnected clients

### **File System Caching**
- **Directory metadata**: Cache `DirectoryInfo` for project listings
- **Message counts**: Parse JSON structure without full deserialization
- **Agent definitions**: Cache parsed agent configuration files

### **Rate Limiting** (Recommended)
```csharp
// Add to production deployments
services.AddRateLimiter(options => {
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
        httpContext => RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.User.Identity?.Name ?? httpContext.Connection.RemoteIpAddress?.ToString(),
            factory: partition => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1)
            }));
});
```