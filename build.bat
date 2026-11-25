@echo off
setlocal
cd /d "%~dp0"
echo === [1/2] Building FluxCord.Media.dll (C++20 AVX2 Zero-Copy NVENC + WASAPI + NoiseSuppressor) ===
call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
if not exist native\build\build.ninja (
  cmake -S native -B native\build -G Ninja -DCMAKE_BUILD_TYPE=Release || exit /b 1
)
cmake --build native\build || exit /b 1

echo === [2/2] Publishing FluxCord.exe (.NET 10 NativeAOT Direct2D FlipDiscard) ===
dotnet publish src\FluxCord\FluxCord.csproj -c Release -r win-x64 -o dist\FluxCord || exit /b 1
copy /Y native\build\FluxCord.Media.dll dist\FluxCord\FluxCord.Media.dll >nul || exit /b 1
copy /Y native\third_party\libdave\bin\libdave.dll dist\FluxCord\libdave.dll >nul || exit /b 1

echo === DONE: %~dp0dist\FluxCord\FluxCord.exe ===
