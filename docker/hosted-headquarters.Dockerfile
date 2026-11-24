FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble
RUN apt-get update && apt-get install -y --no-install-recommends git ca-certificates && rm -rf /var/lib/apt/lists/* \
    && install -d -o app -g app /state
WORKDIR /srv
COPY . /srv/
ENV HOME=/state ASPNETCORE_ENVIRONMENT=Production DOTNET_ENVIRONMENT=Production
USER app
# Existing guest volumes also need the standard Linux application-data directory.
ENTRYPOINT ["/bin/sh", "-ec", "mkdir -p \"$HOME/.local/share\"; exec dotnet \"$@\"", "dotnet"]
CMD ["/srv/CSweet.Api/CSweet.Api.dll"]
