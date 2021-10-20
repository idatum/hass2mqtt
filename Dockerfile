FROM mcr.microsoft.com/dotnet/sdk:5.0-alpine AS build
WORKDIR /app

# Label as build image
LABEL "build"="hass2mqtt"

# copy csproj and restore as distinct layers
COPY *.csproj .
RUN dotnet restore .

# copy everything else and build
COPY ./ ./
WORKDIR /app
RUN dotnet publish -c Release -o out

FROM mcr.microsoft.com/dotnet/aspnet:5.0-alpine AS runtime
RUN addgroup -g 1010 hass2mqtt && \
    adduser -S -u 1010 -G hass2mqtt -s /bin/sh hass2mqtt
WORKDIR /app
COPY --from=build /app/out ./
RUN chown -R hass2mqtt:hass2mqtt /app
USER hass2mqtt
ENTRYPOINT ["dotnet", "hass2mqtt.dll"]
