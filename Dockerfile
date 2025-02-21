# Use the official .NET SDK image as the base image for building
FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src

# Copy csproj and restore dependencies
COPY ["EchoBot.csproj", "./"]
RUN dotnet restore "EchoBot.csproj"

# Copy the rest of the code
COPY . .

# Build the application
RUN dotnet build "EchoBot.csproj" -c Release -o /app/build

# Publish the application
FROM build AS publish
RUN dotnet publish "EchoBot.csproj" -c Release -o /app/publish

# Build the runtime image
FROM mcr.microsoft.com/dotnet/aspnet:6.0 AS runtime
WORKDIR /app

# Install required dependencies for Media Services
RUN apt-get update && apt-get install -y \
    libc6 \
    libgcc1 \
    libgssapi-krb5-2 \
    libicu67 \
    libssl1.1 \
    libstdc++6 \
    zlib1g \
    && rm -rf /var/lib/apt/lists/*

# Copy the published application
COPY --from=publish /app/publish .

# Create directory for certificates if needed
RUN mkdir -p /usr/share/dotnet/https/

# Set environment variables
ENV ASPNETCORE_URLS=http://+:8445
ENV DOTNET_RUNNING_IN_CONTAINER=true

# Expose the necessary ports
# 8445 for the bot service
# 17659 for media instance external port
EXPOSE 8445 17659

# Start the application
ENTRYPOINT ["dotnet", "EchoBot.dll"]
