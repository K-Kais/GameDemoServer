@echo off
echo 🚀 Building and Pushing GameDemo Server...

:: Configuration
set IMAGE_NAME=tranbinh0708/gamedemoserver
set SERVER_HOST=45.32.107.220
set SERVER_PORT=5002
set ASPNETCORE_ENVIRONMENT=ServerDev
set SERVER_PASSWORD=$w8Mjo6x+?3$NJsQ

:: Step 1: Build the Docker image
echo.
echo 🔨 Building Docker image...
docker build -t %IMAGE_NAME%:latest .
if %ERRORLEVEL% neq 0 (
    echo ❌ Failed to build Docker image
    pause
    exit /b 1
)
echo ✅ Docker image built successfully

:: Step 2: Tag with timestamp (optional)
echo.
echo 🏷️  Tagging image with timestamp...
for /f "tokens=2-4 delims=/ " %%a in ('date /t') do (set mydate=%%c%%a%%b)
for /f "tokens=1-2 delims=/: " %%a in ('time /t') do (set mytime=%%a%%b)
set TIMESTAMP=%mydate%-%mytime%
docker tag %IMAGE_NAME%:latest %IMAGE_NAME%:%TIMESTAMP%
echo ✅ Tagged as %IMAGE_NAME%:%TIMESTAMP%

:: Step 3: Push to Docker registry
echo.
echo 📤 Pushing Docker image to registry...
docker push %IMAGE_NAME%:latest
if %ERRORLEVEL% neq 0 (
    echo ❌ Failed to push Docker image
    pause
    exit /b 1
)
docker push %IMAGE_NAME%:%TIMESTAMP%
echo ✅ Docker image pushed successfully

:: Step 4: Deploy to production server
echo.
echo 🌐 Deploying to production server %SERVER_HOST%...
echo Password: %SERVER_PASSWORD%
ssh root@%SERVER_HOST%

