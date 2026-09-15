@echo off
rem Opens GitKay's commit window (like git gui), for the repository in the current directory.
start "" "%~dp0gitkay.exe" gui %*
