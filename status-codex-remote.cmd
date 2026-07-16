@echo off
call "%~dp0status-ssh-proxy-bridge.cmd" %*
exit /b %errorlevel%
