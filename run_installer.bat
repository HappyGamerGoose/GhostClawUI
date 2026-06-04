@echo off
powershell -Command "Start-Process powershell -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File \"%~dp0install_msix.ps1\"' -Verb RunAs -Wait"
