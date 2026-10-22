$inno = "C:\Users\LECOO FIGHTER\AppData\Local\Programs\Inno Setup 6\ISCC.exe"
Write-Output "Publishing win-x64..."
dotnet publish "D:\BIMUP Addin\BIMUP.CDE\src\BIMUP.CDE\BIMUP.CDE.csproj" -c Release -r win-x64 --self-contained true -o "D:\BIMUP Addin\BIMUP.CDE\Installer\Publish"

Write-Output "Building Inno Setup installer..."
& $inno "D:\BIMUP Addin\BIMUP.CDE\Installer\SGC.CDE.iss"

$desktopExe = Join-Path ([Environment]::GetFolderPath('Desktop')) 'SGC.CDE.Studio-Setup-v1.1.0.exe'
Copy-Item "D:\BIMUP Addin\BIMUP.CDE\Installer\Output\SGC.CDE.Studio-Setup-v1.1.0.exe" -Destination $desktopExe -Force
Write-Output "Installer deployed to: $desktopExe"

Write-Output "Silently installing update..."
Start-Process $desktopExe -ArgumentList '/SILENT /FORCECLOSEAPPLICATIONS' -Wait
Write-Output "ALL_DONE_SUCCESSFULLY"
