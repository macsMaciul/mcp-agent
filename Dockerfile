# Use the official .NET SDK image for build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

# Copy csproj and restore as distinct layers
COPY ski-agent.csproj ./
RUN dotnet restore

# Copy everything else and build
COPY . ./
RUN dotnet publish -c Release -o out

# Use the official ASP.NET runtime image for runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Copy published output from build image
COPY --from=build /app/out ./

# Expose default port
ENV ASPNETCORE_URLS=http://+:3000
EXPOSE 3000

# Set environment variable for OpenAI API key (can be overridden at runtime)
ENV OPENAI_API_KEY=""

# Start the application
ENTRYPOINT ["dotnet", "ski-agent.dll"]
