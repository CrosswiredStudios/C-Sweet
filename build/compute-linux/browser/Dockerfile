FROM mcr.microsoft.com/playwright:v1.64.0-noble@sha256:06a9939e57531807f8d5fd76ce44b53165ffb7d7501d87ab10e285c20b1e971f
COPY package.json package-lock.json /opt/browser/
RUN npm ci --prefix /opt/browser --ignore-scripts --no-audit --no-fund
COPY runner.cjs extract.py /opt/browser/
ENTRYPOINT ["python3", "/opt/browser/extract.py"]
