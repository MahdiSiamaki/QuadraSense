#!/bin/bash
# Setup script for the QuadraSense cloud environment.
#
# Paste the whole file into the "Setup script" field of the environment at claude.ai/code.
# It runs once, as root on Ubuntu 24.04, before Claude starts, and the result is cached for
# about seven days - so the 240 MB download happens once a week, not once a session.
#
# WHY NOT `apt install dotnet-sdk-10.0`, which would be one line: Canonical builds only the
# 1xx feature band, and global.json asks for 10.0.400. That is not pedantry. The analyzers
# ship inside the SDK, AnalysisLevel is latest-recommended and TreatWarningsAsErrors is on,
# so a different band is a different set of rules - code that builds green in the cloud could
# fail on the laptop, or the reverse. Both places must run the same SDK, so this installs
# Microsoft's build of exactly the version the laptop has.
#
# NETWORK: the installer and the SDK both come from builds.dotnet.microsoft.com, which is not
# on the default Trusted list (dotnet.microsoft.com and dot.net are, as exact hosts). Set the
# environment's network access to Custom, keep the defaults, and add that one host.
#
# DOTNET_SDK must equal "sdk.version" in global.json. Change both in the same commit.

DOTNET_SDK=10.0.400
DOTNET_DIR=/usr/share/dotnet
INSTALLER=https://builds.dotnet.microsoft.com/dotnet/scripts/v1/dotnet-install.sh

log() { echo "[quadrasense-setup] $*"; }

if "$DOTNET_DIR/dotnet" --list-sdks 2>/dev/null | grep -q "^$DOTNET_SDK "; then
  log ".NET SDK $DOTNET_SDK already present"
elif curl -fsSL "$INSTALLER" -o /tmp/dotnet-install.sh \
  && bash /tmp/dotnet-install.sh --version "$DOTNET_SDK" --install-dir "$DOTNET_DIR" --no-path; then
  ln -sf "$DOTNET_DIR/dotnet" /usr/local/bin/dotnet
  log ".NET SDK $DOTNET_SDK installed at $DOTNET_DIR"
else
  log "FAILED to install .NET SDK $DOTNET_SDK."
  log "Most likely builds.dotnet.microsoft.com is not in this environment's allowed domains."
fi

# Always zero. A non-zero exit stops the session from starting at all, and the frontend, the
# docs and the Python tools do not need .NET. The line above says what is wrong; CLAUDE.md
# tells the session to check before it builds.
exit 0
