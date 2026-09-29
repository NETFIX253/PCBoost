@echo off
rem Démarre l'agent de développement PCBoost (outil développeur). Fermez la fenêtre pour l'arrêter.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0dev-agent.ps1"
