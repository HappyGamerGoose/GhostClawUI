@echo off
powershell -Command "Start-Process powershell -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File C:\Users\akshi\Documents\GhostClawUI\install_msix.ps1' -Verb RunAs -Wait"
