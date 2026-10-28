FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY src/Orencode.Web/Orencode.Web.csproj src/Orencode.Web/
RUN dotnet restore src/Orencode.Web
COPY src/Orencode.Web/ src/Orencode.Web/
RUN dotnet publish src/Orencode.Web -c Release --no-restore -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir /data && chown app:app /data
USER app
ENV ASPNETCORE_URLS=http://+:8080 Database__Path=/data/orencode.db
EXPOSE 8080
ENTRYPOINT ["dotnet", "Orencode.Web.dll"]
