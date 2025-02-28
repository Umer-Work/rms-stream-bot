# Use the official .NET SDK image as the base image for building
FROM mcr.microsoft.com/dotnet/sdk:6.0-windowsservercore-ltsc2022 AS build

ARG CallSignalingPort=9442
ARG CallSignalingPort2=9441
ARG InstanceInternalPort=8445

COPY . /src

WORKDIR /src

RUN dotnet build EchoBot.csproj --arch x64 --self-contained --configuration Release --output C:\app

FROM mcr.microsoft.com/windows/server:10.0.20348.2655
SHELL ["powershell", "-Command"]

ADD https://aka.ms/vs/17/release/vc_redist.x64.exe /bot/VC_redist.x64.exe

COPY /scripts/entrypoint.cmd /bot
COPY /scripts/halt_termination.ps1 /bot
COPY --from=build /app /bot

WORKDIR /bot

RUN Set-ExecutionPolicy Bypass -Scope Process -Force; \
    [System.Net.ServicePointManager]::SecurityProtocol = \
        [System.Net.ServicePointManager]::SecurityProtocol -bor 3072; \
        iex ((New-Object System.Net.WebClient).DownloadString('https://chocolatey.org/install.ps1'))

RUN choco install openssl.light -y

EXPOSE $InstanceInternalPort
EXPOSE $CallSignalingPort
EXPOSE $CallSignalingPort2

ENTRYPOINT [ "entrypoint.cmd" ]