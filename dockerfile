# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /App

# Copy everything into the container
COPY . ./

# Restore dependencies
WORKDIR /App/src/MCC.TestTask
RUN dotnet restore MCC.TestTask.sln

# Publish the main app project
RUN dotnet publish MCC.TestTask.App/MCC.TestTask.App.csproj -c Release -o /App/out

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /App
COPY --from=build /App/out ./

ENTRYPOINT ["dotnet", "MCC.TestTask.App.dll"]