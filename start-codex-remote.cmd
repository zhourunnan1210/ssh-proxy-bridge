@echo off
call "%~dp0start-ssh-proxy-bridge.cmd" %*
exit /b %errorlevel%
