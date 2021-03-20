#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:5.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:5.0 AS build
WORKDIR /src
COPY ["bcc-wp-proxy.csproj", "."]
RUN dotnet restore "./bcc-wp-proxy.csproj"
COPY . .
WORKDIR "/src/."
RUN dotnet build "bcc-wp-proxy.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "bcc-wp-proxy.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "BCC.WPProxy.dll"]