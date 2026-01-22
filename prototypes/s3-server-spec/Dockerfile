FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 9000

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy solution and project files first for layer caching
COPY Kurrent.S3.sln ./
COPY src/Kurrent.S3/Kurrent.S3.csproj src/Kurrent.S3/
COPY src/Kurrent.S3.Tests/Kurrent.S3.Tests.csproj src/Kurrent.S3.Tests/

# Restore dependencies
RUN dotnet restore

# Copy everything else
COPY . .

# Build
WORKDIR /src/src/Kurrent.S3
RUN dotnet build -c Release -o /app/build

FROM build AS publish
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Create data directory
RUN mkdir -p /app/s3-data
VOLUME /app/s3-data

ENV ASPNETCORE_URLS=http://+:9000
ENV S3__StoragePath=/app/s3-data

ENTRYPOINT ["dotnet", "Kurrent.S3.dll"]
