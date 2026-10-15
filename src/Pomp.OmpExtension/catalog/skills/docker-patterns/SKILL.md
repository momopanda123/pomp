---
name: docker-patterns
description: "Create or review Dockerfiles and Compose stacks: reproducible dev environments, lean non-root images, hardened runtime, debugging order. Use for containerizing, image hardening or container diagnosis, or says /skill:docker-patterns."
---

# Containers without surprises (pomp catalog)

Author or assess Dockerfiles and Compose setups for repeatable local environments, compact unprivileged production images, sane networking and storage, and a brisk diagnostic routine. Pipeline flow and rollout strategy are `skill://deployment-patterns` (whose orchestration section covers cluster targets); cloud account posture is `skill://security-review/cloud-and-infrastructure`.

Skill ids below are catalog ids; this skill works when they are absent — apply the named idea directly.

## 1. Survey first

Survey existing container files, runtime and build tooling, ports, needed services and secrets, and the intended flavour — Linux or Windows. Ask when it matters: Linux containers on Windows sit inside a light VM and prove nothing Windows-specific; Windows containers demand a Windows host plus a base matching its OS build.

## 2. Compose the local stack

1. A single process per service (app, database, cache, plus a mail catcher where mail flows). Gate startup on health rather than mere launch — dependents await readiness instead of racing and crashing.
2. Persist state in named volumes; bind-mount only sources under edit, and shield container-built directories from host shadowing.
3. Confine developer comforts (debug ports, chatty logging, source mounts) to an override file beside an explicit production file, so conveniences never seep into production definitions; both environments share one Dockerfile via build targets.

## 3. Bake lean, unprivileged images

Stage the build: restore, compile, then assemble a minimal runtime stage. Copy dependency manifests ahead of sources to cache the dependency layer — for .NET, restore in its own layer from the project files first, publish Release, and ship on the slimmer ASP.NET or runtime base. Pin base tags (digests for strict repeatability); run as non-root (current official images bundle an app user; distroless-style "chiseled" runtimes narrow the target further); embed a health check; keep version control, env files, build artefacts, tests, and docs out via the ignore file (Visual Studio Container Tools drafts Dockerfiles, Compose files, and ignore files, and debug inside containers — yet debug mode may compile a partial Dockerfile, so green debugging never certifies the release image).

## 4. Network, storage, lockdown

1. Address services by name; isolate front and back networks so the public tier cannot see the database; publish the minimum ports; loopback-bind developer database ports and publish nothing in production.
2. Lock down the runtime: no privilege escalation, capabilities dropped then selectively restored, read-only root with tmpfs scratch, resource caps, a restart policy.
3. Banish secrets from layers (pullable images are readable images), build args, and Compose files; supply them at runtime or through secret mounts. Build-time credentials travel the builder's secret mounts. Scanners sweep images for known-vulnerable packages.

## 5. Commands, traps, diagnosis

Container commands run through bash only with approval; forewarn whenever an image pull (network, bulk) looms. Volume-removing and pruning commands need prior consent — both destroy. Mind the traps: CRLF-saved shell scripts die inside Linux containers (pin LF via gitattributes); Windows-host bind-mount watchers lag or misfire — favour in-container watchers or polling.

Diagnose in order: logs, container state and health, an interactive shell, inter-service name resolution and connectivity, resource pressure, and finally a clean rebuild.

Finished when: a clean checkout builds the image; the container starts healthy as non-root; one command raises the stack in order; layers and tracked files hide no secrets; the ignore file bars sensitive and bulky paths; only documented ports show. Report the changes, start/stop steps, and which commands destroy.
