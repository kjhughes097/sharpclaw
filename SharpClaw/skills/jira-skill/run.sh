#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TEMPLATES_DIR="${SCRIPT_DIR}/templates"

# --- Validation ---

check_env() {
    local missing=()
    [[ -z "${JIRA_USER_EMAIL:-}" ]] && missing+=("JIRA_USER_EMAIL")
    [[ -z "${JIRA_API_KEY:-}" ]] && missing+=("JIRA_API_KEY")
    [[ -z "${JIRA_BASE_URL:-}" ]] && missing+=("JIRA_BASE_URL")

    if [[ ${#missing[@]} -gt 0 ]]; then
        echo "ERROR: Missing required environment variables: ${missing[*]}" >&2
        echo "Set JIRA_USER_EMAIL, JIRA_API_KEY, and JIRA_BASE_URL before running this script." >&2
        exit 1
    fi
}

# --- Helpers ---

jira_api() {
    local method="$1"
    local endpoint="$2"
    local data="${3:-}"

    local url="${JIRA_BASE_URL}/rest/api/3${endpoint}"
    local auth
    auth=$(printf '%s:%s' "$JIRA_USER_EMAIL" "$JIRA_API_KEY" | base64 -w0 2>/dev/null || printf '%s:%s' "$JIRA_USER_EMAIL" "$JIRA_API_KEY" | base64)

    local args=(
        -s -w "\n%{http_code}"
        -X "$method"
        -H "Authorization: Basic ${auth}"
        -H "Content-Type: application/json"
        -H "Accept: application/json"
    )

    if [[ -n "$data" ]]; then
        args+=(-d "$data")
    fi

    local response
    response=$(curl "${args[@]}" "$url")

    local http_code
    http_code=$(echo "$response" | tail -1)
    local body
    body=$(echo "$response" | sed '$d')

    if [[ "$http_code" -ge 400 ]]; then
        echo "ERROR: JIRA API returned HTTP ${http_code}" >&2
        echo "$body" | jq -r '.errors // .errorMessages // .' 2>/dev/null || echo "$body" >&2
        exit 1
    fi

    echo "$body"
}

load_template() {
    local project_key="$1"
    local template_file="${TEMPLATES_DIR}/${project_key}.json"

    if [[ -f "$template_file" ]]; then
        cat "$template_file"
    else
        echo "{}"
    fi
}

# --- Commands ---

cmd_create() {
    local project="" summary="" description="" issue_type="" priority=""
    local labels="" components="" assignee="" sprint=""

    while [[ $# -gt 0 ]]; do
        case "$1" in
            --project)  project="$2"; shift 2 ;;
            --summary)  summary="$2"; shift 2 ;;
            --description) description="$2"; shift 2 ;;
            --type)     issue_type="$2"; shift 2 ;;
            --priority) priority="$2"; shift 2 ;;
            --labels)   labels="$2"; shift 2 ;;
            --components) components="$2"; shift 2 ;;
            --assignee) assignee="$2"; shift 2 ;;
            --sprint)   sprint="$2"; shift 2 ;;
            *) echo "ERROR: Unknown flag: $1" >&2; exit 1 ;;
        esac
    done

    if [[ -z "$project" ]]; then
        echo "ERROR: --project is required" >&2; exit 1
    fi
    if [[ -z "$summary" ]]; then
        echo "ERROR: --summary is required" >&2; exit 1
    fi

    # Load template defaults
    local template
    template=$(load_template "$project")

    # Resolve fields (CLI overrides template)
    issue_type="${issue_type:-$(echo "$template" | jq -r '.issue_type // "Task"')}"
    priority="${priority:-$(echo "$template" | jq -r '.priority // empty')}"
    local tpl_labels
    tpl_labels=$(echo "$template" | jq -r '.labels // [] | join(",")')
    labels="${labels:-$tpl_labels}"
    local tpl_components
    tpl_components=$(echo "$template" | jq -r '.components // [] | join(",")')
    components="${components:-$tpl_components}"

    # Build JSON payload
    local payload
    payload=$(jq -n \
        --arg project "$project" \
        --arg summary "$summary" \
        --arg issuetype "$issue_type" \
        '{
            fields: {
                project: { key: $project },
                summary: $summary,
                issuetype: { name: $issuetype }
            }
        }')

    # Add description (as ADF)
    if [[ -n "$description" ]]; then
        payload=$(echo "$payload" | jq --arg desc "$description" \
            '.fields.description = {
                type: "doc",
                version: 1,
                content: [{
                    type: "paragraph",
                    content: [{
                        type: "text",
                        text: $desc
                    }]
                }]
            }')
    fi

    # Add priority
    if [[ -n "$priority" ]]; then
        payload=$(echo "$payload" | jq --arg p "$priority" '.fields.priority = { name: $p }')
    fi

    # Add labels
    if [[ -n "$labels" ]]; then
        payload=$(echo "$payload" | jq --arg l "$labels" '.fields.labels = ($l | split(",") | map(gsub("^\\s+|\\s+$"; "")))')
    fi

    # Add components
    if [[ -n "$components" ]]; then
        payload=$(echo "$payload" | jq --arg c "$components" '.fields.components = ($c | split(",") | map(gsub("^\\s+|\\s+$"; "")) | map({ name: . }))')
    fi

    # Add assignee
    if [[ -n "$assignee" ]]; then
        payload=$(echo "$payload" | jq --arg a "$assignee" '.fields.assignee = { id: $a }')
    fi

    # Add custom fields from template
    local custom_fields
    custom_fields=$(echo "$template" | jq -r '.custom_fields // {}')
    if [[ "$custom_fields" != "{}" ]]; then
        payload=$(echo "$payload" | jq --argjson cf "$custom_fields" '.fields += $cf')
    fi

    # Create the issue
    local result
    result=$(jira_api POST "/issue" "$payload")

    local key
    key=$(echo "$result" | jq -r '.key')
    local id
    id=$(echo "$result" | jq -r '.id')

    # Add to sprint if specified
    if [[ -n "$sprint" ]]; then
        local sprint_payload
        sprint_payload=$(jq -n --arg id "$id" '{ issues: [$id] }')
        jira_api POST "/sprint/${sprint}/issue" "$sprint_payload" >/dev/null 2>&1 || \
            echo "WARNING: Could not add issue to sprint ${sprint}. You may need to use the Agile API." >&2
    fi

    echo "✅ Created ticket: **${key}**"
    echo ""
    echo "- **Summary:** ${summary}"
    echo "- **Type:** ${issue_type}"
    echo "- **Project:** ${project}"
    [[ -n "$priority" ]] && echo "- **Priority:** ${priority}"
    [[ -n "$labels" ]] && echo "- **Labels:** ${labels}"
    echo "- **URL:** ${JIRA_BASE_URL}/browse/${key}"
}

cmd_fetch() {
    local project="" sprint="" assignee="" label="" status="" max="50"

    while [[ $# -gt 0 ]]; do
        case "$1" in
            --project)  project="$2"; shift 2 ;;
            --sprint)   sprint="$2"; shift 2 ;;
            --assignee) assignee="$2"; shift 2 ;;
            --label)    label="$2"; shift 2 ;;
            --status)   status="$2"; shift 2 ;;
            --max)      max="$2"; shift 2 ;;
            *) echo "ERROR: Unknown flag: $1" >&2; exit 1 ;;
        esac
    done

    if [[ -z "$project" ]]; then
        echo "ERROR: --project is required" >&2; exit 1
    fi

    # Build JQL query
    local jql="project = \"${project}\""

    if [[ -n "$sprint" ]]; then
        jql+=" AND sprint = \"${sprint}\""
    else
        jql+=" AND sprint in openSprints()"
    fi

    if [[ -n "$assignee" ]]; then
        jql+=" AND assignee = \"${assignee}\""
    fi

    if [[ -n "$label" ]]; then
        jql+=" AND labels = \"${label}\""
    fi

    if [[ -n "$status" ]]; then
        jql+=" AND status = \"${status}\""
    fi

    jql+=" ORDER BY priority DESC, created DESC"

    # URL-encode and fetch
    local encoded_jql
    encoded_jql=$(printf '%s' "$jql" | jq -sRr @uri)

    local result
    result=$(jira_api GET "/search?jql=${encoded_jql}&maxResults=${max}&fields=summary,status,assignee,priority,labels")

    local total
    total=$(echo "$result" | jq -r '.total')
    local returned
    returned=$(echo "$result" | jq -r '.issues | length')

    if [[ "$returned" -eq 0 ]]; then
        echo "No tickets found matching query."
        echo ""
        echo "**JQL:** \`${jql}\`"
        return 0
    fi

    echo "## Tickets in ${project} (${returned} of ${total})"
    echo ""
    echo "| Key | Summary | Status | Assignee | Priority | Labels |"
    echo "|-----|---------|--------|----------|----------|--------|"

    echo "$result" | jq -r '.issues[] |
        "| " +
        .key + " | " +
        (.fields.summary // "-") + " | " +
        (.fields.status.name // "-") + " | " +
        (.fields.assignee.displayName // "Unassigned") + " | " +
        (.fields.priority.name // "-") + " | " +
        ((.fields.labels // []) | join(", ")) + " |"'

    echo ""
    echo "**JQL:** \`${jql}\`"
}

# --- Main ---

usage() {
    echo "Usage: $0 <command> [options]"
    echo ""
    echo "Commands:"
    echo "  create    Create a JIRA ticket"
    echo "  fetch     Fetch JIRA tickets"
    echo ""
    echo "Run '$0 <command> --help' for command-specific options."
    echo ""
    echo "Required environment variables:"
    echo "  JIRA_USER_EMAIL  - Your JIRA email address"
    echo "  JIRA_API_KEY     - Your JIRA API token"
    echo "  JIRA_BASE_URL    - Your JIRA instance URL"
}

if [[ $# -lt 1 ]]; then
    usage
    exit 1
fi

COMMAND="$1"
shift

check_env

case "$COMMAND" in
    create) cmd_create "$@" ;;
    fetch)  cmd_fetch "$@" ;;
    -h|--help|help) usage ;;
    *) echo "ERROR: Unknown command: ${COMMAND}" >&2; usage; exit 1 ;;
esac
