FROM mcr.microsoft.com/dotnet/sdk:8.0.413-bookworm-slim AS build
WORKDIR /src
COPY src/ZoomCheck.Relay.Contracts/ZoomCheck.Relay.Contracts.csproj src/ZoomCheck.Relay.Contracts/
COPY src/ZoomCheck.Relay/ZoomCheck.Relay.csproj src/ZoomCheck.Relay/
RUN dotnet restore src/ZoomCheck.Relay/ZoomCheck.Relay.csproj
COPY src/ZoomCheck.Relay.Contracts/ src/ZoomCheck.Relay.Contracts/
COPY src/ZoomCheck.Relay/ src/ZoomCheck.Relay/
RUN dotnet publish src/ZoomCheck.Relay/ZoomCheck.Relay.csproj -c Release --no-restore -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0.20-bookworm-slim AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=10000
EXPOSE 10000
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "ZoomCheck.Relay.dll"]
