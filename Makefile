# MCGalaxy build helpers (survival-support branch)
#
# Convenience wrapper around the .NET SDK for people who would rather type
# `make` than remember the csproj paths. The canonical CI build still uses
# msbuild/mono against MCGalaxy.sln; this Makefile just drives the same
# projects with `dotnet` (and falls back to msbuild for the full solution).
#
#   make            # build the core MCGalaxy library (fast; what the commit hook runs)
#   make cli        # build the CLI server executable
#   make all        # build core + CLI
#   make run        # build then launch the CLI server
#   make clean      # remove build outputs
#   make hooks      # install the auto-build git pre-commit hook
#   make help       # list targets

# Locate a .NET SDK: prefer one on PATH, else the per-user install directory.
DOTNET ?= $(shell command -v dotnet 2>/dev/null || echo $(HOME)/.dotnet/dotnet)
CONFIG ?= Release

CORE_PROJ := MCGalaxy/MCGalaxy_dotnet.csproj
CLI_PROJ  := CLI/MCGalaxyCLI_dotnet8.csproj

export DOTNET_CLI_TELEMETRY_OPTOUT := 1
export DOTNET_NOLOGO := 1

.DEFAULT_GOAL := build
.PHONY: build cli all run clean hooks help

## build: compile the core MCGalaxy library (fast; used by the commit hook)
build:
	$(DOTNET) build $(CORE_PROJ) -c $(CONFIG) -v minimal

## cli: compile the CLI server executable
cli:
	$(DOTNET) build $(CLI_PROJ) -c $(CONFIG) -v minimal

## all: compile the core library and the CLI server
all: build cli

## run: build then launch the CLI server
run: cli
	$(DOTNET) CLI/bin/$(CONFIG)/net8.0/MCGalaxyCLI.dll

## clean: remove build outputs (bin/ and obj/)
clean:
	rm -rf MCGalaxy/bin MCGalaxy/obj CLI/bin CLI/obj GUI/bin GUI/obj

## hooks: install the auto-build git pre-commit hook (core.hooksPath = .githooks)
hooks:
	git config core.hooksPath .githooks
	@echo "Installed git hooks (core.hooksPath = .githooks)."
	@echo "Each commit now builds $(CORE_PROJ) and is aborted if the build fails."
	@echo "Bypass a single commit with: git commit --no-verify"

## help: list available targets
help:
	@grep -E '^## ' $(MAKEFILE_LIST) | sed 's/^## //'
