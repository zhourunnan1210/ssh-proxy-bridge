@echo off
call "%~dp0stop-ssh-proxy-bridge.cmd" %*
exit /b %errorlevel%
