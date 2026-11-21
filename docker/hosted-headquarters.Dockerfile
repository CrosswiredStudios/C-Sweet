FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble
RUN apt-get update && apt-get install -y --no-install-recommends git ca-certificates && rm -rf /var/lib/apt/lists/* \
    && install -d -o app -g app /state
WORKDIR /srv
COPY . /srv/
ENV HOME=/state ASPNETCORE_ENVIRONMENT=Production DOTNET_ENVIRONMENT=Production
USER app
ENTRYPOINT ["dotnet"]
CMD ["/srv/CSweet.Api/CSweet.Api.dll"]
