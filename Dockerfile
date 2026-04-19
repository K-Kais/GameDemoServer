FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY ["GameDemoServer.csproj", "./"]
RUN dotnet restore "./GameDemoServer.csproj"

COPY . .
RUN dotnet build "./GameDemoServer.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "./GameDemoServer.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
EXPOSE 8080

COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "GameDemoServer.dll"]
