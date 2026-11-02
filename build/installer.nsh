; electron-builder v26 invokes these before removing the previous application's
; files, and after extracting the new files. Default launch uses ExecShellAsUser.
!macro customInit
  StrCpy $INSTDIR "$PROGRAMFILES64\Finley"
  InitPluginsDir
  File /oname=$PLUGINSDIR\finley-service-bootstrap.exe "${PROJECT_DIR}\bin\finley-tunnel-service.exe"
  nsExec::ExecToStack '"$PLUGINSDIR\finley-service-bootstrap.exe" --prepare-install'
  Pop $0
  Pop $1
  ${If} $0 != 0
    MessageBox MB_ICONSTOP|MB_OK "Finley cannot safely use its installation directory.$\r$\n$1"
    SetErrorLevel 1
    Abort
  ${EndIf}
!macroend

!macro customInstall
  nsExec::ExecToStack '"$INSTDIR\resources\bin\finley-tunnel-service.exe" --install'
  Pop $0
  Pop $1
  ${If} $0 != 0
    MessageBox MB_ICONSTOP|MB_OK "Finley Tunnel Service could not be installed or started.$\r$\n$1$\r$\nInstallation is incomplete. Run this installer again as administrator to repair it."
    SetErrorLevel 1
    Abort
  ${EndIf}
!macroend

!macro customUnInstall
  ${If} ${FileExists} "$INSTDIR\resources\bin\finley-tunnel-service.exe"
    nsExec::ExecToStack '"$INSTDIR\resources\bin\finley-tunnel-service.exe" --uninstall'
    Pop $0
    Pop $1
    ${If} $0 != 0
      MessageBox MB_ICONSTOP|MB_OK "Finley Tunnel Service could not be stopped or removed.$\r$\n$1$\r$\nFiles have been retained. Repair Finley before trying again."
      SetErrorLevel 1
      Abort
    ${EndIf}
  ${EndIf}
!macroend
