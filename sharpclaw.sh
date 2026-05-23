#!/usr/bin/env bash

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PID_FILE="${ROOT_DIR}/.sharpclaw.pid"
WATCHER_PID_FILE="${ROOT_DIR}/.sharpclaw-watcher.pid"
LOG_FILE="${ROOT_DIR}/.sharpclaw.log"
COMPOSE_FILE="${ROOT_DIR}/docker/docker-compose.yml"
GRAFANA_EXPLORE_LEFT="%7B%22datasource%22%3A%22Loki%22%2C%22queries%22%3A%5B%7B%22refId%22%3A%22A%22%2C%22expr%22%3A%22%7Bservice_name%3D%5C%22SharpClaw%5C%22%7D%22%7D%5D%2C%22range%22%3A%7B%22from%22%3A%22now-1h%22%2C%22to%22%3A%22now%22%7D%7D"
GRAFANA_URL="http://localhost:3000/explore?orgId=1&left=${GRAFANA_EXPLORE_LEFT}"

resolve_workspace_path() {
  if [[ -n "${SHARPCLAW_WORKSPACE_PATH:-}" ]]; then
    echo "${SHARPCLAW_WORKSPACE_PATH}"
    return 0
  fi

  local config_paths=(
    "${ROOT_DIR}/SharpClaw/appsettings.Development.json"
    "${ROOT_DIR}/SharpClaw/appsettings.json"
  )

  local config_path
  for config_path in "${config_paths[@]}"; do
    [[ -f "${config_path}" ]] || continue

    if command -v jq >/dev/null 2>&1; then
      local value
      value="$(jq -r '.SharpClaw.WorkspacePath // ""' "${config_path}" 2>/dev/null || true)"
      if [[ -n "${value}" ]]; then
        echo "${value}"
        return 0
      fi
    else
      local line
      line="$(grep -m1 '"WorkspacePath"' "${config_path}" || true)"
      if [[ -n "${line}" ]]; then
        local parsed
        parsed="$(sed -E 's/.*"WorkspacePath"[[:space:]]*:[[:space:]]*"([^"]*)".*/\1/' <<<"${line}")"
        if [[ -n "${parsed}" ]]; then
          echo "${parsed}"
          return 0
        fi
      fi
    fi
  done

  return 1
}

resolve_project() {
  if [[ -f "${ROOT_DIR}/SharpClaw/SharpClaw.csproj" ]]; then
    echo "SharpClaw/SharpClaw.csproj"
    return 0
  fi

  if [[ -f "${ROOT_DIR}/src/SharpClaw/SharpClaw.csproj" ]]; then
    echo "src/SharpClaw/SharpClaw.csproj"
    return 0
  fi

  echo "Unable to find SharpClaw project file." >&2
  return 1
}

start_stack() {
  echo "Starting Grafana stack with Docker Compose..."
  docker compose -f "${COMPOSE_FILE}" up -d
}

stop_stack() {
  echo "Stopping Grafana stack with Docker Compose..."
  docker compose -f "${COMPOSE_FILE}" down
}

compose_available() {
  command -v docker >/dev/null 2>&1 && docker compose version >/dev/null 2>&1
}

is_service_running() {
  [[ -f "${PID_FILE}" ]] || return 1

  local pid
  pid="$(cat "${PID_FILE}")"
  [[ -n "${pid}" ]] || return 1

  kill -0 "${pid}" 2>/dev/null
}

start_service() {
  local project
  project="$(resolve_project)"

  if is_service_running; then
    echo "SharpClaw service is already running (PID $(cat "${PID_FILE}"))."
    return 0
  fi

  if [[ -f "${PID_FILE}" ]]; then
    rm -f "${PID_FILE}"
  fi

  # Clean up any stale restart signal
  rm -f "${ROOT_DIR}/.sharpclaw.restart"

  echo "Starting SharpClaw service..."
  (
    cd "${ROOT_DIR}"
    nohup dotnet run --project "${project}" >"${LOG_FILE}" 2>&1 &
    echo $! >"${PID_FILE}"
  )

  echo "SharpClaw started (PID $(cat "${PID_FILE}")). Logs: ${LOG_FILE}"

  # Start the restart watcher
  start_watcher
}

start_watcher() {
  # Kill existing watcher if running
  if [[ -f "${WATCHER_PID_FILE}" ]]; then
    local old_pid
    old_pid="$(cat "${WATCHER_PID_FILE}")"
    if [[ -n "${old_pid}" ]] && kill -0 "${old_pid}" 2>/dev/null; then
      kill "${old_pid}" 2>/dev/null || true
    fi
    rm -f "${WATCHER_PID_FILE}"
  fi

  nohup bash -c '
    ROOT_DIR="'"${ROOT_DIR}"'"
    PID_FILE="'"${PID_FILE}"'"
    LOG_FILE="'"${LOG_FILE}"'"
    SIGNAL_FILE="${ROOT_DIR}/.sharpclaw.restart"
    PROJECT="'"$(resolve_project)"'"

    while true; do
      sleep 2
      [[ -f "${SIGNAL_FILE}" ]] || continue

      rm -f "${SIGNAL_FILE}"
      echo "[watcher] Restart signal detected. Rebuilding..." >> "${LOG_FILE}"

      # Build first
      cd "${ROOT_DIR}"
      if ! dotnet build --project "${PROJECT}" --nologo -q >> "${LOG_FILE}" 2>&1; then
        echo "[watcher] Build failed — restart aborted." >> "${LOG_FILE}"
        continue
      fi

      # Stop current process
      if [[ -f "${PID_FILE}" ]]; then
        pid="$(cat "${PID_FILE}")"
        if [[ -n "${pid}" ]] && kill -0 "${pid}" 2>/dev/null; then
          kill "${pid}" 2>/dev/null || true
          for i in {1..20}; do
            kill -0 "${pid}" 2>/dev/null || break
            sleep 0.25
          done
          kill -0 "${pid}" 2>/dev/null && kill -9 "${pid}" 2>/dev/null || true
        fi
        rm -f "${PID_FILE}"
      fi

      # Start new process
      echo "[watcher] Starting SharpClaw..." >> "${LOG_FILE}"
      cd "${ROOT_DIR}"
      nohup dotnet run --project "${PROJECT}" >>"${LOG_FILE}" 2>&1 &
      echo $! > "${PID_FILE}"
      echo "[watcher] SharpClaw restarted (PID $(cat "${PID_FILE}"))." >> "${LOG_FILE}"
    done
  ' >/dev/null 2>&1 &
  echo $! >"${WATCHER_PID_FILE}"
}

stop_service() {
  # Stop the watcher first
  stop_watcher

  if ! is_service_running; then
    echo "SharpClaw service is not running."
    rm -f "${PID_FILE}"
    return 0
  fi

  local pid
  pid="$(cat "${PID_FILE}")"

  echo "Stopping SharpClaw service (PID ${pid})..."
  kill "${pid}" 2>/dev/null || true

  local i
  for i in {1..20}; do
    if kill -0 "${pid}" 2>/dev/null; then
      sleep 0.25
      continue
    fi

    rm -f "${PID_FILE}"
    echo "SharpClaw service stopped."
    return 0
  done

  echo "Process did not stop in time; forcing shutdown..."
  kill -9 "${pid}" 2>/dev/null || true
  rm -f "${PID_FILE}"
  echo "SharpClaw service stopped."
}

stop_watcher() {
  if [[ -f "${WATCHER_PID_FILE}" ]]; then
    local watcher_pid
    watcher_pid="$(cat "${WATCHER_PID_FILE}")"
    if [[ -n "${watcher_pid}" ]] && kill -0 "${watcher_pid}" 2>/dev/null; then
      kill "${watcher_pid}" 2>/dev/null || true
    fi
    rm -f "${WATCHER_PID_FILE}"
  fi
  rm -f "${ROOT_DIR}/.sharpclaw.restart"
}

start_all() {
  start_stack
  start_service
}

stop_all() {
  stop_service
  stop_stack
}

restart_all() {
  stop_all
  start_all
}

restart_service() {
  stop_service
  start_service
}

show_status() {
  if is_service_running; then
    echo "SharpClaw service: running (PID $(cat "${PID_FILE}"))"
  else
    echo "SharpClaw service: stopped"
  fi

  if ! compose_available; then
    echo "Grafana stack: unknown (docker compose not available)"
    return 0
  fi

  local running_services
  running_services="$(docker compose -f "${COMPOSE_FILE}" ps --status running --services 2>/dev/null || true)"

  if [[ -n "${running_services}" ]]; then
    echo "Grafana stack: running"
    echo "Running services:"
    while IFS= read -r service; do
      [[ -n "${service}" ]] && echo "  - ${service}"
    done <<<"${running_services}"
  else
    echo "Grafana stack: stopped"
  fi
}

show_logs() {
  local target="${1:-ui}"

  case "${target}" in
    ui)
      echo "Opening Grafana logs UI: ${GRAFANA_URL}"
      if command -v xdg-open >/dev/null 2>&1; then
        xdg-open "${GRAFANA_URL}" >/dev/null 2>&1 || true
      elif command -v open >/dev/null 2>&1; then
        open "${GRAFANA_URL}" >/dev/null 2>&1 || true
      else
        echo "No browser opener command found. Open this URL manually: ${GRAFANA_URL}"
      fi
      ;;
    service)
      if [[ -f "${LOG_FILE}" ]]; then
        echo "Showing SharpClaw service logs (${LOG_FILE})..."
        tail -n 200 "${LOG_FILE}"
      else
        echo "No SharpClaw service log file found at ${LOG_FILE}."
      fi
      ;;
    *)
      echo "Unknown logs target: ${target}"
      echo "Valid targets: ui, service"
      return 1
      ;;
  esac
}

run_tests() {
  echo "Running tests..."
  (
    cd "${ROOT_DIR}"
    dotnet test
  )
}

view_docs() {
  echo "Starting docs dev server on http://localhost:3001 ..."
  (
    cd "${ROOT_DIR}/docs"
    if command -v yarn >/dev/null 2>&1; then
      yarn start --port 3001
    else
      npm run start -- --port 3001
    fi
  )
}

web_dev() {
  echo "Starting web UI dev server on http://localhost:5173 (proxying API to :5100)..."
  (
    cd "${ROOT_DIR}/SharpClaw.Web"
    npm run dev -- --host
  )
}

web_build() {
  echo "Building web UI production bundle to SharpClaw/wwwroot/..."
  (
    cd "${ROOT_DIR}/SharpClaw.Web"
    npm run build
  )
}

show_transcript_diagnostics() {
  local agent="${1:-}"
  shift || true

  local workspace_override=""
  local session_id=""
  local browser_focus="false"

  while [[ $# -gt 0 ]]; do
    case "$1" in
      --session|-s)
        shift
        if [[ $# -eq 0 || -z "${1:-}" ]]; then
          echo "Missing value for --session"
          return 1
        fi
        session_id="$1"
        ;;
      --help|-h)
        echo "Usage: ./sharpclaw.sh transcript <agent> [workspace_path] [--session <session_id>] [--browser]"
        return 0
        ;;
      --browser|-b)
        browser_focus="true"
        ;;
      *)
        if [[ -z "${workspace_override}" ]]; then
          workspace_override="$1"
        else
          echo "Unexpected argument: $1"
          echo "Usage: ./sharpclaw.sh transcript <agent> [workspace_path] [--session <session_id>] [--browser]"
          return 1
        fi
        ;;
    esac
    shift
  done

  if [[ -z "${agent}" ]]; then
    echo "Usage: ./sharpclaw.sh transcript <agent> [workspace_path] [--session <session_id>] [--browser]"
    return 1
  fi

  if ! command -v jq >/dev/null 2>&1; then
    echo "The transcript command requires 'jq'. Install it and retry."
    return 1
  fi

  local workspace_path
  if [[ -n "${workspace_override}" ]]; then
    workspace_path="${workspace_override}"
  else
    workspace_path="$(resolve_workspace_path || true)"
  fi

  if [[ -z "${workspace_path}" ]]; then
    echo "Could not resolve workspace path."
    echo "Pass it explicitly: ./sharpclaw.sh transcript <agent> /path/to/workspace"
    echo "or set SHARPCLAW_WORKSPACE_PATH."
    return 1
  fi

  local sessions_dir="${workspace_path}/${agent}/sessions"
  if [[ ! -d "${sessions_dir}" ]]; then
    echo "No transcript directory found for agent '${agent}' at ${sessions_dir}"
    return 1
  fi

  local files=()
  while IFS= read -r file; do
    files+=("${file}")
  done < <(find "${sessions_dir}" -type f -name '*.transcript.jsonl' | sort)

  if [[ ${#files[@]} -eq 0 ]]; then
    echo "No transcript files found in ${sessions_dir}"
    return 0
  fi

  echo "Workspace: ${workspace_path}"
  echo "Agent: ${agent}"
  if [[ -n "${session_id}" ]]; then
    echo "Session filter: ${session_id}"
  fi
  if [[ "${browser_focus}" == "true" ]]; then
    echo "Browser diagnostics: enabled"
  fi
  echo "Transcript files: ${#files[@]}"
  echo

  if [[ -n "${session_id}" ]]; then
    if [[ "${session_id}" == *"/"* ]]; then
      echo "Session id must not contain '/'."
      return 1
    fi

    local session_file="${sessions_dir}/${session_id}.transcript.jsonl"
    if [[ ! -f "${session_file}" ]]; then
      echo "No transcript file found for session '${session_id}' at ${session_file}"
      return 1
    fi

    files=("${session_file}")
    echo "Using only ${session_file}"
    echo
  fi

  echo "Largest turns (top 10):"
  cat "${files[@]}" \
    | jq -r '[.timestampUtc, .sessionId, .turnType, (.content|length)] | @tsv' \
    | sort -k4,4nr \
    | head -n 10
  echo

  echo "Session summary (counts, max payload, avg duration):"
  cat "${files[@]}" \
    | jq -s '
        group_by(.sessionId)
        | map({
            sessionId: .[0].sessionId,
            requests: (map(select(.turnType == "request")) | length),
            responses: (map(select(.turnType == "response")) | length),
            maxContentLength: (map(.content | length) | max),
            avgDurationMs: ([.[] | select(.durationMs != null) | .durationMs] | if length == 0 then 0 else (add / length) end)
          })
        | sort_by(.maxContentLength)
        | reverse
      '
  echo

  echo "Latest timeline (last 20 turns):"
  cat "${files[@]}" \
    | jq -r '[.timestampUtc, .sessionId, .turnType, (.content|length), (.durationMs // 0)] | @tsv' \
    | sort -k1,1 \
    | tail -n 20

  if [[ -n "${session_id}" ]]; then
    echo
    echo "Session deep dump (content preview):"
    cat "${files[@]}" \
      | jq -r '"\(.timestampUtc) [\(.turnType)] len=\(.content|length) dur=\(.durationMs // 0)" + "\n" + (.content | gsub("\\n"; " ") | .[0:220]) + "\n"' \
      | sed '/^$/N;/^\n$/D'
  fi

  if [[ "${browser_focus}" == "true" ]]; then
    echo
    echo "Browser MCP signal summary:"
    cat "${files[@]}" \
      | jq -s '
          {
            totalTurns: length,
            browserToolMentions: (map(select(.content | test("browser_[a-z_]+"; "i"))) | length),
            leakedBrowserInvokeTags: (map(select(.turnType == "response" and (.content | test("<invoke name=\\\"browser_"; "i")))) | length),
            browserErrorLikeResponses: (
              map(select(
                .turnType == "response" and
                (.content | test("browser_|playwright|navigate|screenshot|click"; "i")) and
                (.content | test("agent error|failed|unable|could not|cannot|timeout|technical difficulties"; "i"))
              ))
              | length
            ),
            slowBrowserRelatedResponses: (
              map(select(
                .turnType == "response" and
                (.durationMs // 0) > 30000 and
                (.content | test("navigate|browser|playwright|screenshot|click"; "i"))
              ))
              | length
            )
          }
        '

    echo
    echo "Suspicious browser-related turns (latest 20):"
    cat "${files[@]}" \
      | jq -r '
          select(
            .turnType == "response" and (
              (.content | test("<invoke name=\\\"browser_"; "i")) or
              (
                (.content | test("browser_|playwright|navigate|screenshot|click"; "i")) and
                (.content | test("agent error|failed|unable|could not|cannot|timeout|technical difficulties"; "i"))
              ) or
              (
                (.durationMs // 0) > 30000 and
                (.content | test("navigate|browser|playwright|screenshot|click"; "i"))
              )
            )
          )
          | [.timestampUtc, .sessionId, (.durationMs // 0), (.content | length), (.content | gsub("\\n"; " ") | .[0:180])] 
          | @tsv
        ' \
      | sort -k1,1 \
      | tail -n 20
  fi
}

usage() {
  cat <<'EOF'
Usage: ./sharpclaw.sh <command>
Usage: ./sharpclaw.sh logs [ui|service]
Usage: ./sharpclaw.sh transcript <agent> [workspace_path] [--session <session_id>] [--browser]

Commands:
  start-all     Start SharpClaw service and Grafana stack
  stop-all      Stop SharpClaw service and Grafana stack
  restart-all   Restart SharpClaw service and Grafana stack
  start         Start SharpClaw service and Grafana stack
  stop          Stop SharpClaw service and Grafana stack
  restart       Restart SharpClaw service and Grafana stack
  status        Show SharpClaw and Grafana stack status
  logs          Open Grafana logs UI (default target: ui)
  transcript    Show transcript diagnostics for an agent (optional --session filter, --browser)
  test          Run dotnet tests
  docs          Run Docusaurus docs dev server on port 3001
  web           Start web UI dev server (Vite, port 5173, proxies API to :5100)
  web-build     Build web UI production bundle to SharpClaw/wwwroot/
EOF
}

main() {
  if [[ $# -lt 1 || $# -gt 6 ]]; then
    usage
    exit 1
  fi

  case "$1" in
    start-all)
      start_all
      ;;
    stop-all)
      stop_all
      ;;
    restart-all)
      restart_all
      ;;
    start)
      start_service
      ;;
    stop)
      stop_service
      ;;
    restart)
      restart_service
      ;;
    status)
      show_status
      ;;
    logs)
      show_logs "${2:-ui}"
      ;;
    transcript)
      shift
      show_transcript_diagnostics "$@"
      ;;
    test)
      run_tests
      ;;
    docs)
      view_docs
      ;;
    web)
      web_dev
      ;;
    web-build)
      web_build
      ;;
    *)
      usage
      exit 1
      ;;
  esac
}

main "$@"
