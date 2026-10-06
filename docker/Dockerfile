# Build + run environment for the XUL-J WinForms bridge on Linux (Mono's WinForms + virtual X display).
FROM mono:6.12
RUN (apt-get update || (sed -i 's|deb.debian.org|archive.debian.org|g; /security/d' /etc/apt/sources.list && apt-get update)) \
 && apt-get install -y --no-install-recommends xvfb xauth libgdiplus fonts-dejavu-core \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /src
