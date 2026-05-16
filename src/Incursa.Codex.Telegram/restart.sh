#!/bin/bash

echo "Restarting Codex Telegram Bot..."

pkill -f "Incursa.Codex.Telegram"

sleep 2

cd ~/projects/codex-telegram || exit

{
    sleep 3
    echo 1
    sleep 2
    echo y
} | dotnet run --project src/Incursa.Codex.Telegram