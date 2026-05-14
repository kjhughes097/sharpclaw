#!/usr/bin/env bash

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PID_FILE="${ROOT_DIR}/.sharpclaw.pid"
LOG_FILE="${ROOT_DIR}/.sharpclaw.log"
COMPOSE_FILE="${ROOT_DIR}/docker/docker-compose.yml"
GRAFANA_EXPLORE_LEFT="%7B%22datasource%22%3A%22Loki%22%2C%22queries%22%3A%5B%7B%22refId%22%3A%22A%22%2C%22expr%22%3A%22%7Bservice_name%3D%5C%22SharpClaw%5C%22%7D%22%7D%5D%2C%22range%22%3A%7B%22from%22%3A%22now-1h%22%2C%22to%22%3A%22now%22%7D%7D"
GRAFANA_URL="http://localhost:3000/explore?orgId=1&left=${GRAFANA_EXPLORE_LEFT}"

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

  echo "Starting SharpClaw service..."
  (
    cd "${ROOT_DIR}"
    nohup dotnet run --project "${project}" >"${LOG_FILE}" 2>&1 &
    echo $! >"${PID_FILE}"
  )

  echo "SharpClaw started (PID $(cat "${PID_FILE}")). Logs: ${LOG_FILE}"
}

stop_service() {
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

usage() {
  cat <<'EOF'
Usage: ./sharpclaw.sh <command>
Usage: ./sharpclaw.sh logs [ui|service]

Commands:
  start     Start SharpClaw service and Grafana stack
  stop      Stop SharpClaw service and Grafana stack
  restart   Restart SharpClaw service and Grafana stack
  status    Show SharpClaw and Grafana stack status
  logs      Open Grafana logs UI (default target: ui)
  test      Run dotnet tests
  docs      Run Docusaurus docs dev server on port 3001
EOF
}

main() {
  if [[ $# -lt 1 || $# -gt 2 ]]; then
    usage
    exit 1
  fi

  case "$1" in
    start)
      start_all
      ;;
    stop)
      stop_all
      ;;
    restart)
      restart_all
      ;;
    status)
      show_status
      ;;
    logs)
      show_logs "${2:-ui}"
      ;;
    test)
      run_tests
      ;;
    docs)
      view_docs
      ;;
    *)
      usage
      exit 1
      ;;
  esac
}

main "$@"
