#See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:6.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src
COPY ["Worker/MonteCarlo.Worker.csproj", "MonteCarlo/Worker/"]
RUN dotnet restore "MonteCarlo/Worker/MonteCarlo.Worker.csproj"
COPY . .
WORKDIR "/src/MonteCarlo/Worker"
RUN dotnet build "MonteCarlo.Worker.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "MonteCarlo.Worker.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
RUN groupadd --gid 5000 armonikuser && useradd --home-dir /home/armonikuser --create-home --uid 5000 --gid 5000 --shell /bin/sh --skel /dev/null armonikuser
RUN mkdir /cache && chown armonikuser: /cache
USER armonikuser
EXPOSE 1080
ENTRYPOINT ["dotnet", "MonteCarlo.Worker.dll"]

