@echo off
title Smarter Trading Build
rem 本目录（.build）存放全部非模组内容：源码 src\、工程 SmarterTrading.csproj、编译产物 obj\ bin\。
rem 游戏的模组扫描器只跳过「以点开头的文件夹」，所以整个 .build 都不会被当成模组内容。
rem 模组真正需要的文件（manifest.xml、SmarterTrading.dll、Localization\）留在模组根目录，
rem 构建成功后 DeployToMods 会把新 DLL 拷回根目录。
cd /d "%~dp0"

echo ============================================
echo   Smarter Trading - build and deploy
echo ============================================
echo.

dotnet build SmarterTrading.csproj -c Release

echo.
if errorlevel 1 (
    echo [FAILED] Build failed. See errors above.
    echo          If the error is about SmarterTrading.dll being in use,
    echo          close the game first - the DLL is copied into the mod folder.
) else (
    echo [OK] DLL deployed to the mod folder. Restart the game to see changes.
    echo      Note: if you only changed Localization\*.csv or manifest.xml,
    echo      no build needed - just restart the game.
)
echo.
pause
