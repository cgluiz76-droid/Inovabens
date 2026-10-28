@echo off
:: Abre o PowerShell, navega até a pasta e executa o dotnet run
start powershell -NoExit -Command "cd 'C:\Claude\inovabens\dotnet\src\Inovabens.Web'; dotnet run"

:: Aguarda 3 segundos para dar tempo do servidor .NET começar a subir
timeout /t 3 /nobreak >nul

:: Abre o Google Chrome no endereço especificado
start chrome "http://localhost:5051"