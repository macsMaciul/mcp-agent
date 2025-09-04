# Use the official .NET SDK image for build
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /app

# Copy csproj and restore as distinct layers
COPY mcp-agent.csproj ./
RUN dotnet restore

# Copy everything else and build
COPY . ./
RUN dotnet publish -c Release -o out

# Use the official ASP.NET runtime image for runtime
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Copy published output from build image
COPY --from=build /app/out ./

# Expose default port
ENV ASPNETCORE_URLS=http://+:3000
EXPOSE 3000

# Start the application
ENTRYPOINT ["dotnet", "mcp-agent.dll"]
