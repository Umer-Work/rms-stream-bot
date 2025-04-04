@echo off

IF "%1"=="-v" (
    .\EchoBot.exe -v
    exit /b 0
)

:: echo Setup: Debug Mode!!!
:: echo Setup: Sleeping for 10 mins
:: powershell.exe Start-Sleep -Seconds 600

echo Setup: Converting certificate (changed order, doing this before starting VC_redist)
powershell.exe C:\Program` Files\OpenSSL\bin\openssl.exe pkcs12 -export -out C:\bot\certificate.pfx -passout pass: -inkey C:\certs\key.pem -in C:\certs\cert.pem

:: --- Ensure the VC_redist is installed for the Microsoft.Skype.Bots.Media Library ---
:: echo Setup: Starting VC_redist (using powershell)
:: powershell.exe .\VC_redist.x64.exe /quiet /norestart /log Install_vc_redist_2017_x64.log

:: echo Setup: VC_redist log:
:: more Install_vc_redist_2017_x64.log

echo Setup: Installing certificate
dir certificate*
certutil -f -p "" -importpfx certificate.pfx
powershell.exe "(Get-PfxCertificate -FilePath certificate.pfx).Thumbprint" > thumbprint
set /p AppSettings__CertificateThumbprint= < thumbprint
del thumbprint
del certificate.pfx

set /A CallSignalingPort2 = %AppSettings__BotCallingInternalPort% - 1

ECHO %AppSettings__BotCallingInternalPort%
ECHO %AppSettings__MediaInternalPort%
ECHO %CallSignalingPort2%
ECHO %AppSettings__CertificateThumbprint%

:: --- Delete existing certificate bindings and URL ACL registrations ---
echo Setup: Deleting bindings
netsh http delete sslcert ipport=0.0.0.0:%AppSettings__BotCallingInternalPort% > nul
netsh http delete sslcert ipport=0.0.0.0:%AppSettings__MediaInternalPort% > nul
netsh http delete urlacl url=https://+:%AppSettings__BotCallingInternalPort%/ > nul
netsh http delete urlacl url=https://+:%AppSettings__MediaInternalPort%/ > nul
netsh http delete urlacl url=http://+:%CallSignalingPort2%/ > nul

:: --- Add new URL ACLs and certificate bindings ---
echo Setup: Adding bindings
netsh http add urlacl url=https://+:%AppSettings__BotCallingInternalPort%/ sddl=D:(A;;GX;;;S-1-1-0) > nul && ^
netsh http add urlacl url=https://+:%AppSettings__MediaInternalPort%/ sddl=D:(A;;GX;;;S-1-1-0) > nul && ^
netsh http add urlacl url=http://+:%CallSignalingPort2%/ sddl=D:(A;;GX;;;S-1-1-0) > nul && ^
netsh http add sslcert ipport=0.0.0.0:%AppSettings__BotCallingInternalPort% certhash=%AppSettings__CertificateThumbprint% appid=%AppSettings__AadAppId% > nul && ^
netsh http add sslcert ipport=0.0.0.0:%AppSettings__MediaInternalPort% certhash=%AppSettings__CertificateThumbprint% appid=%AppSettings__AadAppId% > nul

if errorlevel 1 (
   echo Setup: Failed to add URL ACLs and certificate bings.
   exit /b %errorlevel%
)

echo Setup: Done
echo ---------------------

:: --- Running bot ---
.\EchoBot.exe