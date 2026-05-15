#!/usr/bin/env bash

PORTS=(3000 3001 3002 3003 3004 3005 3100 3500 4000)

for port in "${PORTS[@]}"; do
  pids=$(sudo -n ss -lptn 2>/dev/null \
    | grep ":$port " \
    | grep -oP 'pid=\K[0-9]+' \
    | sort -u)

  if [ -z "$pids" ]; then
    echo "Port $port already free"
    continue
  fi

  for pid in $pids; do
    echo "Killing PID $pid on port $port"
    sudo -n kill "$pid" 2>/dev/null || sudo -n kill -9 "$pid"
  done
done
