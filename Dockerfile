# Multi-stage build for the bot host. The image ships NO copyrighted game data
# and NO XOR table — the table is provided at runtime via XOR_TABLE_HEX /
# XOR_TABLE_PATH (BYO, like fiesta-docker). Build context must include the
# FiestaLib-Reloaded submodule (run `git submodule update --init` first).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against the project + submodule project graph first (layer cache).
COPY ik-fiesta-bots.slnx ./
COPY src/Fiesta.Bot/Fiesta.Bot.csproj                 src/Fiesta.Bot/
COPY src/Fiesta.Bot.Host/Fiesta.Bot.Host.csproj       src/Fiesta.Bot.Host/
COPY lib/FiestaLib-Reloaded/src/FiestaLibReloaded.Networking/FiestaLibReloaded.Networking.csproj lib/FiestaLib-Reloaded/src/FiestaLibReloaded.Networking/
COPY lib/FiestaLib-Reloaded/src/FiestaLibReloaded.Config/FiestaLibReloaded.Config.csproj         lib/FiestaLib-Reloaded/src/FiestaLibReloaded.Config/
RUN dotnet restore src/Fiesta.Bot.Host/Fiesta.Bot.Host.csproj

# Build + publish.
COPY . .
RUN dotnet publish src/Fiesta.Bot.Host/Fiesta.Bot.Host.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Fiesta.Bot.Host.dll"]
